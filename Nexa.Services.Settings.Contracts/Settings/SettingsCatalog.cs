

namespace Nexa.Services.Settings;

public enum SettingsCatalogEntryKind { Group, Setting, Action, State, Choice }
public sealed record SettingsCatalogPage(string Id, string Label);
public sealed record SettingsCatalogEntry(string Id, string? Parent, string Scope, string Page, string Section,
    string Label, SettingsCatalogEntryKind Kind, string? SettingKey, bool DeveloperOnly, SettingsCapabilityAvailability Availability)
{
    public SettingsPolicyDefinition? Definition => SettingKey is { } key ? SettingsPolicySchema.ByKey[key] : null;
    public bool InvertBoolean => SettingKey == "appearance.animations-disabled";
    public bool IsRuntimeDetail => Scope == "global" && Page == "java" && !DeveloperOnly
        && Section is not ("自动策略" or "已安装 Java" or "Java 管理");
    public string Owner { get; init; } = "Nexa.Services.Settings";
    public string UnavailableReason => Availability == SettingsCapabilityAvailability.NotImplemented ? "尚未可用" : Availability.ToString();
}
public sealed record SettingsCatalogSnapshot(IReadOnlyList<SettingsCatalogPage> GlobalPages,
    IReadOnlyList<SettingsCatalogPage> InstancePages, IReadOnlyList<SettingsCatalogPage> InstanceSettingsSections,
    IReadOnlyList<SettingsCatalogEntry> Entries);
public sealed record SettingsCatalogQuery(bool DeveloperOptions = false);
