using System.Diagnostics;
using System.Globalization;

namespace Nexa.Platform;

/// <summary>Bounded optional native JDK attach transport; never changes JVM launch arguments.</summary>
internal static class PlatformJvmMemoryProbe
{
    internal static async ValueTask<PlatformJvmMemoryOutput> ReadAsync(string javaExecutable, int processId)
    {
        string? folder = Path.GetDirectoryName(javaExecutable);
        if (string.IsNullOrWhiteSpace(folder)) return default;
        string executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "jcmd.exe" : "jcmd");
        if (!File.Exists(executable)) return default;
        string? heap = await RunAsync(executable, processId, "GC.heap_info").ConfigureAwait(false);
        string? native = await RunAsync(executable, processId, "VM.native_memory").ConfigureAwait(false);
        return new(heap, native);
    }
    private static async ValueTask<string?> RunAsync(string executable, int pid, string command)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture)); info.ArgumentList.Add(command);
        if (command == "VM.native_memory") info.ArgumentList.Add("summary");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return null;
            try
            {
                char[] output = new char[32768];
                int count = await process.StandardOutput.ReadBlockAsync(output.AsMemory(), timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0 && count < output.Length ? new string(output, 0, count) : null;
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }
}
