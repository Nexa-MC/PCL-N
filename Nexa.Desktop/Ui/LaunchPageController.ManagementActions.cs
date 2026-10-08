using Nexa.Core;
using Nexa.Services.Minecraft;

namespace Nexa.Desktop.Ui;

internal sealed partial class LaunchPageController
{
    internal void LaunchInstance(string instanceDirectory)
    {
        if (!TryCaptureManagementInstance(instanceDirectory, out var instance)) return;
        _shell.Stage.Navigation.Replace(_launchPage);
        _launchRequest = StartLaunchAsync(instance.Id, _launchPage);
    }

    internal void ModifyInstance(string instanceDirectory)
    {
        if (!TryCaptureManagementInstance(instanceDirectory, out _)) return;
        _shell.Stage.Navigation.Replace(_launchPage);
        ProjectLibrary();
        OpenInstallEditor(_launchPage);
    }

    private bool TryCaptureManagementInstance(string instanceDirectory, out MinecraftInstanceDescriptor instance)
    {
        instance = null!;
        if (_disposed || LaunchBusy || string.IsNullOrWhiteSpace(instanceDirectory)
            || !Path.IsPathFullyQualified(instanceDirectory)) return false;
        if (_store.ReadAppliedValue(_libraryId) is not MinecraftLibrarySnapshot { IsLoading: false } snapshot
            || snapshot.SelectedInstance is not { } selected
            || selected.Id != ReadCell(LaunchPageState.SelectedInstanceKey)
            || !PathIdentity.Comparer.Equals(snapshot.RootDirectory, ReadCell(LaunchPageState.InstanceDirectoryKey)))
        { _feedback.Warn("当前版本已变化，请重新打开实例概览。"); return false; }
        try
        {
            string requested = Path.GetFullPath(instanceDirectory);
            string expected = Path.GetFullPath(Path.Combine(snapshot.RootDirectory, "versions", selected.Id));
            if (!PathIdentity.Comparer.Equals(requested, expected)
                || !PathIdentity.Comparer.Equals(Path.GetFullPath(selected.DirectoryPath), expected))
            { _feedback.Warn("当前版本已变化，请重新打开实例概览。"); return false; }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { _feedback.Warn("实例目录无效，请重新打开实例概览。"); return false; }
        instance = selected; return true;
    }
}
