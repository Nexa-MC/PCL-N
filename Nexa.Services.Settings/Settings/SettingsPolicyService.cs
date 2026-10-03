using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Xsr;


namespace Nexa.Services.Settings;

/// <summary>Owns policy resolution. Uses the existing durable store for both legacy keys and layered data.</summary>
public sealed partial class SettingsPolicyService
{
    private readonly SettingsService _settings;
    public SettingsPolicyService(SettingsService settings)
    {
        _settings = settings;
        var id = settings.StateStore.Resolve(SettingsPolicyContract.RevisionKey);
        settings.StateStore.Publish(id, settings.Revision);
        settings.Changed += revision => settings.StateStore.Publish(id, revision);
    }

    private static XsrError Invalid(string message) => SettingsErrors.InvalidValue("policy", message);
    private static JsonObject ReadDocument(IReadOnlyDictionary<string, string> values)
    {
        var document = JsonNode.Parse(values.GetValueOrDefault(SettingsPolicySchema.StorageKey) ?? SettingsPolicySchema.EmptyDocument) as JsonObject;
        if (document is null || document["version"]?.GetValue<int>() != 1 || document["global"] is not JsonObject || document["instances"] is not JsonObject)
            throw new InvalidDataException("Unsupported layered settings document; no changes were made.");
        return document;
    }
    private static string? InstanceKey(string? instance)
    {
        if (instance is null) return null;
        if (!Path.IsPathFullyQualified(instance)) throw new InvalidDataException("Instance identity must be its fully qualified directory.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(instance));
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }
    private static SettingsOverride? ReadOverride(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject obj || obj["mode"]?.GetValue<string>() is not ("Auto" or "Custom")
            || !Enum.TryParse<SettingsOverrideMode>(obj["mode"]?.GetValue<string>(), out var mode))
            throw new InvalidDataException("Invalid stored override.");
        return new(mode, obj["value"]?.GetValue<string>());
    }
    private static JsonObject Encode(SettingsOverride value) => new() { ["mode"] = value.Mode.ToString(), ["value"] = value.Value };
    private static bool Recoverable(Exception error) => error is JsonException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException;

    public XsrResult<SettingsEffectiveSnapshot> Read(SettingsEffectiveQuery query)
    {
        try
        {
            var snapshot = _settings.ReadBatch();
            return XsrResult.Success(Resolve(snapshot.Revision, snapshot.Values, ReadDocument(snapshot.Values), InstanceKey(query.InstanceId)));
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure<SettingsEffectiveSnapshot>(Invalid(error.Message)); }
    }

    // Preview-only overlays: not exposed through mutation routes until those capabilities exist.
    internal static SettingsEffectiveValue ResolveValue(SettingsPolicyDefinition definition,
        SettingsOverride? global, SettingsOverride? instance, SettingsOverride? profile = null, SettingsOverride? temporary = null)
    {
        var value = definition.SupportsAuto ? new SettingsOverride(SettingsOverrideMode.Auto) : new(SettingsOverrideMode.Custom, definition.DefaultValue);
        var source = SettingsLayer.Builtin;
        foreach (var item in new[] { (SettingsLayer.Global, global), (SettingsLayer.Instance, instance), (SettingsLayer.Profile, profile), (SettingsLayer.Temporary, temporary) })
        {
            if (item.Item2 is null || item.Item2.Mode == SettingsOverrideMode.Inherit) continue;
            if (item.Item1 >= SettingsLayer.Instance && !definition.InstanceOverride)
                return new(definition.Key, value, source, definition.Timing, "This setting cannot be overridden by an instance.");
            string? error = definition.Validate(item.Item2);
            if (error is not null) return new(definition.Key, value, source, definition.Timing, error);
            value = item.Item2; source = item.Item1;
        }
        return new(definition.Key, value, source, definition.Timing, null);
    }

