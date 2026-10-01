

namespace Nexa.Services.Capabilities;


/// <summary>
/// Registry definitions for the remaining machine namespaces: GPU memory budget, CPU
/// thermal, and the active power profile. Every definition mirrors Registry 1.1; providers
/// report only what the OS actually exposes — Windows thermal zones that return a sentinel
/// "unknown" value are published as Unknown availability, never as a fabricated number.
/// </summary>
public static class MachineHardwareCatalog
{
    public const string GpuProviderId = "nexa.gpu";
    public const string ThermalProviderId = "nexa.thermal";
    public const string PowerProviderId = "nexa.power";

    // gpu.* — dedicated VRAM budget per the Registry formula: available = budget - usage.
    public static readonly CapabilityDefinition<long> GpuDedicatedBudget = new(
        "gpu.memory.dedicated.budget", "专用显存预算", "显卡", GpuProviderId,
        CapabilityKind.Metric, CapabilityStability.Session, unit: "bytes");
    public static readonly CapabilityDefinition<long> GpuDedicatedCurrentUsage = new(
        "gpu.memory.dedicated.current_usage", "专用显存当前占用", "显卡", GpuProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");
    public static readonly CapabilityDefinition<long> GpuDedicatedAvailableBudget = new(
        "gpu.memory.dedicated.available_budget", "显存可用预算", "显卡", GpuProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic,
        ["gpu.memory.dedicated.budget", "gpu.memory.dedicated.current_usage"], "bytes");

    // thermal.*
    public static readonly CapabilityDefinition<double> ThermalCpuTemperature = new(
        "thermal.cpu.temperature", "CPU 温度", "散热", ThermalProviderId,
        CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "°C");

    // power.profile.current is owned by MachineEnvironmentCatalog; the power facts this
    // catalog adds are the GPU memory trio and the thermal channel.
    public static CapabilityRegistry MergeInto(CapabilityRegistry registry) => new(
    [
        .. registry.Definitions,
        GpuDedicatedBudget,
        GpuDedicatedCurrentUsage,
        GpuDedicatedAvailableBudget,
        ThermalCpuTemperature,
    ]);
}
