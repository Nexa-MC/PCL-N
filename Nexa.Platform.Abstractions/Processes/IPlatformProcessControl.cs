using System.Diagnostics;

namespace Nexa.Platform;

public sealed record PlatformProcessControlResult(bool Succeeded, string Code, string Message);
public readonly record struct PlatformProcessSample(long WorkingSetBytes, long PrivateBytes, int ThreadCount,
    TimeSpan CpuTime, long IoReadBytes, long IoWriteBytes);

public interface IPlatformProcessControl
{
    PlatformProcessControlResult Suspend(Process process, bool suspend);
    PlatformProcessControlResult SetPriority(Process process, ProcessPriorityClass priority);
    PlatformProcessControlResult SetAffinity(Process process, nint affinityMask);
    PlatformProcessSample ReadSample(Process process);
}
