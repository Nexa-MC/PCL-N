using System.Text.Json.Nodes;

namespace Nexa.Services.Settings;

public sealed partial class SettingsPolicyService
{
    // Local recovery only: includes private launch arguments, never a sharing/telemetry payload.
    internal string CaptureRecoverySettings(string instanceDirectory)
    {
        lock (_profileGate) return CaptureRecoverySettingsCore(instanceDirectory);
    }

    private string CaptureRecoverySettingsCore(string instanceDirectory)
    {
        string instance = InstanceKey(instanceDirectory) ?? throw new InvalidDataException("实例目录不能为空。");
        RequireDurableRecoveryScope(instance);
        var snapshot = _settings.ReadBatch();
        if (_settings.LoadError is not null) throw new InvalidDataException("无法读取完整启动设置。");
        var document = ReadDocument(snapshot.Values);
        var effective = Resolve(snapshot.Revision, snapshot.Values, document, instance);
        var (_, _, local) = RecoveryScope(document, instance);
        JsonObject values = new();
        foreach (var definition in SettingsPolicySchema.Definitions.Where(item => item.InstanceOverride && !item.Key.StartsWith("recovery.", StringComparison.Ordinal)))
        {
            var value = effective.Values.Single(item => item.Key == definition.Key);
            if (value.ValidationError is not null) throw new InvalidDataException("无法备份无效启动设置。");
            values[definition.Key] = new JsonObject
            {
                ["override"] = local?[definition.Key]?.DeepClone(),
                ["effective"] = Encode(value.Value)
            };
        }
        string result = new JsonObject { ["version"] = 1, ["instance"] = instance, ["values"] = values }.ToJsonString();
        if (result.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
        return result;
    }

    // Planning only. The recovery transaction must atomically coordinate files and settings.
    internal SettingsImportPreview PreviewRecoverySettings(string instanceDirectory, string baseline)
    {
        lock (_profileGate) return PreviewRecoverySettingsCore(instanceDirectory, baseline);
    }

    private SettingsImportPreview PreviewRecoverySettingsCore(string instanceDirectory, string baseline)
    {
        var snapshot = _settings.ReadBatch();
        List<SettingsMutation> changes = [];
        List<string> errors = [];
        try
        {
            string instance = InstanceKey(instanceDirectory) ?? throw new InvalidDataException("实例目录不能为空。");
            RequireDurableRecoveryScope(instance);
            if (_settings.LoadError is not null) throw new InvalidDataException("无法读取完整启动设置。");
            if (baseline.Length > 1024 * 1024) throw new InvalidDataException("启动设置快照超过大小限制。");
            var input = JsonNode.Parse(baseline) as JsonObject;
            if (input?["version"]?.GetValue<int>() != 1 || input["instance"]?.GetValue<string>() != instance
                || input["values"] is not JsonObject values)
                throw new InvalidDataException("启动设置快照不属于当前实例。");
            var definitions = SettingsPolicySchema.Definitions.Where(item => item.InstanceOverride && !item.Key.StartsWith("recovery.", StringComparison.Ordinal)).ToArray();
            if (values.Count != definitions.Length || values.Any(item => !SettingsPolicySchema.ByKey.TryGetValue(item.Key, out var definition) || !definition.InstanceOverride))
                throw new InvalidDataException("启动设置快照的字段不完整或不受支持。");
            var document = ReadDocument(snapshot.Values);
            var (layer, profileId, local) = RecoveryScope(document, instance);
            var below = (JsonObject)document.DeepClone();
            if (profileId is not null) ProfileInstance(below, instance).Remove("selected");
            var inherited = Resolve(snapshot.Revision, snapshot.Values, below, profileId is null ? null : instance);
            foreach (var definition in definitions)
            {
                if (values[definition.Key] is not JsonObject item) throw new InvalidDataException("启动设置快照缺少字段。");
                var original = ReadOverride(item["override"]);
                var effective = ReadOverride(item["effective"]) ?? throw new InvalidDataException("启动设置快照缺少有效值。");
                if (definition.Validate(effective) is not null || original is not null && (definition.Validate(original) is not null || original != effective))
                    throw new InvalidDataException("启动设置快照包含无效值。");
                var currentInherited = inherited.Values.Single(value => value.Key == definition.Key);
                if (currentInherited.ValidationError is not null) throw new InvalidDataException("当前继承设置无效。");
                var desired = original ?? (currentInherited.Value == effective ? new(SettingsOverrideMode.Inherit) : effective);
                var current = ReadOverride(local?[definition.Key]) ?? new(SettingsOverrideMode.Inherit);
                if (current != desired) changes.Add(new(definition.Key, layer, desired, instanceDirectory) { ProfileId = profileId });
            }
            _ = PrepareChanges(snapshot.Values, changes);
        }
        catch (Exception error) when (Recoverable(error)) { errors.Add(error.Message); changes.Clear(); }
        return new(snapshot.Revision, changes.AsReadOnly(), errors.AsReadOnly());
    }

    private void RequireDurableRecoveryScope(string instance)
    {
        if (_temporaryProfiles.ContainsKey(instance))
            throw new InvalidDataException("请先撤销 Temporary 配置，再创建或恢复持久启动设置备份。");
    }

    private static (SettingsLayer Layer, string? ProfileId, JsonObject? Values) RecoveryScope(JsonObject document, string instance)
    {
        var scope = ProfileInstance(document, instance);
        string? profileId = scope["selected"]?.GetValue<string>();
        return profileId is null
            ? (SettingsLayer.Instance, null, document["instances"]![instance] as JsonObject)
            : (SettingsLayer.Profile, ProfileIdentity(profileId), RecoveryProfileValues(scope, profileId));
    }

    private static JsonObject RecoveryProfileValues(JsonObject scope, string profileId)
        => scope["profiles"]?[profileId]?["values"] as JsonObject
            ?? throw new InvalidDataException("恢复计划引用的 Profile 已不存在。");

    private static JsonObject? RecoveryMutationValues(JsonObject document, SettingsMutation mutation)
    {
        string instance = InstanceKey(mutation.InstanceId)!;
        return mutation.Layer == SettingsLayer.Profile
            ? RecoveryProfileValues(ProfileInstance(document, instance), mutation.ProfileId!)
            : document["instances"]![instance] as JsonObject;
    }
}
