using Nexa.Desktop;
using Nexa.Desktop.Ui;
using Nexa.Services.Settings;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void AdvancedWorkspaceDispatchesFiniteRoutesAndCancelsRetiredDelay()
    {
        using var fixture = new DeveloperDiagnosticsFixture();
        List<DesktopCommandRoute> navigation = []; int palettes = 0, openedFiles = 0;
        fixture.Settings.LauncherSafeMode = true;
        fixture.Settings.NavigateAdvancedCommand = navigation.Add;
        fixture.Settings.OpenAdvancedCommandPalette = () => { palettes++; return true; };
        fixture.Settings.OpenAdvancedSettingsFile = () => openedFiles++;
        fixture.Select("advanced");
        AssertFalse(fixture.Has("AdvancedWorkspace.Settings.Read"));
        AssertFalse(fixture.Has("AdvancedWorkspace.Settings.OpenFile"));
        foreach (var route in DesktopCommandLine.Commands) fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Go." + route.Id);
        AssertEqual(DesktopCommandLine.Commands.Count, navigation.Count);
        AssertTrue(navigation.SequenceEqual(DesktopCommandLine.Commands));
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Palette"); AssertEqual(1, palettes);
        fixture.SetDeveloper(true);
        AssertEqual(0, openedFiles); fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.OpenFile"); AssertEqual(1, openedFiles);
        long revision = fixture.Host.SettingsPolicy.Read(new()).Value!.Revision;
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Delay.Start");
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(fixture.Find("AdvancedWorkspace.Delay.Start"))!.Enabled);
        AssertTrue(SpinWait.SpinUntil(() => { fixture.Pump(); return HasElapsed(); }, TimeSpan.FromSeconds(3)));
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Delay.Start");
        var retired = fixture.Find("AdvancedWorkspace.Delay.Start");
        var retiredFile = fixture.Find("AdvancedWorkspace.Settings.OpenFile");
        fixture.Select("general"); AssertFalse(fixture.Shell.Tree.IsAlive(retired));
        Emit(fixture.Intents, "ui.settings.management.action", retired); fixture.Pump();
        Emit(fixture.Intents, "ui.settings.management.action", retiredFile); fixture.Pump(); AssertEqual(1, openedFiles);
        fixture.Select("advanced");
        AssertFalse(HasElapsed());
        AssertTrue(fixture.Shell.Tree.GetComponent<XsrUiInput>(fixture.Find("AdvancedWorkspace.Delay.Start"))!.Enabled);
        AssertEqual(revision, fixture.Host.SettingsPolicy.Read(new()).Value!.Revision);
        fixture.Settings.OpenAdvancedSettingsFile = null; fixture.Select("general"); fixture.Select("advanced");
        AssertFalse(fixture.Shell.Tree.GetComponent<XsrUiInput>(fixture.Find("AdvancedWorkspace.Settings.OpenFile"))!.Enabled);
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.OpenFile"); AssertEqual(1, openedFiles);
        fixture.SetDeveloper(false); AssertFalse(fixture.Has("AdvancedWorkspace.Settings.OpenFile"));

        bool HasElapsed()
        {
            bool found = false;
            fixture.Shell.Tree.Walk(fixture.Settings.Page, entity =>
            { if (fixture.Shell.Tree.GetComponent<XsrUiText>(entity)?.Content == "实际调度耗时") found = true; return true; });
            return found;
        }
    }

    private static void AdvancedSettingsSnapshotRedactsSecretsPathsCommandsAndCopiesCapturedRows()
    {
        using var fixture = new DeveloperDiagnosticsFixture();
        void Set(string key, string value) => AssertTrue(fixture.Host.SettingsPolicy.Set(new(key, SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
        Set("network.proxy-password", "private-proxy-secret"); Set("game.jvm", "-Dfixture=private-jvm-secret");
        Set("appearance.background-path", Path.GetFullPath("private-image-path"));
        var snapshot = fixture.Host.SettingsPolicy.Read(new()).Value!;
        var rows = SettingsPageController.CaptureRedactedAdvancedSettings(snapshot);
        string text = string.Join('\n', rows);
        AssertTrue(text.Contains("network.proxy-password | Text |", StringComparison.Ordinal));
        AssertTrue(text.Contains("<redacted>", StringComparison.Ordinal));
        AssertFalse(text.Contains("private-proxy-secret", StringComparison.Ordinal));
        AssertFalse(text.Contains("private-jvm-secret", StringComparison.Ordinal));
        AssertFalse(text.Contains("private-image-path", StringComparison.Ordinal));
        var invalid = new SettingsEffectiveSnapshot(0, [new("developer.enabled", new(SettingsOverrideMode.Custom, "private-invalid"), SettingsLayer.Global, SettingsApplyTiming.Immediate, null),
            new("unknown-private-key", new(SettingsOverrideMode.Custom, "private-value"), SettingsLayer.Global, SettingsApplyTiming.Immediate, null)]);
        text = string.Join('\n', SettingsPageController.CaptureRedactedAdvancedSettings(invalid));
        AssertTrue(text.Contains("developer.enabled", StringComparison.Ordinal));
        AssertFalse(text.Contains("private-invalid", StringComparison.Ordinal)); AssertFalse(text.Contains("unknown-private-key", StringComparison.Ordinal));
        fixture.Select("advanced"); fixture.SetDeveloper(true);
        string? copied = null; fixture.Settings.CopyAdvancedSettingsTextAsync = value => { copied = value; return Task.CompletedTask; };
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.Read");
        AssertTrue(fixture.Has("AdvancedWorkspace.Settings.Next"));
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.Copy");
        AssertTrue(copied is not null && copied.Contains("<redacted>", StringComparison.Ordinal));
        AssertFalse(copied!.Contains("private-proxy-secret", StringComparison.Ordinal));
        string captured = copied!;
        Set("network.proxy-password", "changed-private-secret");
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.Next");
        fixture.Click("ui.settings.management.action", "AdvancedWorkspace.Settings.Copy"); AssertEqual(captured, copied);
        fixture.SetDeveloper(false); AssertFalse(fixture.Has("AdvancedWorkspace.Settings.Copy"));
    }
}
