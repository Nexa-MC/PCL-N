using System.Diagnostics;
using System.Security.Cryptography;
using Nexa.Sidecar.Transport;

namespace Nexa.Xsr.Runtime;

/// <summary>Discovers, verifies and supervises renamed native executables independently.</summary>
public sealed class SidecarSupervisor : IDisposable, IAsyncDisposable
{
    private readonly Func<Stream, Stream, CancellationToken, Task> _verify;
    private readonly Action<string, string>? _diagnostic;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<Child> _children = [];
    private Task _startup = Task.CompletedTask;
    private bool _started, _disposed;
    public XsrFunctionPatchAdmission? FunctionPatchAdmission { get; init; }
    public XsrSignalAdmission? SignalAdmission { get; init; }
    public XsrUiPatchAdmission? UiPatchAdmission { get; init; }
    public XsrUiModuleAdmission? UiModuleAdmission { get; init; }

    public SidecarSupervisor(Func<Stream, Stream, CancellationToken, Task> verify,
        Action<string, string>? diagnostic = null)
    {
        _verify = verify ?? throw new ArgumentNullException(nameof(verify));
        _diagnostic = diagnostic;
    }

    public IReadOnlyList<SidecarHostSession> Sessions
    {
        get { lock (_gate) return _children.Where(child => child.Session.State == SidecarSessionState.Active).Select(child => child.Session).ToArray(); }
    }

    public Task StartAsync(string executableDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Sidecar discovery has already started.");
            _started = true;
            _startup = DiscoverAsync(Path.GetFullPath(executableDirectory), cancellationToken);
            return _startup;
        }
    }

    private async Task DiscoverAsync(string directory, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        string[] candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".nsc", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Take(65).ToArray();
        if (candidates.Length > 64) throw new InvalidDataException("Sidecar discovery package budget exceeded.");
        // Sequential bootstrap bounds processes, open images and verification memory.
        foreach (string path in candidates)
        {
            linked.Token.ThrowIfCancellationRequested();
            await StartChildAsync(path, linked.Token).ConfigureAwait(false);
        }
    }

    private async Task StartChildAsync(string path, CancellationToken cancellationToken)
    {
        Process? process = null;
        Stream? stream = null;
        SidecarHostSession? session = null;
        byte[] challenge = RandomNumberGenerator.GetBytes(SidecarBootstrap.ChallengeSize);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked Sidecar images are not supported.");
            using var image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            SidecarExecutable.Validate(image);
            using var signature = new FileStream(path + ".asc", FileMode.Open, FileAccess.Read, FileShare.Read);
            if (signature.Length is <= 0 or > 64 * 1024) throw new InvalidDataException("Invalid Sidecar signature size.");
            await _verify(image, signature, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            using var listener = SidecarIpcListener.Bind("nexa-" + Guid.NewGuid().ToString("N"));
            // Bind the Windows pipe before starting the child as well as the Unix socket.
            Task<Stream> accepting = listener.AcceptAsync(deadline.Token).AsTask();
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                RedirectStandardInput = true,
            };
            start.ArgumentList.Add("--nexa-sidecar");
            start.ArgumentList.Add("--endpoint");
            start.ArgumentList.Add(listener.Endpoint);
            try
            {
                process = Process.Start(start) ?? throw new IOException("Sidecar process did not start.");
                await process.StandardInput.BaseStream.WriteAsync(challenge, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                Task exit = process.WaitForExitAsync(deadline.Token);
                if (await Task.WhenAny(accepting, exit).ConfigureAwait(false) != accepting)
                    throw new IOException("Sidecar exited before connecting.");
                stream = await accepting.ConfigureAwait(false);
            }
            catch
            {
                deadline.Cancel();
                listener.Dispose();
                try { (await accepting.ConfigureAwait(false)).Dispose(); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
                throw;
            }
            await SidecarBootstrap.AuthenticateAsync(stream, challenge, deadline.Token).ConfigureAwait(false);
            session = new(new SidecarConnection(stream), Path.GetFileNameWithoutExtension(path))
            { FunctionPatchAdmission = FunctionPatchAdmission, SignalAdmission = SignalAdmission, UiPatchAdmission = UiPatchAdmission, UiModuleAdmission = UiModuleAdmission };
            stream = null; // Session now owns the stream.
            await session.HandshakeAsync(deadline.Token).ConfigureAwait(false);
            await session.AcceptRegistrationAsync(deadline.Token).ConfigureAwait(false);
            await session.AcceptStateSnapshotAsync(deadline.Token).ConfigureAwait(false);
            await session.ActivateAsync(deadline.Token).ConfigureAwait(false);
            var child = new Child(process, session);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _children.Add(child);
            }
            process = null;
            session = null;
            child.Monitor = MonitorAsync(child);
            Report(Path.GetFileName(path), "active");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            Report(Path.GetFileName(path), "failed: " + error.Message);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(challenge);
            stream?.Dispose();
            session?.Dispose();
            if (process is not null) await StopProcessAsync(process).ConfigureAwait(false);
        }
    }

    private async Task MonitorAsync(Child child)
    {
        try
        {
            Task receive = child.Session.RunReceiveLoopAsync(_lifetime.Token).AsTask();
            Task exit = child.Process.WaitForExitAsync(_lifetime.Token);
            await Task.WhenAny(receive, exit).ConfigureAwait(false);
            child.Session.Dispose();
            try { await receive.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { Report(child.Session.PluginName, "disconnected: " + error.Message); }
        finally
        {
            child.Session.Dispose();
            lock (_gate) _children.Remove(child);
            await child.StopAsync().ConfigureAwait(false);
        }
    }

    private void Report(string name, string status)
    {
        try { _diagnostic?.Invoke(name, status); }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException) { }
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or TimeoutException) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        Child[] children;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            children = _children.ToArray();
        }
        _lifetime.Cancel();
        foreach (Child child in children) { child.Session.Dispose(); _ = child.StopAsync(); }
        // Each monitor owns process termination; startup owns children not yet published.
    }

    public async ValueTask DisposeAsync()
    {
        Child[] children;
        lock (_gate) children = _children.ToArray();
        Dispose();
        try { await _startup.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await Task.WhenAll(children.Select(child => child.Monitor)).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private sealed class Child(Process process, SidecarHostSession session)
    {
        private readonly object _stopGate = new();
        private Task? _stopTask;
        public Process Process { get; } = process;
        public SidecarHostSession Session { get; } = session;
        public Task Monitor { get; set; } = Task.CompletedTask;
        public Task StopAsync() { lock (_stopGate) return _stopTask ??= StopProcessAsync(Process); }
    }
}
