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
}
