using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nexa.Services.Updates;

/// <summary>
/// Compatibility entry points for the retired caller-controlled executable handoff.
/// Automatic replacement requires a separately installed, protected update helper.
/// </summary>
public sealed class UpdateRestartScheduler
{
    public UpdateRestartScheduler(IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
    }

    /// <summary>Refuses the retired install-and-restart handoff.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Preserve the retired instance API while refusing unsafe handoff.")]
    public void ScheduleInstallAndRestart(PreparedLauncherUpdate update, int processId) =>
        RefuseUnprotectedHandoff(update);

    /// <summary>Refuses the retired install-on-exit handoff.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Preserve the retired instance API while refusing unsafe handoff.")]
    public void ScheduleInstallOnExit(PreparedLauncherUpdate update, int processId) =>
        RefuseUnprotectedHandoff(update);

    private static void RefuseUnprotectedHandoff(PreparedLauncherUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        throw new NotSupportedException("自动更新需要预安装且受保护的更新助手。请使用系统安装包并按提示提升权限。");
    }

    /// <summary>
    /// Refuses to turn a caller-controlled staged executable into an update helper.
    /// </summary>
    public static ProcessStartInfo CreateReplacementProcess(PreparedLauncherUpdate update, int processId, bool restartAfterInstall)
    {
        ArgumentNullException.ThrowIfNull(update);
        throw new NotSupportedException("暂存的启动器不能作为受保护更新助手执行。请使用系统安装包并按提示提升权限。");
    }
}
