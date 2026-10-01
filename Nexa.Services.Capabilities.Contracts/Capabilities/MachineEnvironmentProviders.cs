

namespace Nexa.Services.Capabilities;


/// <summary>
/// Registry definitions for the machine-fact namespaces that the first slice deferred:
/// display, storage, filesystem and power. Every definition mirrors Registry 1.1; providers
/// report facts they can truly observe and mark everything else unavailable — never
/// synthesized (no marketing VRAM, no invented thermal numbers).
/// </summary>
public static class MachineEnvironmentCatalog
{
    public const string DisplayProviderId = "nexa.display";
    public const string StorageProviderId = "nexa.storage";
    public const string FilesystemProviderId = "nexa.filesystem";
    public const string PowerProviderId = "nexa.power";

    // display.*
    public static readonly CapabilityDefinition<int> DisplayCount = new("display.count", "显示器数量", "显示", DisplayProviderId);
    public static readonly CapabilityDefinition<bool> DisplayPrimaryInternal = new("display.internal", "主显示器为内建屏", "显示", DisplayProviderId);
    public static readonly CapabilityDefinition<string> DisplayPrimaryResolution = new("display.resolution", "主显示器分辨率", "显示", DisplayProviderId,
        CapabilityKind.Metric, CapabilityStability.Session);
    public static readonly CapabilityDefinition<double> DisplayPrimaryRefreshHz = new("display.refresh.current", "主显示器刷新率", "显示", DisplayProviderId,
        CapabilityKind.Metric, CapabilityStability.Session, unit: "Hz");

    // storage.*
    public static readonly CapabilityDefinition<long> InstanceVolumeFreeBytes = new("storage.device.capacity.free", "实例所在卷剩余空间", "存储", StorageProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");

    // filesystem.*
    // Path/volume facts belong to the storage provider (nexa.storage) — that is the
    // provider whose CollectAsync produces them, and the broker enforces ownership.
    public static readonly CapabilityDefinition<bool> InstancePathExists = new("filesystem.path.exists", "实例路径存在", "文件系统", StorageProviderId,
        CapabilityKind.Fact, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> InstancePathWritable = new("filesystem.path.writable", "实例路径可写", "文件系统", StorageProviderId,
        CapabilityKind.Fact, CapabilityStability.Dynamic);
    public static readonly CapabilityDefinition<bool> FilesystemCaseSensitive = new("filesystem.case_sensitive", "文件系统大小写敏感", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Static);
    public static readonly CapabilityDefinition<bool> FilesystemSymlink = new("filesystem.symlink", "符号链接支持", "文件系统", FilesystemProviderId,
        CapabilityKind.Fact, CapabilityStability.Static);
    public static readonly CapabilityDefinition<bool> FilesystemReflink = new("filesystem.reflink", "文件克隆", "文件系统", FilesystemProviderId,
        CapabilityKind.Action, CapabilityStability.Static);

    // power.*
    public static readonly CapabilityDefinition<string> PowerSource = new("power.source", "电源来源", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<bool> PowerBatteryPresent = new("power.battery.present", "存在电池", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<int> PowerBatteryLevelPercent = new("power.battery.level", "电池电量", "电源", PowerProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "%");
    public static readonly CapabilityDefinition<bool> PowerBatteryCharging = new("power.battery.charging", "电池充电中", "电源", PowerProviderId);
    public static readonly CapabilityDefinition<string> PowerProfileCurrent = new("power.profile.current", "电源模式", "电源", PowerProviderId);

    public static CapabilityRegistry MergeInto(CapabilityRegistry registry) => new(
        [.. registry.Definitions,
            DisplayCount, DisplayPrimaryInternal, DisplayPrimaryResolution, DisplayPrimaryRefreshHz,
            InstanceVolumeFreeBytes,
            InstancePathExists, InstancePathWritable, FilesystemCaseSensitive, FilesystemSymlink, FilesystemReflink,
            PowerSource, PowerBatteryPresent, PowerBatteryLevelPercent, PowerBatteryCharging, PowerProfileCurrent]);
}