    private static SettingsEffectiveSnapshot Resolve(long revision, IReadOnlyDictionary<string, string> raw, JsonObject document, string? instance)
    {
        var global = (JsonObject)document["global"]!;
        var local = instance is null ? null : document["instances"]![instance] as JsonObject;
        if (instance is not null && document["instances"]![instance] is not null && local is null)
            throw new InvalidDataException("Invalid instance settings.");
        var values = SettingsPolicySchema.Definitions.Select(definition =>
        {
            SettingsOverride? inherited = ReadOverride(global[definition.Key]);
            if (definition.LegacyKey is { } key && raw.TryGetValue(key, out string? legacy) && (inherited is not null || legacy != definition.DefaultValue))
                inherited = new(SettingsOverrideMode.Custom, legacy);
            if (definition.Key == "general.language" && inherited is { Mode: SettingsOverrideMode.Custom, Value: { } language })
            {
                string normalized = language.Replace('_', '-');
                string? canonical = normalized.Equals("auto", StringComparison.OrdinalIgnoreCase) ? "auto"
                    : normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || normalized.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)
                        || normalized.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) || normalized.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ? "zh-Hant"
                    : normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-Hans"
                    : normalized.Equals("en", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? "en" : null;
                if (canonical is not null) inherited = new(SettingsOverrideMode.Custom, canonical);
            }
            if (inherited is null && definition.Key == "game.window-mode" && raw.GetValueOrDefault("LaunchArgumentWindowType") == "0")
                inherited = new(SettingsOverrideMode.Custom, "fullscreen");
            if (inherited is null && definition.Key == "game.launcher-visibility" && raw.GetValueOrDefault("LaunchArgumentVisible") is "0" or "2" or "3" or "4")
                inherited = new(SettingsOverrideMode.Custom, raw["LaunchArgumentVisible"] switch
                { "0" or "2" => "hide-and-close", "3" => "hide", _ => "minimize" });
            if (inherited is null && definition.Key == "updates.auto-check" && raw.GetValueOrDefault("SystemUpdateMode") == "3")
                inherited = new(SettingsOverrideMode.Custom, "false");
            if (inherited is null && definition.Key == "diagnostics.log-level" && raw.GetValueOrDefault("SystemLogLevel") is { } level && level != "2")
                inherited = new(SettingsOverrideMode.Custom, level);
            if (inherited is null && definition.Key == "diagnostics.log-lines" && raw.GetValueOrDefault("SystemMaxLog") is { } logSlider
                && logSlider != "13" && int.TryParse(logSlider, out int logValue))
            {
                long lines = logValue switch { <= 5 => (long)logValue * 10 + 50, <= 13 => (long)logValue * 50 - 150, <= 28 => (long)logValue * 100 - 800, _ => 2000 };
                inherited = new(SettingsOverrideMode.Custom, Math.Clamp(lines, 50, 2000).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (inherited is null && definition.Key == "game.process-priority" && raw.GetValueOrDefault("LaunchArgumentPriority") is "0" or "2" or "3" or "4")
                inherited = new(SettingsOverrideMode.Custom, raw["LaunchArgumentPriority"] switch
                { "0" => "above-normal", "2" => "below-normal", "3" => "high", _ => "real-time" });
            if (inherited is null && definition.Key == "appearance.animation-fps"
                && raw.GetValueOrDefault("UiAniFPS") is { } frameSlider && frameSlider != "59"
                && int.TryParse(frameSlider, out int frames))
                inherited = new(SettingsOverrideMode.Custom, Math.Clamp((long)frames + 1, 1, 240).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (inherited is null && definition.Key == "network.game-source"
                && raw.GetValueOrDefault("ToolDownloadSource") is "0" or "2")
                inherited = new(SettingsOverrideMode.Custom, raw["ToolDownloadSource"] == "0" ? "mirrors-first" : "official-only");
            if (inherited is null && definition.Key == "network.file-concurrency"
                && raw.GetValueOrDefault("ToolDownloadThread") is { } threadSlider && threadSlider != "63"
                && int.TryParse(threadSlider, out int threads))
                inherited = new(SettingsOverrideMode.Custom, Math.Clamp((long)threads + 1, 1, 64).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (inherited is null && definition.Key == "game.memory" && raw.GetValueOrDefault("LaunchRamType") == "1"
                && int.TryParse(raw.GetValueOrDefault("LaunchRamCustom"), out int slider))
            {
                double gib = slider switch { <= 12 => slider * 0.1 + 0.3, <= 25 => (slider - 12) * 0.5 + 1.5, <= 33 => slider - 25 + 8, _ => (slider - 33) * 2 + 16 };
                inherited = new(SettingsOverrideMode.Custom, Math.Max(256, (int)Math.Round(gib * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return ResolveValue(definition, inherited, ReadOverride(local?[definition.Key]));
        }).ToArray();
        return new(revision, Array.AsReadOnly(values));
    }

    public XsrResult Set(SettingsMutation command) => Apply([command], null);
    public XsrResult SetBatch(SettingsBatchCommand command) => Apply(command.Changes.ToArray(), command.ExpectedRevision);

    private XsrResult Apply(IReadOnlyList<SettingsMutation> mutations, long? expectedRevision)
    {
        try
        {
            var snapshot = _settings.ReadBatch();
            if (_settings.LoadError is { } loadError && snapshot.Revision == 0) return XsrResult.Failure(loadError);
            if (expectedRevision is { } expected && expected != snapshot.Revision)
                return XsrResult.Failure(new(XsrErrorKind.Rejected, XsrSemanticId.Parse("settings.stale_revision"), "Settings changed; preview again."));
            return _settings.SetRawValues(PrepareChanges(snapshot.Values, mutations), snapshot.Revision);
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure(Invalid(error.Message)); }
    }

    private static Dictionary<string, string> PrepareChanges(IReadOnlyDictionary<string, string> raw, IReadOnlyList<SettingsMutation> mutations)
    {
        var document = ReadDocument(raw);
        var writes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mutation in mutations)
        {
            string? error = ValidateMutation(mutation);
            if (error is not null) throw new InvalidDataException(error);
            var definition = SettingsPolicySchema.ByKey[mutation.Key];
            string? instance = InstanceKey(mutation.InstanceId);
            JsonObject target = (JsonObject)document["global"]!;
            if (mutation.Layer == SettingsLayer.Instance)
            {
                var instances = (JsonObject)document["instances"]!;
                if (instances[instance!] is null) instances[instance!] = new JsonObject();
                target = instances[instance!] as JsonObject ?? throw new InvalidDataException("Invalid instance settings.");
            }
            if (mutation.Value.Mode == SettingsOverrideMode.Inherit) target.Remove(mutation.Key);
            else target[mutation.Key] = Encode(mutation.Value);
            if (mutation.Layer == SettingsLayer.Global && definition.LegacyKey is { } key)
                writes[key] = mutation.Value.Mode == SettingsOverrideMode.Inherit ? definition.DefaultValue : mutation.Value.Value!;
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "game.window-mode")
                writes["LaunchArgumentWindowType"] = mutation.Value.Value == "fullscreen" ? "0" : "1";
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "game.launcher-visibility")
                writes["LaunchArgumentVisible"] = mutation.Value.Value switch
                { "hide-and-close" => "2", "hide" => "3", "minimize" => "4", _ => "5" };
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "game.process-priority")
                writes["LaunchArgumentPriority"] = mutation.Value.Value switch
                { "above-normal" => "0", "below-normal" => "2", "high" => "3", "real-time" => "4", _ => "1" };
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "appearance.animation-fps")
                writes["UiAniFPS"] = mutation.Value.Mode == SettingsOverrideMode.Inherit ? "59"
                    : (int.Parse(mutation.Value.Value!, System.Globalization.CultureInfo.InvariantCulture) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "diagnostics.log-level")
                writes["SystemLogLevel"] = mutation.Value.Value is "0" or "1" or "2" or "3" or "4" ? mutation.Value.Value : "2";
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "diagnostics.log-lines" && mutation.Value.Mode == SettingsOverrideMode.Inherit)
                writes["SystemMaxLog"] = "13";
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "network.game-source")
                writes["ToolDownloadSource"] = mutation.Value.Value switch { "mirrors-first" => "0", "official-only" => "2", _ => "1" };
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "network.file-concurrency")
                writes["ToolDownloadThread"] = mutation.Value.Mode == SettingsOverrideMode.Inherit ? "63"
                    : (int.Parse(mutation.Value.Value!, System.Globalization.CultureInfo.InvariantCulture) - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (mutation.Layer == SettingsLayer.Global && mutation.Key == "game.memory" && mutation.Value.Mode != SettingsOverrideMode.Custom)
                writes["LaunchRamType"] = "0";
        }
        // Validate the effective combination, not just individual input fields.
        var merged = new Dictionary<string, string>(raw, StringComparer.Ordinal);
        foreach (var pair in writes) merged[pair.Key] = pair.Value;
        foreach (string? instance in mutations.Select(item => InstanceKey(item.InstanceId)).Append(null).Distinct())
        {
            var result = Resolve(0, merged, document, instance);
            if (result.Values.Any(item => item.ValidationError is not null)) throw new InvalidDataException("The effective policy contains an invalid value.");
            var proxy = result.Values.Single(item => item.Key == "network.proxy-mode");
            if (proxy.Value.Value == "2")
            {
                string? address = result.Values.Single(item => item.Key == "network.proxy-address").Value.Value;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5"))
                    throw new InvalidDataException("Custom proxy requires an HTTP, HTTPS or SOCKS5 address.");
            }
        }
        merged[SettingsPolicySchema.StorageKey] = document.ToJsonString();
        return merged;
    }

    public XsrResult<SettingsEffectiveSnapshot> Preview(SettingsPreviewQuery query)
    {
        try
        {
            var snapshot = _settings.ReadBatch();
            var prepared = PrepareChanges(snapshot.Values, query.Changes.ToArray());
            return XsrResult.Success(Resolve(snapshot.Revision, prepared, ReadDocument(prepared), InstanceKey(query.InstanceId)));
        }
        catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure<SettingsEffectiveSnapshot>(Invalid(error.Message)); }
    }

    private static string? ValidateMutation(SettingsMutation mutation)
    {
        if (!SettingsPolicySchema.ByKey.TryGetValue(mutation.Key, out var definition)) return "Unknown setting.";
        if (mutation.Layer is not (SettingsLayer.Global or SettingsLayer.Instance)) return "This layer is not editable.";
        if (mutation.Layer == SettingsLayer.Instance && (!definition.InstanceOverride || mutation.InstanceId is null)) return "An instance override is not allowed or has no identity.";
        if (mutation.Layer == SettingsLayer.Global && mutation.InstanceId is not null) return "Global settings cannot carry an instance identity.";
        return definition.Validate(mutation.Value);
    }
}
