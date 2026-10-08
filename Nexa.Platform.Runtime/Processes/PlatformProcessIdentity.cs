using System.Buffers.Text;
using System.Diagnostics;

namespace Nexa.Platform;

public static class PlatformProcessIdentityFactory
{
    public static IPlatformProcessIdentity Create() => new PlatformProcessIdentity();
}

/// <summary>Owns native process lookup and maps failures to an explicit unknown observation.</summary>
public sealed class PlatformProcessIdentity : IPlatformProcessIdentity
{
    public PlatformProcessIdentityObservation Observe(int processId)
    {
        if (processId <= 0) return new(PlatformProcessIdentityState.Unknown);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                try
                {
                    using var input = new FileStream($"/proc/{processId}/stat", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    Span<byte> snapshot = stackalloc byte[4096];
                    int count = input.Read(snapshot);
                    if (count == snapshot.Length || !TryReadLinuxKernelState(snapshot[..count], processId, out char state))
                        return new(PlatformProcessIdentityState.Unknown);
                    if (state is 'Z' or 'X' or 'x') return new(PlatformProcessIdentityState.Exited);
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    // The PID may have disappeared during lookup. Preserve the existing
                    // OS process query's absent/unknown distinction below.
                }
            }
            Process process;
            try { process = Process.GetProcessById(processId); }
            catch (ArgumentException) { return new(PlatformProcessIdentityState.Exited); }
            using (process)
            {
                if (process.HasExited) return new(PlatformProcessIdentityState.Exited);
                long ticks;
                try { ticks = process.StartTime.ToUniversalTime().Ticks; }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return process.HasExited ? new(PlatformProcessIdentityState.Exited)
                        : new(PlatformProcessIdentityState.Unknown);
                }
                return new(PlatformProcessIdentityState.Running, ticks);
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException
            or NotSupportedException or UnauthorizedAccessException or IOException)
        { return new(PlatformProcessIdentityState.Unknown); }
    }

    internal static bool TryReadLinuxKernelState(ReadOnlySpan<byte> snapshot, int processId, out char state)
    {
        state = default;
        int separator = snapshot.IndexOf((byte)' ');
        if (separator <= 0 || !Utf8Parser.TryParse(snapshot[..separator], out int actualId, out int digits)
            || digits != separator || actualId != processId || processId <= 0) return false;
        int close = snapshot.LastIndexOf((byte)')');
        // Kernel comm can contain spaces, newlines and ') Z'; use its final closing delimiter.
        if (close < separator + 2 || close + 4 >= snapshot.Length || snapshot[separator + 1] != '(' || snapshot[close + 1] != ' '
            || snapshot[close + 3] != ' ') return false;
        char value = (char)snapshot[close + 2];
        if (value is not ('R' or 'S' or 'D' or 'Z' or 'T' or 't' or 'X' or 'x' or 'K' or 'W' or 'P' or 'I')) return false;
        ReadOnlySpan<byte> tail = snapshot[(close + 4)..];
        if (!Utf8Parser.TryParse(tail, out int parentId, out int parentDigits) || parentId < 0
            || parentDigits >= tail.Length || tail[parentDigits] != ' ') return false;
        state = value;
        return true;
    }
}
