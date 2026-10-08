using System.Text.Json.Nodes;

namespace Nexa.Services.Settings;

internal sealed record RenameSettingsPlan(string Source, string Destination, string? Value)
{
    public string? Profiles { get; init; }
}

public sealed partial class SettingsPolicyService
{
    internal RenameSettingsPlan PrepareRenameSettings(string source, string destination)
    {
        var snapshot = _settings.ReadBatch();
        if (_settings.LoadError is not null) throw new IOException("无法读取实例设置。");
        string from = InstanceKey(source)!, to = InstanceKey(destination)!;
        var document = ReadDocument(snapshot.Values);
        var instances = (JsonObject)document["instances"]!;
        if (from != to && instances[to] is not null) throw new IOException("目标名称已有实例设置。");
        var profiles = ProfileInstances(document);
        if (from != to && profiles[to] is not null) throw new IOException("目标名称已有启动配置。");
        lock (_profileGate)
            if (_temporaryProfiles.ContainsKey(from)) throw new IOException("请先撤销临时启动覆盖，再改名实例。");
        return new(source, destination, instances[from]?.ToJsonString()) { Profiles = profiles[from]?.ToJsonString() };
    }

    internal void ApplyRenameSettings(RenameSettingsPlan plan, bool reverse)
    {
        lock (_profileGate)
        {
            var snapshot = _settings.ReadBatch();
            if (_settings.LoadError is not null) throw new IOException("无法读取实例设置。");
            string from = InstanceKey(plan.Source)!, to = InstanceKey(plan.Destination)!;
            if (from == to) return;
            if (_temporaryProfiles.ContainsKey(from) || _temporaryProfiles.ContainsKey(to))
                throw new IOException("实例在改名期间启用了临时启动覆盖，请先撤销后重试。");
            var document = ReadDocument(snapshot.Values);
            var instances = (JsonObject)document["instances"]!;
            var value = plan.Value is null ? null : JsonNode.Parse(plan.Value);
            var profiles = ProfileInstances(document);
            var profileValue = plan.Profiles is null ? null : JsonNode.Parse(plan.Profiles);
            bool before = JsonNode.DeepEquals(instances[from], value) && instances[to] is null;
            bool after = instances[from] is null && JsonNode.DeepEquals(instances[to], value);
            before &= JsonNode.DeepEquals(profiles[from], profileValue) && profiles[to] is null;
            after &= profiles[from] is null && JsonNode.DeepEquals(profiles[to], profileValue);
            if (reverse ? before : after) return;
            if (!(reverse ? after : before)) throw new IOException("实例设置在改名期间发生变化，未覆盖后续修改。");
            instances.Remove(from); instances.Remove(to);
            if (value is not null) instances[reverse ? from : to] = value.DeepClone();
            profiles.Remove(from); profiles.Remove(to);
            if (profileValue is not null) profiles[reverse ? from : to] = profileValue.DeepClone();
            var result = _settings.SetRawValues(new Dictionary<string, string> { [SettingsPolicySchema.StorageKey] = document.ToJsonString() }, snapshot.Revision);
            if (!result.IsSuccess) throw new IOException(result.Error?.Message ?? "实例设置迁移失败。");
        }
    }
}
