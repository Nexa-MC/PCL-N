using System.Text.Json;
using Nexa.Services.Settings;
using Nexa.UI.Next;
using Nexa.UI.Next.Backend.Avalonia;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Desktop;

internal static partial class Program
{
    internal static AvaloniaUiStartupAppearance ReadStartupAppearance(string root)
    {
        var fallback = new AvaloniaUiStartupAppearance(ProductVersion: ResolveInformationalVersion());
        try
        {
            using JsonDocument? document = ReadBootstrapSettings(Path.Combine(root,
                Nexa.Services.Files.FolderNames.Settings, "settings.json"));
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out int version) || version != 1) return fallback;
            JsonElement snapshot = document.RootElement;
            XsrUiThemeMode mode = XsrUiThemeMode.System;
            if (snapshot.TryGetProperty("integerOptions", out var integers) && integers.ValueKind == JsonValueKind.Object
                && integers.TryGetProperty("UiDarkMode", out var dark) && dark.ValueKind == JsonValueKind.Number
                && dark.TryGetInt32(out int value))
                mode = value switch { 0 => XsrUiThemeMode.Light, 1 => XsrUiThemeMode.Dark, _ => XsrUiThemeMode.System };
            bool Boolean(string key) => snapshot.TryGetProperty("booleanOptions", out var booleans)
                && booleans.ValueKind == JsonValueKind.Object && booleans.TryGetProperty(key, out var option)
                && option.ValueKind == JsonValueKind.True;
            bool reduced = Boolean("SystemDisableUiAnimations");
            if (snapshot.TryGetProperty("textOptions", out var texts) && texts.ValueKind == JsonValueKind.Object
                && texts.TryGetProperty(SettingsPolicySchema.StorageKey, out var layers) && layers.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var policy = JsonDocument.Parse(layers.GetString()!);
                    var policyRoot = policy.RootElement;
                    if (policyRoot.ValueKind == JsonValueKind.Object
                        && policyRoot.TryGetProperty("version", out var policyVersion) && policyVersion.ValueKind == JsonValueKind.Number
                        && policyVersion.TryGetInt32(out int number) && number == 1
                        && policyRoot.TryGetProperty("global", out var global) && global.ValueKind == JsonValueKind.Object
                        && global.TryGetProperty("appearance.reduced-motion", out var motion) && motion.ValueKind == JsonValueKind.Object
                        && motion.TryGetProperty("mode", out var custom) && custom.ValueKind == JsonValueKind.String && custom.GetString() == "Custom"
                        && motion.TryGetProperty("value", out var enabled) && enabled.ValueKind == JsonValueKind.String
                        && bool.TryParse(enabled.GetString(), out bool parsed)) reduced |= parsed;
                }
                catch (JsonException) { }
            }
            return fallback with { ThemeMode = mode, ReducedMotion = reduced };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return fallback; }
    }

    internal static AvaloniaUiStartupAppearance CommittedStartupAppearance(XsrStateStore state, XsrUiRenderer renderer)
    {
        var mode = state.TryResolve(XsrSemanticId.Parse("UiDarkMode"), out var theme)
            ? state.Read<int>(theme).Value switch { 0 => XsrUiThemeMode.Light, 1 => XsrUiThemeMode.Dark, _ => XsrUiThemeMode.System }
            : XsrUiThemeMode.System;
        return new(mode, renderer.ReducedMotion, ResolveInformationalVersion());
    }
}
