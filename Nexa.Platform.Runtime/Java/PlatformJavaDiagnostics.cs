using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Platform;

public sealed class PlatformJavaDiagnostics : IPlatformJavaDiagnostics
{
    private static readonly SemaphoreSlim Concurrency = new(2);
    public const int MaximumStreamBytes = 64 * 1024;
    public static TimeSpan ProbeTimeout => TimeSpan.FromSeconds(8);
    public async ValueTask<PlatformJavaExecutableIdentity> CaptureIdentityAsync(string executable, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length > 8192 || executable.Any(char.IsControl)
            || !Path.IsPathFullyQualified(executable) || executable.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("Java executable must be an absolute local path.");
        string path = Path.GetFullPath(executable); var info = new FileInfo(path);
        if (!info.Exists || info.Length is < 1 or > 128 * 1024 * 1024) throw new IOException("Java executable identity is unavailable or exceeds its budget.");
        long size = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
        info.Refresh();
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != ticks) throw new IOException("Java executable changed during identity capture.");
        return new(path, size, ticks, hash);
    }

    public async ValueTask<PlatformJavaProbeOutput> ProbeAsync(PlatformJavaExecutableIdentity identity, PlatformJavaProbeKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(kind)) return Empty(PlatformJavaProbeStatus.Rejected);
        if (!await Concurrency.WaitAsync(ProbeTimeout, cancellationToken).ConfigureAwait(false)) return Empty(PlatformJavaProbeStatus.TimedOut);
        try { return await ProbeCoreAsync(identity, kind, cancellationToken).ConfigureAwait(false); }
        finally { Concurrency.Release(); }
    }

    private async ValueTask<PlatformJavaProbeOutput> ProbeCoreAsync(PlatformJavaExecutableIdentity identity, PlatformJavaProbeKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(kind)) return Empty(PlatformJavaProbeStatus.Rejected);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (await CaptureIdentityAsync(identity.Executable, cancellationToken).ConfigureAwait(false) != identity)
                return Empty(PlatformJavaProbeStatus.IdentityChanged);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return Empty(PlatformJavaProbeStatus.IdentityChanged); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ProbeTimeout);
        var start = new ProcessStartInfo(identity.Executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string variable in new[] { "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH" }) start.Environment.Remove(variable);
        foreach (string argument in kind == PlatformJavaProbeKind.Properties ? new[] { "-XshowSettings:properties", "-version" } : ["--list-modules"])
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        int limit = 0;
        bool started = false, cleanupConfirmed = true;
        PlatformJavaProbeOutput result = Empty(PlatformJavaProbeStatus.Failed);
        try
        {
            if (process.Start())
            {
                started = true;
                var output = ReadAsync(process.StandardOutput.BaseStream); var error = ReadAsync(process.StandardError.BaseStream);
                await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                result = await CaptureIdentityAsync(identity.Executable, cancellationToken).ConfigureAwait(false) != identity
                    ? Empty(PlatformJavaProbeStatus.IdentityChanged)
                    : new(process.ExitCode == 0 ? PlatformJavaProbeStatus.Available : PlatformJavaProbeStatus.Failed,
                        process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            result = Empty(Volatile.Read(ref limit) != 0 ? PlatformJavaProbeStatus.OutputLimit : PlatformJavaProbeStatus.TimedOut);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { result = Empty(PlatformJavaProbeStatus.Failed); }
        finally
        {
            if (started) try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    // Reaping must finish even after the caller canceled its read-only probe.
                    await process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                { cleanupConfirmed = false; }
        }
        // A timeout/limit caption may claim the owned process ended only after confirmed reaping.
        if (!cleanupConfirmed) return Empty(PlatformJavaProbeStatus.Failed);
        cancellationToken.ThrowIfCancellationRequested();
        return result;

        async Task<string> ReadAsync(Stream stream)
        {
            byte[] bytes = new byte[MaximumStreamBytes + 1]; int count = 0;
            while (count < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (read == 0) return Encoding.UTF8.GetString(bytes, 0, count);
                count += read;
            }
            Interlocked.Exchange(ref limit, 1); await deadline.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException(deadline.Token);
        }
    }
    private static PlatformJavaProbeOutput Empty(PlatformJavaProbeStatus status) => new(status, null, "", "");
}
