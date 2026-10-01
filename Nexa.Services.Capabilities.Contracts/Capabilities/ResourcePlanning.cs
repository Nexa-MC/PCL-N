using System.Collections.Frozen;

namespace Nexa.Services.Capabilities;

public enum ResourceEstimateStatus { NotStarted, Pending, Completed, Failed }

public sealed record ResourceEstimatorProfile(
    string Version,
    long HeapLaunchMiB,
    long HeapRuntimeMiB,
    long NativeLaunchMiB,
    long NativeRuntimeMiB,
    long SafetyMarginMiB)
{
    public IReadOnlyDictionary<string, long> MinecraftBaselineMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal)
        { ["default"] = 1024, ["legacy"] = 768, ["modern"] = 1536 };
    public IReadOnlyDictionary<string, long> LoaderBaselineMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        { ["Vanilla"] = 0, ["Fabric"] = 192, ["Quilt"] = 224, ["Forge"] = 384, ["NeoForge"] = 384 };
    public IReadOnlyDictionary<string, double> Coefficients { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["mod_count_mib"] = 5.5,
            ["class_count_mib"] = 0.012,
            ["resource_weight"] = 1.0,
            ["resource_peak_launch"] = 0.5,
            ["metaspace_class_mib"] = 0.0125,
            ["gc_heap_ratio"] = 0.0625,
            ["graphics_model_ratio"] = 0.5,
            ["heap_estimated_minimum_ratio"] = 0.75,
            ["heap_useful_maximum_ratio"] = 1.5,
        };
    public IReadOnlyDictionary<string, long> SafetyMarginsMiB { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["heap"] = 256,
            ["native"] = 192,
            ["graphics"] = 256,
            ["system"] = 1024,
            ["commit"] = 512,
            ["native_launch"] = 128,
            ["renderer"] = 256,
        };
    public double QuantileTarget { get; init; } = 0.95;
    public IReadOnlyDictionary<string, double> HardwareCorrections { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal) { ["uma"] = 1.15, ["emulated"] = 1.10 };

    public static ResourceEstimatorProfile Default { get; } = new("1.1.0", 2048, 2048, 512, 768, 512);
}

/// <summary>Immutable §56 provenance. All values remain MiB until presentation.</summary>
public sealed record EstimateResult(
    long ValueMiB,
    CapabilityConfidence Confidence,
    string ModelVersion,
    string ProfileVersion,
    IReadOnlyList<string> Inputs,
    double HistoricalWeight,
    long SafetyMarginMiB,
    IReadOnlyList<string> Reasons);

public sealed record ResourceEstimateSnapshot(
    ResourceEstimateStatus Status,
    CapabilityConfidence Confidence,
    EstimateResult HeapLaunch,
    EstimateResult HeapRuntime,
    EstimateResult NativeLaunch,
    EstimateResult NativeRuntime,
    EstimateResult PhysicalLaunch,
    EstimateResult PhysicalRuntime,
    EstimateResult CommitLaunch,
    EstimateResult CommitRuntime);

public sealed record CapabilityPreflightReport(
    IReadOnlyList<CapabilityPreflightIssue> Issues,
    IReadOnlyList<CapabilityPreflightIssue> CollapsedIssues,
    PreflightSeverity OverallSeverity);

public sealed record RemediationDefinition(string Id, string Label, bool RequiresConfirmation);

public sealed record RemediationRequest(string Id, IReadOnlyDictionary<string, string>? Arguments = null,
    bool Confirmed = false);
public sealed record RemediationResult(string Id, bool Succeeded, string Code, string Message);

public interface IRemediationHandler
{
    string Id { get; }
    ValueTask<RemediationResult> ExecuteAsync(RemediationRequest request, CancellationToken cancellationToken);
}

public interface IRemediationAvailability
{
    bool Available { get; }
}

public static class RemediationCatalog
{
    private static readonly RemediationDefinition[] Items =
    [
        new("remediation.memory.adjust_heap", "调整堆内存", true),
        new("remediation.memory.release_background", "释放后台内存", true),
        new("remediation.memory.inspect_commit", "检查提交预算", false),
        new("remediation.memory.open_pagefile_settings", "打开页面文件设置", false),
        new("remediation.java.select", "选择 Java", false),
        new("remediation.java.download", "下载合适的 Java", true),
        new("remediation.java.switch_recommended", "切换到推荐 Java", true),
        new("remediation.gpu.select_high_performance", "选择高性能显卡", true),
        new("remediation.gpu.reduce_resource_settings", "降低资源设置", true),
        new("remediation.loader.repair", "修复加载器", true),
        new("remediation.loader.switch", "切换加载器", true),
        new("remediation.mod.install_dependency", "安装缺失依赖", true),
        new("remediation.mod.resolve_conflict", "处理模组冲突", true),
        new("remediation.game.repair_files", "修复游戏文件", true),
        new("remediation.game.repair_version", "修复游戏版本", true),
        new("remediation.instance.move", "移动实例", true),
        new("remediation.instance.recheck_permissions", "重新检查目录权限", false),
    ];
    private static readonly FrozenDictionary<string, RemediationDefinition> ById =
        Items.ToFrozenDictionary(static item => item.Id, StringComparer.Ordinal);

    public static IReadOnlyList<RemediationDefinition> All => Items;
    public static RemediationDefinition Get(string id) => ById.TryGetValue(id, out RemediationDefinition? item)
        ? item : throw new KeyNotFoundException("Unknown remediation: " + id);
    public static IReadOnlyList<RemediationDefinition> For(CapabilityPreflightIssue issue) =>
        Array.AsReadOnly(issue.Remediations.Select(Get).ToArray());

    public static IReadOnlyList<ICapabilityDefinition> Definitions() => Items.Select(static item =>
        (ICapabilityDefinition)new CapabilityDefinition<bool>(item.Id, item.Label, "修复", "nexa.remediation",
            CapabilityKind.Action, CapabilityStability.Dynamic)).ToArray();
}
