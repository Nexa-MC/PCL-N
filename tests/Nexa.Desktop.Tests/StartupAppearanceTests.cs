using System.Globalization;
using System.Text.Json.Nodes;
using Nexa.Desktop.Ui;
using Nexa.Services.Files;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr.State;

namespace Nexa.Desktop.Tests;

internal static partial class Program
{
    private static void StartupAppearanceReadsDurableThemeAndMotionPreferences()
    {
        string root = StartupAppearanceDirectory();
        try
        {
            var (settings, policy, _) = StartupAppearanceSettings(root);
            foreach (var (stored, expected) in new[]
            {
                (0, XsrUiThemeMode.Light), (1, XsrUiThemeMode.Dark), (2, XsrUiThemeMode.System),
            })
            {
                AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global,
                    new(SettingsOverrideMode.Custom, stored.ToString(CultureInfo.InvariantCulture)))).IsSuccess);
                var appearance = Nexa.Desktop.Program.ReadStartupAppearance(root);
                AssertEqual(expected, appearance.ThemeMode);
                AssertEqual(stored, StartupAppearanceSettings(root).Settings.GetValue<int>("UiDarkMode").Value);
                AssertFalse(appearance.ReducedMotion);
                AssertTrue(!string.IsNullOrWhiteSpace(appearance.ProductVersion));
            }

            foreach (bool reduced in new[] { false, true })
                foreach (bool legacyDisabled in new[] { false, true })
                {
                    AssertTrue(policy.SetBatch(new([
                        new("appearance.reduced-motion", SettingsLayer.Global, new(SettingsOverrideMode.Custom, reduced ? "true" : "false")),
                            new("appearance.animations-disabled", SettingsLayer.Global, new(SettingsOverrideMode.Custom, legacyDisabled ? "true" : "false")),
                        ], settings.Revision)).IsSuccess);
                    var appearance = Nexa.Desktop.Program.ReadStartupAppearance(root);
                    AssertEqual(XsrUiThemeMode.System, appearance.ThemeMode);
                    AssertEqual(reduced || legacyDisabled, appearance.ReducedMotion);
                    AssertEqual(legacyDisabled, settings.GetValue<bool>("SystemDisableUiAnimations").Value);
                    var restarted = StartupAppearanceSettings(root);
                    AssertEqual(legacyDisabled, restarted.Settings.GetValue<bool>("SystemDisableUiAnimations").Value);
                    AssertEqual(reduced ? "true" : "false", restarted.Policy.Read(new()).Value!.Values
                        .Single(value => value.Key == "appearance.reduced-motion").Value.Value);
                    AssertStartupAppearanceReadOnly(root, appearance);
                }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void StartupAppearanceRejectsInvalidDocumentsWithoutWrites()
    {
        string root = StartupAppearanceDirectory();
        try
        {
            var fallback = Nexa.Desktop.Program.ReadStartupAppearance(root);
            AssertEqual(XsrUiThemeMode.System, fallback.ThemeMode);
            AssertFalse(fallback.ReducedMotion);
            AssertStartupAppearanceReadOnly(root, fallback);
            AssertFalse(Directory.Exists(Path.Combine(root, FolderNames.Settings)));

            string path = StartupAppearancePath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            foreach (string document in new[]
            {
                "{broken", "null", "[]", "{}",
                """{"schemaVersion":0,"integerOptions":{"UiDarkMode":1},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true}}""",
                """{"schemaVersion":2,"integerOptions":{"UiDarkMode":1},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true}}""",
                """{"schemaVersion":"1","integerOptions":{"UiDarkMode":1},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true}}""",
                """{"schemaVersion":true,"integerOptions":{"UiDarkMode":1},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true}}""",
                """{"schemaVersion":1,"integerOptions":{"UiDarkMode":"1"},"booleanOptions":{"SystemDisableUiAnimations":"true","UiUltraLowPowerMode":1},"textOptions":{"NexaSettingsLayers":true}}""",
                """{"schemaVersion":1,"integerOptions":{"UiDarkMode":true},"booleanOptions":{"SystemDisableUiAnimations":1,"UiUltraLowPowerMode":"true"}}""",
                """{"schemaVersion":1,"integerOptions":{"UiDarkMode":1.5},"booleanOptions":{"SystemDisableUiAnimations":null,"UiUltraLowPowerMode":null}}""",
                """{"schemaVersion":1,"integerOptions":{"UiDarkMode":99}}""",
                """{"schemaVersion":1,"integerOptions":[],"booleanOptions":true,"textOptions":[]}""",
                """{"schemaVersion":1,"textOptions":{"NexaSettingsLayers":"{broken"}}""",
                """{"schemaVersion":1,"integerOptions":{"UiDarkMode":1},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true},"padding":"OVERSIZE"}"""
                    .Replace("OVERSIZE", new string('x', 4 * 1024 * 1024), StringComparison.Ordinal),
            })
            {
                File.WriteAllText(path, document);
                AssertStartupAppearanceReadOnly(root, fallback);
            }

            foreach (string layers in new[]
            {
                "null", "[]", "{}",
                """{"version":2,"global":{"appearance.reduced-motion":{"mode":"Custom","value":"true"}}}""",
                """{"version":"1","global":{"appearance.reduced-motion":{"mode":"Custom","value":"true"}}}""",
                """{"version":1,"global":{"appearance.reduced-motion":{"mode":"Auto","value":"true"}}}""",
                """{"version":1,"global":{"appearance.reduced-motion":{"mode":true,"value":"true"}}}""",
                """{"version":1,"global":{"appearance.reduced-motion":{"mode":"Custom","value":true}}}""",
                """{"version":1,"global":{"appearance.reduced-motion":{"mode":"Custom","value":"invalid"}}}""",
                """{"version":1,"global":[],"instances":{}}""",
                """{"version":1,"global":{},"instances":{"unselected":{"appearance.reduced-motion":{"mode":"Custom","value":"true"}}}}""",
            })
            {
                var document = new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["textOptions"] = new JsonObject { [SettingsPolicySchema.StorageKey] = layers },
                };
                File.WriteAllText(path, document.ToJsonString());
                AssertStartupAppearanceReadOnly(root, fallback);
            }

            // Retired low-power facts must neither suppress motion nor rewrite old data.
            var retired = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["integerOptions"] = new JsonObject { ["UiDarkMode"] = 0 },
                ["booleanOptions"] = new JsonObject { ["UiUltraLowPowerMode"] = true },
                ["textOptions"] = new JsonObject
                {
                    [SettingsPolicySchema.StorageKey] = """{"version":1,"global":{"appearance.low-power":{"mode":"Custom","value":"true"}}}""",
                },
            };
            File.WriteAllText(path, retired.ToJsonString());
            AssertStartupAppearanceReadOnly(root, fallback with { ThemeMode = XsrUiThemeMode.Light });

            // An invalid optional policy does not discard valid independently committed flags.
            File.WriteAllText(path, """{"schemaVersion":1,"integerOptions":{"UiDarkMode":0},"booleanOptions":{"SystemDisableUiAnimations":true,"UiUltraLowPowerMode":true},"textOptions":{"NexaSettingsLayers":"{broken"}}""");
            AssertStartupAppearanceReadOnly(root, fallback with { ThemeMode = XsrUiThemeMode.Light, ReducedMotion = true });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void StartupAppearanceFallbackModesApplyOnlyWithoutCommittedCells()
    {
        foreach (var (mode, dark) in new[] { (XsrUiThemeMode.Light, false), (XsrUiThemeMode.Dark, true) })
        {
            var empty = new XsrStateStoreBuilder().Build();
            var shell = new XsrUiShell(empty);
            var native = new AppearanceSource(!dark);
            using var session = new DesktopAppearanceSession(shell, empty, native, fallbackMode: mode);
            AssertEqual(dark, shell.Renderer.ColorScheme.IsDark);
            AssertEqual(mode, native.Modes.Single());
            foreach (bool systemDark in new[] { false, true, false })
            {
                native.Change(systemDark);
                shell.Render(new(850, 500));
                AssertEqual(dark, shell.Renderer.ColorScheme.IsDark);
            }
            AssertEqual(1, native.Modes.Count);
        }

        string root = StartupAppearanceDirectory();
        try
        {
            var (_, policy, state) = StartupAppearanceSettings(root);
            AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
            var shell = new XsrUiShell(state);
            var native = new AppearanceSource(true);
            using var session = new DesktopAppearanceSession(shell, state, native, fallbackMode: XsrUiThemeMode.Dark);
            AssertFalse(shell.Renderer.ColorScheme.IsDark);
            AssertEqual(XsrUiThemeMode.Light, native.Modes.Single());
            native.Change(false); native.Change(true); shell.Render(new(850, 500));
            AssertFalse(shell.Renderer.ColorScheme.IsDark);
            AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
            shell.Render(new(850, 500)); AssertTrue(shell.Renderer.ColorScheme.IsDark);
            native.Change(false); shell.Render(new(850, 500)); AssertTrue(shell.Renderer.ColorScheme.IsDark);
            AssertTrue(policy.Set(new("appearance.theme-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))).IsSuccess);
            shell.Render(new(850, 500)); AssertFalse(shell.Renderer.ColorScheme.IsDark);
            native.Change(true); shell.Render(new(850, 500)); AssertTrue(shell.Renderer.ColorScheme.IsDark);
            AssertTrue(native.Modes.SequenceEqual(new[] { XsrUiThemeMode.Light, XsrUiThemeMode.Dark, XsrUiThemeMode.System }));
            shell.Renderer.ReducedMotion = false;
            shell.Renderer.OptionalMotionSuspended = true;
            AssertTrue(shell.Renderer.EffectiveReducedMotion);
            AssertFalse(Nexa.Desktop.Program.CommittedStartupAppearance(state, shell.Renderer).ReducedMotion);
            shell.Renderer.ReducedMotion = true;
            AssertTrue(Nexa.Desktop.Program.CommittedStartupAppearance(state, shell.Renderer).ReducedMotion);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string StartupAppearanceDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-startup-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
    private static string StartupAppearancePath(string root) => Path.Combine(root, FolderNames.Settings, "settings.json");
    private static (SettingsService Settings, SettingsPolicyService Policy, XsrStateStore State) StartupAppearanceSettings(string root)
    {
        var schema = LauncherDefaults.CreateSchema();
        XsrStateStoreBuilder builder = new();
        SettingsService.DeclareState(builder, schema);
        SettingsPolicyContract.DeclareState(builder);
        var state = builder.Build();
        var settings = new SettingsService(state, schema, new LauncherSettingsJsonPort(StartupAppearancePath(root), schema));
        return (settings, new SettingsPolicyService(settings), state);
    }
    private static void AssertStartupAppearanceReadOnly(string root, AvaloniaUiStartupAppearance expected)
    {
        string path = StartupAppearancePath(root);
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        DateTime? modified = before is null ? null : File.GetLastWriteTimeUtc(path);
        string[] entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        AssertEqual(expected, Nexa.Desktop.Program.ReadStartupAppearance(root));
        AssertTrue(entries.SequenceEqual(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)));
        if (before is null) AssertFalse(File.Exists(path));
        else
        {
            AssertTrue(before.AsSpan().SequenceEqual(File.ReadAllBytes(path)));
            AssertEqual(modified!.Value, File.GetLastWriteTimeUtc(path));
        }
    }
}
