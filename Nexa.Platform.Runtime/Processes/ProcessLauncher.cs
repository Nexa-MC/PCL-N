using System.Diagnostics;

namespace Nexa.Services.Updates;


/// <summary>Real process launch over <see cref="Process"/>.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public void Launch(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新替换进程。");
    }
}
