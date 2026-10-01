





namespace Nexa.Services.Capabilities;


public static class MachineCapabilityCatalog
{
    public const string RuntimeProviderId = "nexa.runtime";
    public const string MemoryProviderId = "nexa.memory";
    public static readonly CapabilityDefinition<string> Os = new("platform.os", "操作系统", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> OsVersion = new("platform.os.version", "系统版本", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> NativeArch = new("platform.arch.native", "系统架构", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> ProcessArch = new("platform.arch.process", "进程架构", "系统", RuntimeProviderId);
    public static readonly CapabilityDefinition<string> Runtime = new("runtime.framework", "运行时", "运行环境", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> DynamicCode = new("runtime.dynamic_code", "动态代码", "运行环境", RuntimeProviderId);
    public static readonly CapabilityDefinition<int> LogicalProcessors = new("cpu.topology.logical_processors", "进程可用逻辑处理器", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Sse2 = new("cpu.isa.sse2", "SSE2", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Avx2 = new("cpu.isa.avx2", "AVX2", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<bool> Neon = new("cpu.isa.arm.neon", "ARM Neon", "处理器", RuntimeProviderId);
    public static readonly CapabilityDefinition<long> PhysicalUsable = Memory("memory.physical.usable", "可用物理内存总量");
    public static readonly CapabilityDefinition<long> PhysicalAvailable = Memory("memory.physical.available", "当前可用物理内存");
    public static readonly CapabilityDefinition<long> CommitTotal = Memory("memory.commit.total", "已提交内存");
    public static readonly CapabilityDefinition<long> CommitLimit = Memory("memory.commit.limit", "提交上限");
    public static readonly CapabilityDefinition<long> CommitAvailable = new("memory.commit.available", "剩余提交预算", "内存", MemoryProviderId,
        CapabilityKind.Derived, CapabilityStability.Dynamic, ["memory.commit.total", "memory.commit.limit"], "bytes");
    private static CapabilityDefinition<long> Memory(string id, string label) => new(id, label, "内存", MemoryProviderId, CapabilityKind.Metric, CapabilityStability.Dynamic, unit: "bytes");
    public static CapabilityRegistry CreateRegistry() => new([
        Os, OsVersion, NativeArch, ProcessArch, Runtime, DynamicCode, LogicalProcessors, Sse2, Avx2, Neon,
        PhysicalUsable, PhysicalAvailable, CommitTotal, CommitLimit, CommitAvailable,
    ]);
}
