using System.Text.Json.Nodes;
using Nexa.Core;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Minecraft.Java;

/// <summary>The composition adapter keeps Java registry persistence in the durable settings store.</summary>
public sealed class JavaRuntimeRegistrationStore(SettingsService settings) : IJavaRuntimeRegistrationStore
{
    public const string EmptyDocument = "{\"version\":1,\"revision\":0,\"entries\":[]}";
    private readonly object _gate = new();
    public JavaRuntimeRegistrySnapshot Read() => Decode(settings.ReadBatch().Values.GetValueOrDefault(JavaRuntimeInventoryContract.RegistryKey) ?? EmptyDocument);
    private static JavaRuntimeRegistrySnapshot Decode(string raw)
    {
        if (raw.Length > 262144) throw new InvalidDataException("Java 登记数据过大。");
        var document = JsonNode.Parse(raw) as JsonObject;
        if (document?["version"]?.GetValue<int>() != 1 || document["entries"] is not JsonArray rows
            || rows.Count > 64 || document["revision"]?.GetValue<long>() is not { } revision || revision < 0)
            throw new InvalidDataException("Java 登记数据无效。");
        List<JavaRuntimeRegistration> entries = [];
        HashSet<string> seen = new(PathIdentity.Comparer);
        foreach (var row in rows)
        {
            if (row is not JsonObject item || item["path"]?.GetValue<string>() is not { } path
                || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
                || item["enabled"]?.GetValue<bool>() is not { } enabled || item["custom"]?.GetValue<bool>() is not { } custom)
                throw new InvalidDataException("Java 登记项目无效。");
            path = Path.GetFullPath(path);
            if (!seen.Add(path)) throw new InvalidDataException("Java 登记路径重复。");
            entries.Add(new(path, enabled, custom));
        }
        return new(revision, entries.AsReadOnly());
    }
    public XsrResult Write(long expectedRevision, IReadOnlyList<JavaRuntimeRegistration> registrations)
    {
        lock (_gate)
        {
            var current = settings.ReadBatch();
            var before = Decode(current.Values.GetValueOrDefault(JavaRuntimeInventoryContract.RegistryKey) ?? EmptyDocument);
            if (before.Revision != expectedRevision) return XsrResult.Failure(new(XsrErrorKind.Rejected,
                XsrSemanticId.Parse("java.runtime.registry.stale"), "Java 列表已变化，请刷新后重试。"));
            var document = new JsonObject
            {
                ["version"] = 1,
                ["revision"] = checked(expectedRevision + 1),
                ["entries"] = new JsonArray(registrations.Select(entry => (JsonNode)new JsonObject
                { ["path"] = entry.Executable, ["enabled"] = entry.Enabled, ["custom"] = entry.Custom }).ToArray())
            };
            string raw = document.ToJsonString(); _ = Decode(raw);
            return settings.SetRawValues(new Dictionary<string, string> { [JavaRuntimeInventoryContract.RegistryKey] = raw }, current.Revision);
        }
    }
}
