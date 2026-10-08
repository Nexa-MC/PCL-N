using Nexa.Desktop.Ui;
using Nexa.UI.Next;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void SystemRegistrationsAndCustomAppearancePreserveBounds()
    {
        string autostart = Nexa.Desktop.DesktopSystemPreferences.DesktopEntry(["/tmp/a dir/launcher%name", "/tmp/app.dll"], false);
        AssertFalse(autostart.Contains(" %u", StringComparison.Ordinal));
        AssertFalse(autostart.Contains(" %f", StringComparison.Ordinal));
        AssertTrue(autostart.Contains("launcher%%name", StringComparison.Ordinal));
        string associations = Nexa.Desktop.DesktopSystemPreferences.DesktopEntry(["/tmp/launcher"], true);
        AssertTrue(associations.Contains(" %f\n", StringComparison.Ordinal));
        AssertTrue(associations.Contains("application/x-nexacl-mrpack", StringComparison.Ordinal));
        AssertFalse(associations.Contains("application/zip", StringComparison.Ordinal));
        var palette = CustomAppearanceSession.ParsePalette("{\"background\":\"#102030\",\"foreground\":\"#F0F1F2\",\"accent\":\"#345678\"}")!;
        AssertEqual(new XsrUiColor(16, 32, 48), palette.Background);
        var scheme = new XsrUiColorScheme(false) { CustomPalette = palette };
        AssertEqual(palette.Accent, scheme.AccentFill);
        AssertEqual(palette.Foreground, scheme.Foreground(new(52, 61, 74)));
        AssertTrue(CustomAppearanceSession.ParsePalette("") is null);
        foreach (string invalid in new[] { "{}", "[]", "{\"background\":\"red\",\"foreground\":\"#ffffff\",\"accent\":\"#000000\"}" })
        {
            bool rejected = false;
            try { CustomAppearanceSession.ParsePalette(invalid); } catch (InvalidDataException) { rejected = true; }
            AssertTrue(rejected);
        }
        bool relativeRejected = false;
        try { CustomAppearanceSession.LoadAsync("relative.png", default).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { relativeRejected = true; }
        AssertTrue(relativeRejected);
    }

    private static void SystemAutostartIsOwnedReversibleAndLocal()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "nexa-autostart-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Directory.CreateDirectory(directory);
        string foreign = Path.Combine(directory, "autostart", "another-launcher.desktop");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Nexa.Desktop.DesktopSystemPreferences.ApplyAutostartAsync(false, default).GetAwaiter().GetResult();
            AssertFalse(Directory.Exists(Path.Combine(directory, "autostart")));
            AssertTrue(Directory.Exists(directory));
            Directory.CreateDirectory(Path.Combine(directory, "autostart"));
            File.WriteAllText(foreign, "other-owned-entry");
            Nexa.Desktop.DesktopSystemPreferences.ApplyAutostartAsync(true, default).GetAwaiter().GetResult();
            string owned = Path.Combine(directory, "autostart", "nexacl.desktop");
            AssertTrue(File.Exists(owned));
            AssertFalse(File.ReadAllText(owned).Contains(" %u", StringComparison.Ordinal));
            Nexa.Desktop.DesktopSystemPreferences.ApplyAutostartAsync(false, default).GetAwaiter().GetResult();
            AssertFalse(File.Exists(owned));
            AssertEqual("other-owned-entry", File.ReadAllText(foreign));
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); Directory.Delete(directory, true); }
    }

    private static void SystemPreferencesConsumeRoutedCommittedChanges()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "nexa-policy-autostart-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Directory.CreateDirectory(directory);
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            using var fixture = new LaunchPageFixture(new ImmediateInstanceSource([]));
            var platform = new Nexa.UI.Next.Backend.Avalonia.AvaloniaUiPlatformActions();
            var session = new SystemPreferencesSession(fixture.Foundation.Queries, fixture.Store, platform, fixture.Feedback, _ => { }, _ => { });
            try
            {
                string owned = Path.Combine(directory, "autostart", "nexacl.desktop");
                AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.autostart", Nexa.Services.Settings.SettingsLayer.Global,
                    new(Nexa.Services.Settings.SettingsOverrideMode.Custom, "true"))).IsSuccess);
                AssertTrue(SpinWait.SpinUntil(() => File.Exists(owned), TimeSpan.FromSeconds(5)));
                AssertTrue(fixture.Foundation.Host.SettingsPolicy.Set(new("general.autostart", Nexa.Services.Settings.SettingsLayer.Global,
                    new(Nexa.Services.Settings.SettingsOverrideMode.Custom, "false"))).IsSuccess);
                AssertTrue(SpinWait.SpinUntil(() => !File.Exists(owned), TimeSpan.FromSeconds(5)));
            }
            finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); Directory.Delete(directory, true); }
    }
}
