using Nexa.Desktop.Ui;
using Nexa.Services.Logging;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.Desktop;

internal static partial class Program
{
    private static async Task PrepareNormalStartupAsync(AvaloniaUiStartupSession? startup,
        XsrUiShell shell, AvaloniaUiPlatformActions platform, LaunchPageController launch,
        SettingsPageController settings, SettingsPageController versionSettings, ResourcesPageController resources,
        CustomAppearanceSession? appearance, DesktopMediaSession? media, DesktopPresentationSession presentation,
        SystemPreferencesSession? systemPreferences, Action<string> setStage, LogService log,
        DesktopIntegrationSession? integration = null, SidecarStartup.Lifetime? sidecars = null,
        XsrStateStore? startupState = null)
    {
        try
        {
            await PrepareNormalStartupCoreAsync(startup, shell, platform, launch, settings, versionSettings,
                resources, appearance, media, presentation, systemPreferences, setStage, log, integration, sidecars, startupState).ConfigureAwait(false);
        }
        catch
        {
            if (startup is not null && !startup.Completion.IsCompleted)
                try { await startup.DiscardPreparedShellAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (startup.CancellationToken.IsCancellationRequested) { }
            throw;
        }
    }

    private static async Task PrepareNormalStartupCoreAsync(AvaloniaUiStartupSession? startup,
        XsrUiShell shell, AvaloniaUiPlatformActions platform, LaunchPageController launch,
        SettingsPageController settings, SettingsPageController versionSettings, ResourcesPageController resources,
        CustomAppearanceSession? appearance, DesktopMediaSession? media, DesktopPresentationSession presentation,
        SystemPreferencesSession? systemPreferences, Action<string> setStage, LogService log,
        DesktopIntegrationSession? integration, SidecarStartup.Lifetime? sidecars, XsrStateStore? startupState)
    {
        CancellationToken cancellation = startup?.CancellationToken ?? default;
        DesktopStartupReadiness readiness = new(stage =>
        {
            setStage(stage);
            log.Info("Startup", "Preparing " + stage);
        }, cancellation);
        Task OnUi(Func<Task> action, CancellationToken token) => startup is null
            ? action() : startup.InvokeAsync(action, token);
        Task Commit(CancellationToken token) => OnUi(() =>
        {
            token.ThrowIfCancellationRequested();
            shell.Render(new XsrUiSize(850, 500));
            if (startupState is not null)
                startup?.SetAppearance(CommittedStartupAppearance(startupState, shell.Renderer));
            return Task.CompletedTask;
        }, token);

        await readiness.RunAsync("startup_sidecar_discovery", token => (sidecars?.InitialReady ?? Task.CompletedTask).WaitAsync(token)).ConfigureAwait(false);

        // Consume the already admitted local scan and actual controller query tasks. Metadata
        // exists before queued deep links select Java/storage/about settings subsections.
        await readiness.RunAsync("startup_local_instances", token => OnUi(() => launch.PrepareStartupAsync(token), token)).ConfigureAwait(false);
        await readiness.RunAsync("startup_settings_metadata", token => OnUi(async () =>
        {
            await settings.PrepareStartupAsync(token).ConfigureAwait(true);
            await versionSettings.PrepareStartupAsync(token).ConfigureAwait(true);
        }, token)).ConfigureAwait(false);
        await readiness.RunAsync("startup_initial_policies", token => Task.WhenAll(
            appearance?.InitialReady ?? Task.CompletedTask, media?.InitialReady ?? Task.CompletedTask,
            presentation.InitialReady, systemPreferences?.InitialReady ?? Task.CompletedTask,
            integration?.InitialReady ?? Task.CompletedTask).WaitAsync(token)).ConfigureAwait(false);

        if (startup is not null)
        {
            startup.SetLocalizer(shell.Renderer.LocalizeText);
            await readiness.RunAsync("startup_hidden_native_shell", token => startup.PrepareShellAsync(shell, platform, token)).ConfigureAwait(false);
        }
        await readiness.RunAsync("startup_controller_projection", Commit).ConfigureAwait(false);
        await readiness.RunAsync("startup_update_preferences", token => OnUi(() => settings.PrepareIndependentStartupAsync(token), token)).ConfigureAwait(false);
        await readiness.RunAsync("startup_update_discovery", token => OnUi(() => settings.PrepareInitialUpdateAsync(token), token),
            DesktopStartupReadiness.OnlineBudget, () => OnUi(() =>
            {
                settings.SetStartupUpdateOffline();
                return Task.CompletedTask;
            }, cancellation)).ConfigureAwait(false);
        await readiness.RunAsync("startup_destination_facts", token => OnUi(async () =>
        {
            await settings.PrepareVisibleStartupAsync(token).ConfigureAwait(true);
            await versionSettings.PrepareVisibleStartupAsync(token).ConfigureAwait(true);
        }, token)).ConfigureAwait(false);
        await readiness.RunAsync("startup_install_catalog", token => OnUi(() => launch.PrepareVisibleStartupAsync(token), token),
            DesktopStartupReadiness.OnlineBudget, () => OnUi(() =>
            {
                launch.SetStartupOffline();
                return Task.CompletedTask;
            }, cancellation)).ConfigureAwait(false);
        await readiness.RunAsync("startup_resource_catalog", token => OnUi(() => resources.PrepareVisibleStartupAsync(token), token),
            DesktopStartupReadiness.OnlineBudget, () => OnUi(() =>
            {
                resources.SetStartupOffline();
                return Task.CompletedTask;
            }, cancellation)).ConfigureAwait(false);
        await readiness.RunAsync("startup_final_layout", Commit).ConfigureAwait(false);
        if (startup is not null)
            await readiness.RunAsync("startup_native_fonts_media_icons", token => startup.WarmUpShellAsync(shell, token)).ConfigureAwait(false);
        setStage("startup_ready");
        log.Info("Startup", "Initial local state, controller facts and native scene are ready.");
    }

    private static async Task PrepareFirstRunStartupAsync(AvaloniaUiStartupSession? startup,
        XsrUiShell shell, AvaloniaUiPlatformActions platform, Action<string> setStage)
    {
        try { await PrepareFirstRunStartupCoreAsync(startup, shell, platform, setStage).ConfigureAwait(false); }
        catch
        {
            if (startup is not null && !startup.Completion.IsCompleted)
                try { await startup.DiscardPreparedShellAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) when (startup.CancellationToken.IsCancellationRequested) { }
            throw;
        }
    }

    private static async Task PrepareFirstRunStartupCoreAsync(AvaloniaUiStartupSession? startup,
        XsrUiShell shell, AvaloniaUiPlatformActions platform, Action<string> setStage)
    {
        DesktopStartupReadiness readiness = new(setStage, startup?.CancellationToken ?? default);
        if (startup is null)
        {
            shell.Render(new XsrUiSize(850, 500));
            return;
        }
        startup.SetLocalizer(shell.Renderer.LocalizeText);
        await readiness.RunAsync("startup_first_run_native_shell", token => startup.PrepareShellAsync(shell, platform, token)).ConfigureAwait(false);
        await readiness.RunAsync("startup_first_run_scene", token => startup.WarmUpShellAsync(shell, token)).ConfigureAwait(false);
        setStage("startup_first_run_ready");
    }
}
