using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;
using Nexa.Xsr;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
    private sealed record TemporaryProfile(string Id, IReadOnlyDictionary<string, SettingsOverride> Values, MinecraftLaunchOverlay Overlay);
    private readonly Dictionary<string, TemporaryProfile> _temporaryProfiles = new(StringComparer.Ordinal);
    private readonly object _profileGate = new();

    private static string ProfileIdentity(string value)
    {
        if (value.Length is 0 or > 64 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new InvalidDataException("Profile identity must be 1–64 ASCII letters, digits, '-' or '_'.");
        return value;
    }

    private static JsonObject ProfileInstances(JsonObject document)
    {
        if (document["launchProfiles"] is null) document["launchProfiles"] = new JsonObject();
        return document["launchProfiles"] as JsonObject ?? throw new InvalidDataException("Invalid launch profile document.");
    }

    private static JsonObject ProfileInstance(JsonObject document, string instance)
    {
        var instances = ProfileInstances(document);
        if (instances[instance] is null) instances[instance] = new JsonObject { ["profiles"] = new JsonObject() };
        return instances[instance] as JsonObject ?? throw new InvalidDataException("Invalid launch profile scope.");
    }

    private static void ValidateProfileValues(IReadOnlyDictionary<string, SettingsOverride> values)
    {
        if (values.Count > 128) throw new InvalidDataException("A profile contains too many values.");
        foreach (var value in values)
        {
            if (!SettingsPolicySchema.ByKey.TryGetValue(value.Key, out var definition) || !definition.InstanceOverride)
                throw new InvalidDataException("A profile can only override instance settings: " + value.Key);
            if (value.Key == "java.compatibility") throw new InvalidDataException("Java compatibility checks are mandatory; this setting has no mutable consumer.");
            string? error = definition.Validate(value.Value);
            if (error is not null) throw new InvalidDataException(value.Key + ": " + error);
        }
    }

    private static void ValidateOverlay(MinecraftLaunchOverlay overlay)
    {
        foreach (string? source in new[] { overlay.ModsSource, overlay.ResourcePacksSource, overlay.ShaderPacksSource, overlay.ConfigSource })
            if (source is not null && (source.Length > 4096 || !Path.IsPathFullyQualified(source) || source.IndexOfAny(['\0', '\r', '\n']) >= 0))
                throw new InvalidDataException("Overlay source must be a fully qualified directory.");
    }

    private static JsonObject EncodeOverlay(MinecraftLaunchOverlay overlay) => new()
    {
        ["safeLaunch"] = overlay.SafeLaunch,
        ["mods"] = overlay.ModsSource,
        ["resourcepacks"] = overlay.ResourcePacksSource,
        ["shaderpacks"] = overlay.ShaderPacksSource,
        ["config"] = overlay.ConfigSource,
    };

    private static MinecraftLaunchOverlay DecodeOverlay(JsonNode? node)
    {
        if (node is null) return new();
        var obj = node as JsonObject ?? throw new InvalidDataException("Invalid overlay document.");
        var overlay = new MinecraftLaunchOverlay(obj["safeLaunch"]?.GetValue<bool>() ?? false,
            obj["mods"]?.GetValue<string>(), obj["resourcepacks"]?.GetValue<string>(), obj["shaderpacks"]?.GetValue<string>(), obj["config"]?.GetValue<string>());
        ValidateOverlay(overlay); return overlay;
    }

    private static ReadOnlyDictionary<string, SettingsOverride> DecodeProfileValues(JsonNode? node)
    {
        if (node is not JsonObject obj) throw new InvalidDataException("Invalid profile values.");
        var values = obj.ToDictionary(pair => pair.Key, pair => ReadOverride(pair.Value)
            ?? throw new InvalidDataException("Missing profile override."), StringComparer.Ordinal);
        ValidateProfileValues(values);
        return new ReadOnlyDictionary<string, SettingsOverride>(values);
    }

    public XsrResult<SettingsLaunchProfilesSnapshot> ReadLaunchProfiles(SettingsLaunchProfilesQuery query)
    {
        lock (_profileGate)
        {
            try
            {
                var raw = _settings.ReadBatch(); string instance = InstanceKey(query.InstanceId)!;
                var scope = ProfileInstance(ReadDocument(raw.Values), instance);
                var profiles = scope["profiles"] as JsonObject ?? throw new InvalidDataException("Invalid profile inventory.");
                if (profiles.Count > 32) throw new InvalidDataException("Too many stored profiles.");
                var result = profiles.Select(pair =>
                {
                    var profile = pair.Value as JsonObject ?? throw new InvalidDataException("Invalid profile.");
                    return new SettingsLaunchProfile(ProfileIdentity(pair.Key), profile["name"]!.GetValue<string>(),
                        DecodeProfileValues(profile["values"]), DecodeOverlay(profile["overlay"]));
                }).ToArray();
                var temporary = _temporaryProfiles.GetValueOrDefault(instance);
                return XsrResult.Success(new SettingsLaunchProfilesSnapshot(raw.Revision, Array.AsReadOnly(result),
                    scope["selected"]?.GetValue<string>(), temporary?.Id)
                { TemporaryValues = temporary?.Values ?? new Dictionary<string, SettingsOverride>(), TemporaryOverlay = temporary?.Overlay ?? new() });
            }
            catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure<SettingsLaunchProfilesSnapshot>(Invalid(error.Message)); }
        }
    }

    private XsrResult ChangeProfile(string instanceId, long revision, Action<JsonObject, string> change)
    {
        lock (_profileGate)
        {
            try
            {
                var raw = _settings.ReadBatch();
                if (raw.Revision != revision) return XsrResult.Failure(Invalid("Settings changed; refresh the launch profiles."));
                if (_settings.LoadError is { } loadError && raw.Revision == 0) return XsrResult.Failure(loadError);
                string instance = InstanceKey(instanceId)!; var document = ReadDocument(raw.Values);
                change(ProfileInstance(document, instance), instance);
                return _settings.SetRawValues(new Dictionary<string, string> { [SettingsPolicySchema.StorageKey] = document.ToJsonString() }, raw.Revision);
            }
            catch (Exception error) when (Recoverable(error)) { return XsrResult.Failure(Invalid(error.Message)); }
        }
    }

    public XsrResult SaveLaunchProfile(SettingsLaunchProfileSaveCommand command) => ChangeProfile(command.InstanceId, command.ExpectedRevision, (scope, _) =>
    {
        string id = ProfileIdentity(command.ProfileId);
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > 128 || command.Name.Any(char.IsControl))
            throw new InvalidDataException("Profile name must be 1–128 printable characters.");
        ValidateProfileValues(command.Values); ValidateOverlay(command.Overlay);
        var profiles = scope["profiles"] as JsonObject ?? throw new InvalidDataException("Invalid profile inventory.");
        if (profiles[id] is null && profiles.Count == 32) throw new InvalidDataException("An instance supports at most 32 profiles.");
        JsonObject values = new();
        foreach (var pair in command.Values.Where(pair => pair.Value.Mode != SettingsOverrideMode.Inherit)) values[pair.Key] = Encode(pair.Value);
        profiles[id] = new JsonObject { ["name"] = command.Name, ["values"] = values, ["overlay"] = EncodeOverlay(command.Overlay) };
    });

    public XsrResult DeleteLaunchProfile(SettingsLaunchProfileDeleteCommand command) => ChangeProfile(command.InstanceId, command.ExpectedRevision, (scope, _) =>
    {
        (scope["profiles"] as JsonObject ?? throw new InvalidDataException("Invalid profile inventory.")).Remove(ProfileIdentity(command.ProfileId));
        if (scope["selected"]?.GetValue<string>() == command.ProfileId) scope.Remove("selected");
    });

    public XsrResult SelectLaunchProfile(SettingsLaunchProfileSelectCommand command) => ChangeProfile(command.InstanceId, command.ExpectedRevision, (scope, _) =>
    {
        if (command.ProfileId is null) scope.Remove("selected");
        else
        {
            string id = ProfileIdentity(command.ProfileId);
            if (scope["profiles"]?[id] is null) throw new InvalidDataException("The selected profile does not exist.");
            scope["selected"] = id;
        }
    });

    public XsrResult BeginTemporaryLaunch(SettingsTemporaryLaunchBeginCommand command)
    {
        lock (_profileGate)
        {
            TemporaryProfile? previous = null; string? instance = null;
            var result = ChangeProfile(command.InstanceId, command.ExpectedRevision, (scope, identity) =>
            {
                ValidateProfileValues(command.Values); ValidateOverlay(command.Overlay);
                instance = identity; previous = _temporaryProfiles.GetValueOrDefault(identity);
                _temporaryProfiles[identity] = new(ProfileIdentity(command.TemporaryId),
                    new ReadOnlyDictionary<string, SettingsOverride>(new Dictionary<string, SettingsOverride>(command.Values, StringComparer.Ordinal)), command.Overlay);
                scope["temporaryRevision"] = (scope["temporaryRevision"]?.GetValue<long>() ?? 0) + 1;
            });
            if (!result.IsSuccess && instance is not null)
            { if (previous is null) _temporaryProfiles.Remove(instance); else _temporaryProfiles[instance] = previous; }
            return result;
        }
    }

    public XsrResult EndTemporaryLaunch(SettingsTemporaryLaunchEndCommand command)
    {
        lock (_profileGate)
        {
            TemporaryProfile? previous = null; string? instance = null;
            var result = ChangeProfile(command.InstanceId, command.ExpectedRevision, (scope, identity) =>
            {
                instance = identity; previous = _temporaryProfiles.GetValueOrDefault(identity); _temporaryProfiles.Remove(identity);
                scope["temporaryRevision"] = (scope["temporaryRevision"]?.GetValue<long>() ?? 0) + 1;
            });
            if (!result.IsSuccess && instance is not null && previous is not null) _temporaryProfiles[instance] = previous;
            return result;
        }
    }

    public MinecraftLaunchOverlay ReadLaunchOverlay(string instanceId)
    {
        lock (_profileGate)
        {
            string instance = InstanceKey(instanceId)!; var raw = _settings.ReadBatch();
            return ResolveLaunchOverlay(ReadDocument(raw.Values), instance, null, _temporaryProfiles.GetValueOrDefault(instance));
        }
    }

    private static MinecraftLaunchOverlay ResolveLaunchOverlay(JsonObject document, string instance, string? profileId, TemporaryProfile? temporary)
    {
        var scope = ProfileInstance(document, instance);
        string? selected = profileId ?? scope["selected"]?.GetValue<string>();
        var profile = selected is null ? new MinecraftLaunchOverlay() : DecodeOverlay(scope["profiles"]?[selected]?["overlay"]);
        if (temporary is null) return profile;
        return new(profile.SafeLaunch || temporary.Overlay.SafeLaunch,
            temporary.Overlay.ModsSource ?? profile.ModsSource, temporary.Overlay.ResourcePacksSource ?? profile.ResourcePacksSource,
            temporary.Overlay.ShaderPacksSource ?? profile.ShaderPacksSource, temporary.Overlay.ConfigSource ?? profile.ConfigSource);
    }
}
