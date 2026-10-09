using System.Collections.Frozen;
using System.Globalization;

namespace Nexa.Services.Settings;

public enum SettingsApplyTiming { Immediate, NextLaunch, NextTask, Restart }
public enum SettingsValueKind { Boolean, Number, Text, Enum, Path }
public enum SettingsLayer { Builtin, Global, Instance, Profile, Temporary }
public enum SettingsOverrideMode { Inherit, Auto, Custom }
public enum SettingsCapabilityAvailability { Available, NotImplemented, PlatformUnsupported, DependencyMissing, TemporarilyUnavailable }

public sealed record SettingsPolicyDefinition(string Key, SettingsValueKind Kind, string DefaultValue,
    bool InstanceOverride, bool SupportsAuto, string? LegacyKey, SettingsApplyTiming Timing,
    string Unit = "", long? Minimum = null, long? Maximum = null, string Choices = "", bool Exportable = true)
{
    public string Owner { get; init; } = "Nexa.Services.Settings";
    public string? Validate(SettingsOverride value)
    {
        if (value.Mode == SettingsOverrideMode.Inherit) return value.Value is null ? null : "Inherited values cannot carry a payload.";
        if (value.Mode == SettingsOverrideMode.Auto) return SupportsAuto && value.Value is null ? null : "Auto is not supported or carries a payload.";
        if (value.Mode != SettingsOverrideMode.Custom || value.Value is null) return "A custom value is required.";
        string raw = value.Value;
        if (Key == "general.region" && raw is not ("auto" or "follow-language" or "ui-language"))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 85 || raw.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
                return "Expected a named formatting culture, system or interface language.";
            try { _ = CultureInfo.GetCultureInfo(raw); }
            catch (CultureNotFoundException) { return "Unknown formatting culture."; }
        }
        if (Key == "game.server" && (raw.Length > 512 || raw.Any(char.IsWhiteSpace)
            || raw.Any(char.IsControl) || raw.Contains("://", StringComparison.Ordinal)))
            return "Expected a server host with an optional port, without spaces or a URI scheme.";
        if (Key == "game.title" && (raw.Length > 512 || raw.Any(char.IsControl)))
            return "Window title must be at most 512 characters without controls.";
        if (Key is "game.wrapper" or "game.pre-launch" or "game.post-exit" or "game.environment" or "game.classpath-head")
        {
            try
            {
                if (Key == "game.wrapper") _ = Minecraft.Launch.MinecraftLaunchHooks.ParseWrapper(raw);
                else if (Key == "game.environment") _ = Minecraft.Launch.MinecraftLaunchHooks.ParseEnvironment(raw);
                else if (Key == "game.classpath-head") _ = Minecraft.Launch.MinecraftLaunchHooks.ParseClasspathHead(raw);
                else Minecraft.Launch.MinecraftLaunchHooks.ValidatePreLaunch(raw);
            }
            catch (ArgumentException error) { return error.Message; }
        }
        if (Key == "network.proxy-address" && raw.Length > 0 &&
            (!Uri.TryCreate(raw, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5")
                || string.IsNullOrEmpty(proxy.Host) || proxy.UserInfo.Length != 0 || proxy.AbsolutePath != "/"
                || proxy.Query.Length != 0 || proxy.Fragment.Length != 0 || raw.Any(char.IsControl)))
            return "Expected a proxy endpoint without credentials, path, query or fragment.";
        if (Key is "network.proxy-user" or "network.proxy-password" && (raw.Length > 1024 || raw.Any(char.IsControl)))
            return "Proxy credentials must be at most 1024 characters without line breaks.";
        if (Key is "appearance.logo-path" or "appearance.background-path" or "appearance.video-path" or "music.path"
            && raw.Length > 0 && (raw.Length > 4096 || !Path.IsPathFullyQualified(raw) || raw.Any(char.IsControl)))
            return "Expected an absolute local media path without control characters.";
        if (Key == "appearance.background-color" && raw != "auto" &&
            (raw.Length != 7 || raw[0] != '#' || raw.Skip(1).Any(character => !char.IsAsciiHexDigit(character))))
            return "Expected auto or a #RRGGBB background color.";
        if (Key == "appearance.custom-theme" && raw.Length > 0)
        {
            if (raw.Length > 4096) return "Custom theme exceeds its text budget.";
            try
            {
                using var theme = System.Text.Json.JsonDocument.Parse(raw);
                if (theme.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return "Expected a theme object.";
                var colors = theme.RootElement.EnumerateObject().ToArray();
                if (colors.Length != 3 || colors.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 3
                    || colors.Any(item => item.Name is not ("background" or "foreground" or "accent")
                        || item.Value.ValueKind != System.Text.Json.JsonValueKind.String
                        || item.Value.GetString() is not { Length: 7 } value || value[0] != '#'
                        || value.Skip(1).Any(character => !char.IsAsciiHexDigit(character))))
                    return "Expected background, foreground and accent as #RRGGBB colors.";
            }
            catch (System.Text.Json.JsonException) { return "Custom theme is not valid JSON."; }
        }
        return Kind switch
        {
            SettingsValueKind.Boolean when raw is not ("true" or "false") => "Expected true or false.",
            SettingsValueKind.Number when !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                || Minimum is { } min && number < min || Maximum is { } max && number > max => "Value is outside the declared range.",
            SettingsValueKind.Enum when !Choices.Split('|').Contains(raw, StringComparer.Ordinal) => "Unknown choice.",
            SettingsValueKind.Path when raw.IndexOfAny(['\0', '\r', '\n']) >= 0 || !Path.IsPathFullyQualified(raw) => "Expected a fully qualified path.",
            _ when raw.Contains('\0') => "NUL is not allowed.",
            _ => null,
        };
    }
}

public static class SettingsPolicySchema
{
    public const string StorageKey = "NexaSettingsLayers";
    public const string EmptyDocument = "{\"version\":1,\"global\":{},\"instances\":{}}";
    public static IReadOnlyList<SettingsPolicyDefinition> Definitions { get; } = Array.AsReadOnly<SettingsPolicyDefinition>([
        new("general.language", SettingsValueKind.Enum, "auto", false, false, "UiLanguage", SettingsApplyTiming.Immediate, Choices: "auto|zh-Hans|zh-Hant|en"),
        new("general.region", SettingsValueKind.Text, "auto", false, false, "UiFormatCulture", SettingsApplyTiming.Restart),
        new("general.single-instance", SettingsValueKind.Boolean, "true", false, false, "SystemSingleInstance", SettingsApplyTiming.Restart),
        new("general.tray", SettingsValueKind.Boolean, "true", false, false, "UiTrayEnabled", SettingsApplyTiming.Immediate),
        new("general.close-to-tray", SettingsValueKind.Boolean, "false", false, false, "UiCloseToTray", SettingsApplyTiming.Immediate),
        new("general.minimize-to-tray", SettingsValueKind.Boolean, "false", false, false, "UiMinimizeToTray", SettingsApplyTiming.Immediate),
        new("appearance.animations-disabled", SettingsValueKind.Boolean, "false", false, false, "SystemDisableUiAnimations", SettingsApplyTiming.Immediate),
        new("appearance.animation-fps", SettingsValueKind.Number, "60", false, false, null, SettingsApplyTiming.Immediate, "fps", 1, 240),
        new("appearance.lock-window", SettingsValueKind.Boolean, "false", false, false, "UiLockWindowSize", SettingsApplyTiming.Immediate),
        new("appearance.hardware-acceleration-disabled", SettingsValueKind.Boolean, "false", false, false, "SystemDisableHardwareAcceleration", SettingsApplyTiming.Restart),
        new("appearance.theme-mode", SettingsValueKind.Enum, "2", false, false, "UiDarkMode", SettingsApplyTiming.Immediate, Choices: "2|0|1"),
        new("appearance.accent", SettingsValueKind.Enum, "blue", false, false, "UiAccentColor", SettingsApplyTiming.Immediate, Choices: "blue|purple|green|orange"),
        new("java.runtime", SettingsValueKind.Path, "", true, true, null, SettingsApplyTiming.NextLaunch, Exportable: false),
        new("java.auto-install", SettingsValueKind.Boolean, "false", true, false, null, SettingsApplyTiming.NextLaunch),
        new("java.vendor", SettingsValueKind.Enum, "", true, false, null, SettingsApplyTiming.NextLaunch,
            Choices: "|EclipseTemurin|Microsoft|Zulu|Oracle|Liberica|Corretto|IbmSemeru|Dragonwell|TencentKona|OpenJdk|GraalVmCommunity|JetBrains|Unknown"),
        new("java.compatibility", SettingsValueKind.Boolean, "true", true, false, null, SettingsApplyTiming.NextLaunch),
        new("recovery.keep-history", SettingsValueKind.Boolean, "false", true, false, null, SettingsApplyTiming.NextTask, Exportable: false),
        new("game.memory", SettingsValueKind.Number, "2048", true, true, null, SettingsApplyTiming.NextLaunch, "MiB", 256, 1048576),
        new("game.gpu-preference", SettingsValueKind.Enum, "auto", true, false, null, SettingsApplyTiming.NextLaunch, Choices: "auto|secondary"),
        new("game.renderer", SettingsValueKind.Enum, "auto", true, false, null, SettingsApplyTiming.NextLaunch, Choices: "auto|mesa-software"),
        new("game.system-glfw", SettingsValueKind.Boolean, "false", true, false, "LaunchUseSystemGlfw", SettingsApplyTiming.NextLaunch),
        new("game.window-mode", SettingsValueKind.Enum, "windowed", true, false, null, SettingsApplyTiming.NextLaunch, Choices: "windowed|fullscreen"),
        new("game.width", SettingsValueKind.Number, "854", true, false, "LaunchArgumentWindowWidth", SettingsApplyTiming.NextLaunch, "px", 1, 32768),
        new("game.height", SettingsValueKind.Number, "480", true, false, "LaunchArgumentWindowHeight", SettingsApplyTiming.NextLaunch, "px", 1, 32768),
        new("game.title", SettingsValueKind.Text, "", true, false, "LaunchArgumentTitle", SettingsApplyTiming.NextLaunch),
        new("game.default-isolation", SettingsValueKind.Enum, "all", false, false, null, SettingsApplyTiming.NextTask,
            Choices: "none|loaders|non-release|loaders-or-non-release|all"),
        new("game.launcher-visibility", SettingsValueKind.Enum, "keep", true, false, null, SettingsApplyTiming.NextLaunch,
            Choices: "keep|minimize|hide|hide-and-close"),
        new("game.jvm", SettingsValueKind.Text, LauncherDefaults.TextDefaults["LaunchAdvanceJvm"], true, false, "LaunchAdvanceJvm", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.arguments", SettingsValueKind.Text, "", true, false, "LaunchAdvanceGame", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.wrapper", SettingsValueKind.Text, "", true, false, "LaunchWrapperCommand", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.pre-launch", SettingsValueKind.Text, "", true, false, "LaunchAdvanceRun", SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.pre-launch-wait", SettingsValueKind.Boolean, "true", true, false, "LaunchAdvanceRunWait", SettingsApplyTiming.NextLaunch),
        new("game.auto-repair", SettingsValueKind.Boolean, "true", true, false, "LaunchAutoRepairGame", SettingsApplyTiming.NextLaunch),
        new("game.server", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch),
        new("game.process-priority", SettingsValueKind.Enum, "normal", true, false, null, SettingsApplyTiming.NextLaunch,
            Choices: "normal|below-normal|above-normal|high|real-time"),
        new("network.proxy-mode", SettingsValueKind.Enum, "1", false, false, "SystemHttpProxyType", SettingsApplyTiming.NextTask, Choices: "0|1|2"),
        new("network.proxy-address", SettingsValueKind.Text, "", false, false, "SystemHttpProxy", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.proxy-user", SettingsValueKind.Text, "", false, false, "SystemHttpProxyCustomUsername", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.proxy-password", SettingsValueKind.Text, "", false, false, "SystemHttpProxyCustomPassword", SettingsApplyTiming.NextTask, Exportable: false),
        new("network.doh", SettingsValueKind.Boolean, "true", false, false, "SystemNetEnableDoH", SettingsApplyTiming.NextTask),
        new("network.ip-stack", SettingsValueKind.Enum, "auto", false, false, null, SettingsApplyTiming.NextTask, Choices: "auto|ipv4|ipv6"),
        new("network.bandwidth-kib", SettingsValueKind.Number, "0", false, false, null, SettingsApplyTiming.NextTask, "KiB/s", 0, 1048576),
        new("network.file-concurrency", SettingsValueKind.Number, "8", false, false, null, SettingsApplyTiming.NextTask, "files", 1, 64),
        new("network.file-retry", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.game-source", SettingsValueKind.Enum, "official-first", false, false, null, SettingsApplyTiming.NextTask,
            Choices: "official-first|mirrors-first|official-only"),
        new("install.inherit-vanilla", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.NextTask),
        new("diagnostics.telemetry", SettingsValueKind.Boolean, "false", false, false, "TelemetryExperienceProgram", SettingsApplyTiming.Immediate),
        new("diagnostics.log-level", SettingsValueKind.Enum, "auto", false, false, null, SettingsApplyTiming.Immediate, Choices: "auto|0|1|2|3|4"),
        new("diagnostics.log-lines", SettingsValueKind.Number, "500", false, false, null, SettingsApplyTiming.Immediate, "entries", 50, 2000),
        new("diagnostics.disk-log-days", SettingsValueKind.Number, "7", false, false, null, SettingsApplyTiming.Immediate, "days", 1, 90),
        new("updates.channel", SettingsValueKind.Enum, "build", false, false, null, SettingsApplyTiming.NextTask, Choices: "build|stable|alpha|beta|ci"),
        new("updates.auto-check", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("game.environment", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.classpath-head", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.post-exit", SettingsValueKind.Text, "", true, false, null, SettingsApplyTiming.NextLaunch, Exportable: false),
        new("game.safe-launch", SettingsValueKind.Boolean, "false", true, false, null, SettingsApplyTiming.NextLaunch),
        new("general.autostart", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("general.file-association", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("general.native-notifications", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("general.clipboard-detection", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("appearance.window-opacity", SettingsValueKind.Number, "100", false, false, null, SettingsApplyTiming.Immediate, "%", 40, 100),
        new("appearance.window-blur", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("network.provider-modrinth", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.provider-curseforge", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.provider-official", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.provider-mirror", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.trace", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.NextTask),
        new("network.auto-diagnose", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.NextTask),
        new("appearance.custom-theme", SettingsValueKind.Text, "", false, false, null, SettingsApplyTiming.Immediate),
        new("appearance.logo-path", SettingsValueKind.Text, "", false, false, null, SettingsApplyTiming.Immediate, Exportable: false),
        new("appearance.background-path", SettingsValueKind.Text, "", false, false, null, SettingsApplyTiming.Immediate, Exportable: false),
        new("appearance.background-fit", SettingsValueKind.Enum, "cover", false, false, null, SettingsApplyTiming.Immediate, Choices: "cover|contain|stretch"),
        new("appearance.background-color", SettingsValueKind.Text, "auto", false, false, null, SettingsApplyTiming.Immediate),
        new("appearance.reduced-motion", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("appearance.background-opacity", SettingsValueKind.Number, "100", false, false, null, SettingsApplyTiming.Immediate, "%", 0, 100),
        new("appearance.video-path", SettingsValueKind.Text, "", false, false, null, SettingsApplyTiming.Immediate, Exportable: false),
        new("appearance.video-auto-pause", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("music.enabled", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("music.path", SettingsValueKind.Text, "", false, false, null, SettingsApplyTiming.Immediate, Exportable: false),
        new("music.startup", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Restart),
        new("music.autoplay", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("music.shuffle", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("music.volume", SettingsValueKind.Number, "50", false, false, null, SettingsApplyTiming.Immediate, "%", 0, 100),
        new("music.media-controls", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("general.jump-list", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("general.notification-actions", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("general.startup-page", SettingsValueKind.Enum, "launch", false, false, null, SettingsApplyTiming.NextLaunch,
            Choices: "launch|install|resources|settings|java|storage|about|tasks"),
        new("general.launch-hints", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("network.auto-install-dependencies", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("network.resource-source", SettingsValueKind.Enum, "follow-request", false, false, null, SettingsApplyTiming.NextTask,
            Choices: "follow-request|official-first|mirrors-first"),
        new("music.auto-pause", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.Immediate),
        new("network.background-download", SettingsValueKind.Boolean, "true", false, false, null, SettingsApplyTiming.NextTask),
        new("diagnostics.ai.enabled", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
        new("diagnostics.ai.reasoning", SettingsValueKind.Enum, "provider", false, false, null, SettingsApplyTiming.Immediate, Choices: "provider|low|medium|high"),
        new("storage.backup-keep-count", SettingsValueKind.Number, "32", false, false, null, SettingsApplyTiming.Immediate, "backups", 1, 1024),
        new("developer.enabled", SettingsValueKind.Boolean, "false", false, false, null, SettingsApplyTiming.Immediate),
    ]);
    public static FrozenDictionary<string, SettingsPolicyDefinition> ByKey { get; } = Definitions.ToFrozenDictionary(item => item.Key, StringComparer.Ordinal);
}

public sealed record SettingsOverride(SettingsOverrideMode Mode, string? Value = null);
public sealed record SettingsEffectiveValue(string Key, SettingsOverride Value, SettingsLayer Source, SettingsApplyTiming Timing, string? ValidationError)
{
    public IReadOnlyList<string> ArgumentRows
    {
        get
        {
            if (Key is not ("game.jvm" or "game.arguments")) return [];
            List<string> rows = [];
            var token = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char character in Value.Value ?? "")
            {
                if (character == '"') quoted = !quoted;
                if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (token.Length > 0) { rows.Add(token.ToString()); token.Clear(); }
                }
                else token.Append(character);
            }
            if (token.Length > 0) rows.Add(token.ToString());
            return rows.AsReadOnly();
        }
    }
}
public sealed record SettingsEffectiveSnapshot(long Revision, IReadOnlyList<SettingsEffectiveValue> Values)
{
    public Minecraft.Launch.MinecraftLaunchOverlay Overlay { get; init; } = new();
}
public sealed record SettingsMutation(string Key, SettingsLayer Layer, SettingsOverride Value, string? InstanceId = null)
{
    public string? ProfileId { get; init; }
    public string? TemporaryId { get; init; }
}
public sealed record SettingsEffectiveQuery(string? InstanceId = null)
{
    public string? ProfileId { get; init; }
    public string? TemporaryId { get; init; }
}
public sealed record SettingsImportQuery(string Document, string? InstanceId = null);
public sealed record SettingsImportPreview(long Revision, IReadOnlyList<SettingsMutation> Changes, IReadOnlyList<string> Errors);
public sealed record SettingsImportCommand(string Document, long ExpectedRevision, string? InstanceId = null);
public sealed record SettingsExportQuery(string? InstanceId = null);
public sealed record SettingsBatchCommand(IReadOnlyList<SettingsMutation> Changes, long ExpectedRevision);
public sealed record SettingsPreviewQuery(IReadOnlyList<SettingsMutation> Changes, string? InstanceId = null)
{
    public string? ProfileId { get; init; }
    public string? TemporaryId { get; init; }
}
public sealed record SettingsResetQuery(string? InstanceId = null);
public sealed record SettingsResetPreview(long Revision, IReadOnlyList<SettingsMutation> Changes, IReadOnlyList<string> Errors);
public sealed record SettingsResetCommand(long ExpectedRevision, string? InstanceId = null);
