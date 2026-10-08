using System.Text.Json;

namespace Nexa.Services.Settings;

/// <summary>Final IA positions, independent of implementation availability and persisted values.</summary>
public static partial class SettingsCatalog
{
    public static IReadOnlyList<SettingsCatalogPage> GlobalPages { get; } = Pages("general:通用|appearance:外观|game:游戏|java:Java|network:下载与网络|storage:存储与迁移|privacy:隐私与诊断|platform:平台功能|advanced:更新与高级|about:关于");
    public static IReadOnlyList<SettingsCatalogPage> InstancePages { get; } = Pages("overview:概览|content:内容|worlds:世界|servers:服务器|files:文件|screenshots:截图|recovery:快照与存储|diagnostics:诊断|settings:设置");
    public static IReadOnlyList<SettingsCatalogPage> InstanceSettingsSections { get; } = Pages("basic:基本|java:Java|resources:资源与性能|window:窗口与启动|hooks:JVM 与 Hooks|profiles:启动配置|servers:服务器|security:更新与安全|backup:本地备份|advanced:高级");
    public static IReadOnlyList<SettingsCatalogEntry> Entries { get; } = Load();
    public static SettingsCatalogSnapshot Read(SettingsCatalogQuery query) => new(GlobalPages, InstancePages, InstanceSettingsSections,
        query.DeveloperOptions ? Entries : Array.AsReadOnly(Entries.Where(item => !item.DeveloperOnly).ToArray()));
    private static System.Collections.ObjectModel.ReadOnlyCollection<SettingsCatalogPage> Pages(string source) => Array.AsReadOnly(source.Split('|').Select(item =>
    { var pair = item.Split(':'); return new SettingsCatalogPage(pair[0], pair[1]); }).ToArray());
    private static System.Collections.ObjectModel.ReadOnlyCollection<SettingsCatalogEntry> Load()
    {
        using var stream = typeof(SettingsCatalog).Assembly.GetManifestResourceStream("Nexa.Services.Settings.SettingsCatalog.json")
            ?? throw new InvalidOperationException("Settings catalog resource is missing.");
        using var document = JsonDocument.Parse(stream);
        var result = document.RootElement.EnumerateArray().Select(row => new SettingsCatalogEntry(
            row.GetProperty("id").GetString()!, row.GetProperty("parent").GetString(), row.GetProperty("scope").GetString()!,
            row.GetProperty("page").GetString()!, row.GetProperty("section").GetString()!, row.GetProperty("label").GetString()!,
            Enum.Parse<SettingsCatalogEntryKind>(row.GetProperty("kind").GetString()!), row.GetProperty("key").GetString(),
            row.GetProperty("developer").GetBoolean(), ResolveAvailability(row))).ToArray();
        if (result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new InvalidOperationException("Settings catalog identifiers must be unique.");
        return Array.AsReadOnly(result);
    }

    private static SettingsCapabilityAvailability ResolveAvailability(JsonElement row)
    {
        string? key = row.GetProperty("key").GetString();
        if (key == "game.title" && !OperatingSystem.IsWindows()
            || key == "general.file-association" && OperatingSystem.IsMacOS()
            || key is "game.gpu-preference" or "game.renderer" && !OperatingSystem.IsLinux()
            || key == "appearance.hardware-acceleration-disabled" && OperatingSystem.IsMacOS()
            || key == "general.jump-list" && !OperatingSystem.IsWindows())
            return SettingsCapabilityAvailability.PlatformUnsupported;
        var declared = Enum.Parse<SettingsCapabilityAvailability>(row.GetProperty("availability").GetString()!);
        if (declared is SettingsCapabilityAvailability.PlatformUnsupported
            or SettingsCapabilityAvailability.DependencyMissing or SettingsCapabilityAvailability.TemporarilyUnavailable)
            return declared;
        return key is { } policyKey
                    && policyKey != "java.compatibility" && SettingsPolicySchema.ByKey.ContainsKey(policyKey)
                || SettingsCatalogConsumerMap.Contains(row.GetProperty("id").GetString()!)
                || row.GetProperty("id").GetString() is "global.storage.launcher-data" or "global.storage.move-launcher-data" or "global.storage.finished-tasks" or "global.storage.0f96f92a2c85" or "global.storage.e54a4c664f84" or "global.storage.5ac61e098807" or "global.privacy.ea4ad91ab2ba" or "global.privacy.25ff9ed3a4bf" or "global.java.6c6d4d76291f" or "global.java.64589a6632ae" or "global.advanced.0a2a572d1179" or "global.advanced.f629377e6bbc" or "global.advanced.c9b13355b1bc" or "global.java.c3e7c58d3f01" or "global.java.98b7b2d91fdf" or "global.java.dc83e72f0f27" or "global.java.083d3d8fb21c" or "global.java.10b4985ac691" or "global.java.a2b8901854e7" or "global.java.3c069c85eeed" or "global.java.ab5a57e0ac74" or "global.java.d0186e8f64ba"
                || row.GetProperty("id").GetString() is "global.advanced.01c0534c8b37" or "global.advanced.8d723d8dcdfb"
                || row.GetProperty("kind").GetString() is "Group" or "Choice" && declared == SettingsCapabilityAvailability.Available
                    ? SettingsCapabilityAvailability.Available : SettingsCapabilityAvailability.NotImplemented;
    }
}
