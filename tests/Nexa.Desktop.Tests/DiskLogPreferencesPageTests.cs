using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void DiskLogControlsApplyPolicyAndRequireExplicitActions()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        int opened = 0, exports = 0;
        settings.OpenLogDirectory = () => opened++;
        settings.ExportLogs = _ => { exports++; return Task.FromResult(false); };
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.privacy").Entity);
        scene = fixture.Shell.Render(new(1000, 1200));
        AssertEqual(0, opened); AssertEqual(0, exports);
        var input = FindByKey(fixture.Shell, scene, "SettingsInput.diagnostics.disk-log-days").Entity;
        fixture.Shell.Renderer.Focus(input); fixture.Shell.Renderer.SetTextInputValue(input, "14");
        Emit(fixture.Intents, "ui.settings.edit", FindByKey(fixture.Shell, scene, "SettingsEdit.diagnostics.disk-log-days").Entity);
        fixture.Shell.Render(new(1000, 1200));
        AssertTrue(SpinWait.SpinUntil(() => fixture.Foundation.Host.Logging.DiskRetentionDays == 14, TimeSpan.FromSeconds(5)));
        AssertEqual("14", fixture.Foundation.Host.SettingsPolicy.Read(new()).Value!.Values.Single(item => item.Key == "diagnostics.disk-log-days").Value.Value);
        scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.打开日志目录").Entity);
        fixture.Shell.Render(new(1000, 1200)); AssertEqual(1, opened);
        scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.导出日志").Entity);
        fixture.Shell.Render(new(1000, 1200)); AssertEqual(1, exports);
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains("已导出", StringComparison.Ordinal)));
    }

    private static void DiskLogExportCoalescesOffersCancellationAndCancelsOnDispose()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0; CancellationToken observed = default;
        settings.ExportLogs = token => { requests++; observed = token; return pending.Task; };
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.privacy").Entity);
        scene = fixture.Shell.Render(new(1000, 1200));
        var export = FindByKey(fixture.Shell, scene, "Management.导出日志").Entity;
        Emit(fixture.Intents, "ui.settings.management.action", export);
        Emit(fixture.Intents, "ui.settings.management.action", export);
        scene = fixture.Shell.Render(new(1000, 1200)); AssertEqual(1, requests);
        var disabled = FindByKey(fixture.Shell, scene, "Management.导出日志").Entity;
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(disabled)!.Enabled);
        var cancel = FindByKey(fixture.Shell, scene, "Management.取消日志导出").Entity;
        Emit(fixture.Intents, "ui.settings.management.action", cancel);
        fixture.Shell.Render(new(1000, 1200)); AssertTrue(observed.IsCancellationRequested);
        pending.SetResult(true);
        AssertTrue(SpinWait.SpinUntil(() => pending.Task.IsCompleted, TimeSpan.FromSeconds(5)));
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains("已导出", StringComparison.Ordinal)));

        // A separate controller also proves teardown cancels an outstanding native/export effect.
        using var second = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        var onExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken exitToken = default;
        second.ExportLogs = token => { exitToken = token; return onExit.Task; };
        fixture.Shell.Stage.Navigation.Replace(second.Page);
        scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.privacy").Entity);
        scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.导出日志").Entity);
        fixture.Shell.Render(new(1000, 1200)); second.Dispose(); AssertTrue(exitToken.IsCancellationRequested);
        onExit.SetResult(true);
    }

    private static void DiskLogActionFailuresHideExceptionPaths()
    {
        using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
        using var settings = new SettingsPageController(fixture.Shell, fixture.Intents, fixture.Foundation.Queries,
            fixture.Foundation.Commands, fixture.Store, fixture.Feedback);
        settings.OpenLogDirectory = () => throw new IOException("/private/user-secret/logs");
        settings.ExportLogs = _ => Task.FromException<bool>(new IOException("/private/user-secret/export"));
        fixture.Shell.Renderer.ReducedMotion = true; fixture.Shell.Stage.Navigation.Replace(settings.Page);
        var scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.section", FindByKey(fixture.Shell, scene, "SettingsNav.privacy").Entity);
        scene = fixture.Shell.Render(new(1000, 1200));
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.打开日志目录").Entity);
        Emit(fixture.Intents, "ui.settings.management.action", FindByKey(fixture.Shell, scene, "Management.导出日志").Entity);
        fixture.Shell.Render(new(1000, 1200));
        AssertTrue(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains("日志未导出", StringComparison.Ordinal)));
        AssertFalse(fixture.Feedback.Snapshot().Notifications.Any(item => item.Message.Contains("user-secret", StringComparison.Ordinal)));
    }
}
