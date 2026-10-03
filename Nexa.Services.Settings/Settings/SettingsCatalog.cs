using System.Text.Json;

namespace Nexa.Services.Settings;

/// <summary>Final IA positions, independent of implementation availability and persisted values.</summary>
public static class SettingsCatalog
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
            row.GetProperty("developer").GetBoolean(), row.GetProperty("key").GetString() is
                "general.language" or "appearance.animations-disabled" or "appearance.lock-window" or "network.file-concurrency" or "network.file-retry" or "network.game-source" or "recovery.keep-history" or "diagnostics.telemetry" or "game.width" or "game.height" or "game.window-mode" or "game.jvm" or "game.arguments" or "game.memory" or "game.server" or "game.auto-repair" or "java.runtime" or "java.auto-install" or "java.vendor" or "developer.enabled" or "install.inherit-vanilla"
                    ? SettingsCapabilityAvailability.Available : SettingsCapabilityAvailability.NotImplemented)).ToArray();
        if (result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new InvalidOperationException("Settings catalog identifiers must be unique.");
        return Array.AsReadOnly(result);
    }
}
