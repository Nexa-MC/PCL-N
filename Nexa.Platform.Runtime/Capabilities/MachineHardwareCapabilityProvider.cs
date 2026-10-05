using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

/// <summary>
/// GPU adapter memory through DXGI (budget = budget - currentUsage), CPU temperature through
/// the kernel thermal zone, and the active power scheme through the power subsystem. All
/// three are best-effort OS facts: driver-less or locked-down machines report Unknown, and
/// the launcher keeps working — nothing here is a launch prerequisite.
/// </summary>
internal sealed class GpuCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineHardwareCatalog.GpuProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ICapability>>([.. GpuProbes.CollectGpu(timestamp)]);
    }
}

internal sealed class ThermalCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineHardwareCatalog.ThermalProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ICapability> thermal = OperatingSystem.IsWindows()
            ? GpuProbes.CollectThermalWindows(timestamp)
            : OperatingSystem.IsLinux()
                ? GpuProbes.CollectThermalUnix(timestamp)
                : [MachineHardwareCatalog.ThermalCpuTemperature.Unavailable(
                    CapabilityAvailability.PlatformUnsupported, timestamp, OperatingSystem.IsMacOS()
                        ? "macOS 没有公开的免驱动 CPU 温度 API；私有 AppleSMC 协议不在此检测范围"
                        : "此操作系统未提供 CPU 温度检测通道")];
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(thermal);
    }
}

internal sealed class HardwarePowerCapabilityProvider : IMachineCapabilityProvider
{
    public string Id => MachineHardwareCatalog.PowerProviderId;

    public ValueTask<IReadOnlyList<ICapability>> CollectAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ICapability> power = OperatingSystem.IsWindows()
            ? GpuProbes.CollectPowerWindows(timestamp)
            : OperatingSystem.IsLinux()
                ? GpuProbes.CollectPowerUnix(timestamp)
                : OperatingSystem.IsMacOS() ? MacPowerProbe.Collect(timestamp)
                : MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == Id)
                    .Select(item => item.Unavailable(CapabilityAvailability.PlatformUnsupported, timestamp, "此操作系统未提供电源检测通道")).ToList();
        return ValueTask.FromResult<IReadOnlyList<ICapability>>(power);
    }
}

/// <summary>GPU adapter memory through DXGI (available budget = budget - current usage).</summary>
public static class GpuProbes
{
    public static List<ICapability> CollectGpu(DateTimeOffset timestamp)
    {
        const string source = "DXGI IDXGIAdapter3 QueryVideoMemoryInfo";
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                string reason = OperatingSystem.IsLinux()
                    ? "Linux DRM sysfs/NVML 公开整卡容量与全系统占用，不提供 DXGI 等价的当前进程专用显存动态预算"
                    : OperatingSystem.IsMacOS()
                        ? "macOS Metal 推荐工作集与当前 Metal 分配不等同于 DXGI 当前进程专用显存动态预算；统一内存也不属于专用显存"
                        : "此操作系统未提供当前进程专用显存动态预算接口";
                return
                [
                    MachineHardwareCatalog.GpuDedicatedBudget.Unavailable(
                        CapabilityAvailability.PlatformUnsupported, timestamp, reason),
                    MachineHardwareCatalog.GpuDedicatedCurrentUsage.Unavailable(
                        CapabilityAvailability.PlatformUnsupported, timestamp, reason),
                    MachineHardwareCatalog.GpuDedicatedAvailableBudget.Unavailable(
                        CapabilityAvailability.PlatformUnsupported, timestamp, reason),
                ];
            }

            nint factory = 0;
            try
            {
                Guid dxgiFactory2 = new(0x50C83A1C, 0xE072, 0x4C48, 0x87, 0xB0, 0x36, 0x30, 0xFA, 0x36, 0xA6, 0xD0); // IID_IDXGIFactory2
                int created = CreateDXGIFactory1(ref dxgiFactory2, out factory);
                if (created != 0 || factory == 0)
                {
                    return GpuUnavailable(timestamp, $"DXGI 工厂创建失败 (hr=0x{created:X8})");
                }

                nint adapter = 0;
                nint adapter3 = 0;
                try
                {
                    // IDXGIFactory::EnumAdapters is vtable slot 7 (IUnknown 0..2, IDXGIObject 3..5, Parent 6).
                    int enumerated = CallComSlot(factory, 7, 0u, out adapter);
                    if (enumerated != 0 || adapter == 0)
                    {
                        return GpuUnavailable(timestamp, $"没有可枚举的显卡适配器 (hr=0x{enumerated:X8})");
                    }

                    Guid iid = new(0x645967A4, 0x1392, 0x4310, 0xA7, 0x98, 0x80, 0x53, 0xCE, 0x3E, 0x93, 0xFD); // IID_IDXGIAdapter3 (dxgi1_4.h)
                    int queried = QI(adapter, ref iid, out adapter3);
                    if (queried != 0 || adapter3 == 0)
                    {
                        return GpuUnavailable(timestamp, $"适配器 QI 失败 adapter=0x{adapter:X} (hr=0x{queried:X8})");
                    }

                    DXGI_QUERY_VIDEO_MEMORY_INFO info = default;
                    int hr = QueryVideoMemoryInfo(adapter3, 0u, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, ref info);
                    if (hr != 0 || info.Budget == 0)
                    {
                        return GpuUnavailable(timestamp, $"显存预算不可读 (hr=0x{hr:X8})");
                    }

                    long budget = checked((long)info.Budget);
                    long used = checked((long)info.CurrentUsage);
                    return
                    [
                        MachineHardwareCatalog.GpuDedicatedBudget.Observe(budget, timestamp, source),
                        MachineHardwareCatalog.GpuDedicatedCurrentUsage.Observe(used, timestamp, source),
                        MachineHardwareCatalog.GpuDedicatedAvailableBudget.Observe(Math.Max(0, budget - used), timestamp, source),
                    ];
                }
                finally
                {
                    if (adapter3 != 0)
                    {
                        Marshal.Release(adapter3);
                    }

                    if (adapter != 0)
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                if (factory != 0)
                {
                    Marshal.Release(factory);
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return GpuUnavailable(timestamp, "显存查询异常：" + exception.GetType().Name);
        }
    }

    internal static List<ICapability> GpuUnavailable(DateTimeOffset timestamp, string reason) =>
    [
        MachineHardwareCatalog.GpuDedicatedBudget.Unavailable(CapabilityAvailability.Unknown, timestamp, reason),
        MachineHardwareCatalog.GpuDedicatedCurrentUsage.Unavailable(CapabilityAvailability.Unknown, timestamp, reason),
        MachineHardwareCatalog.GpuDedicatedAvailableBudget.Unavailable(CapabilityAvailability.Unknown, timestamp, reason),
    ];

    internal static List<ICapability> CollectThermalWindows(DateTimeOffset timestamp)
    {
        // Windows has NO driverless CPU-temperature API: MSAcpi_ThermalZoneTemperature needs
        // admin and often does not exist, and CallNtPowerInformation carries no temperature
        // at all (treating its policy bytes as kelvin produced -273°C). The honest fact is
        // Unknown until a real channel (kernel driver / WMI with elevation) is wired.
        return
        [
            MachineHardwareCatalog.ThermalCpuTemperature.Unavailable(
                CapabilityAvailability.Unknown, timestamp, "Windows 未提供免驱动的 CPU 温度通道"),
        ];
    }

    internal static List<ICapability> CollectPowerWindows(DateTimeOffset timestamp)
    {
        const string schemeSource = "Windows PowerGetActiveScheme";
        SYSTEM_POWER_STATUS status = default;
        bool readable = GetSystemPowerStatus(ref status);
        List<ICapability> facts = ProjectPowerWindowsStatus(timestamp, readable, status.ACLineStatus, status.BatteryFlag, status.BatteryPercent);

        string profile = ReadActivePowerScheme();
        facts.Add(profile.Length > 0
            ? MachineEnvironmentCatalog.PowerProfileCurrent.Observe(profile, timestamp, schemeSource)
            : MachineEnvironmentCatalog.PowerProfileCurrent.Unavailable(
                CapabilityAvailability.Unknown, timestamp, "电源模式不可读"));
        return facts;
    }

    internal static List<ICapability> ProjectPowerWindowsStatus(DateTimeOffset timestamp, bool readable,
        byte acLineStatus, byte batteryFlag, byte batteryPercent)
    {
        const string statusSource = "Windows GetSystemPowerStatus";
        List<ICapability> facts = [];
        if (readable)
        {
            facts.Add(acLineStatus is 0 or 1
                ? MachineEnvironmentCatalog.PowerSource.Observe(acLineStatus == 1 ? "外接电源" : "电池", timestamp, statusSource)
                : MachineEnvironmentCatalog.PowerSource.Unavailable(CapabilityAvailability.Unknown, timestamp, "外接电源状态不可读"));
            if (batteryFlag == 255)
            {
                facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Unavailable(CapabilityAvailability.Unknown, timestamp, "系统无法确认电池状态"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.Unknown, timestamp, "系统无法确认电池状态"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.Unknown, timestamp, "系统无法确认电池状态"));
            }
            else if ((batteryFlag & 128) != 0)
            {
                facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Observe(false, timestamp, statusSource));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.DependencyMissing, timestamp, "无电池"));
            }
            else
            {
                facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Observe(true, timestamp, statusSource));
                facts.Add(batteryPercent is >= 0 and <= 100
                    ? MachineEnvironmentCatalog.PowerBatteryLevelPercent.Observe(batteryPercent, timestamp, statusSource)
                    : MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电量不可读"));
                facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Observe(
                    (batteryFlag & 8) != 0, timestamp, statusSource));
            }
        }
        else
        {
            facts.Add(MachineEnvironmentCatalog.PowerSource.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源状态不可读"));
            facts.Add(MachineEnvironmentCatalog.PowerBatteryPresent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源状态不可读"));
            facts.Add(MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源状态不可读"));
            facts.Add(MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源状态不可读"));
        }

        return facts;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryPercent;
        public byte Reserved;
        public uint BatteryLifetime;
        public uint BatteryFullLifetime;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);

    internal static string ReadActivePowerScheme()
    {
        if (PowerGetActiveScheme(nint.Zero, out nint scheme) != 0 || scheme == nint.Zero)
        {
            return string.Empty;
        }

        try
        {
            byte[] bytes = new byte[16];
            Marshal.Copy(scheme, bytes, 0, 16);
            Guid active = new(bytes);
            if (active == SchemeBalanced)
            {
                return "平衡";
            }

            if (active == SchemeHighPerformance)
            {
                return "高性能";
            }

            if (active == SchemePowerSaver)
            {
                return "节能";
            }

            return active.ToString();
        }
        finally
        {
            _ = LocalFree(scheme);
        }
    }

    private static readonly Guid SchemeBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid SchemeHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid SchemePowerSaver = new("a1841308-3541-4fab-bc1f-f6006f501f54");

    internal static List<ICapability> CollectThermalUnix(DateTimeOffset timestamp)
    {
        const string thermalSource = "Linux hwmon";
        List<ICapability> facts = [];
        double? cpuTemperature = ReadLinuxCpuTemperature();
        facts.Add(cpuTemperature is { } temperature
            ? MachineHardwareCatalog.ThermalCpuTemperature.Observe(temperature, timestamp, thermalSource)
            : MachineHardwareCatalog.ThermalCpuTemperature.Unavailable(
                CapabilityAvailability.Unknown, timestamp, "系统未暴露 CPU 温度"));
        return facts;
    }

    internal static List<ICapability> CollectPowerUnix(DateTimeOffset timestamp) => LinuxPowerProbe.Collect(timestamp);

    internal static double? ReadLinuxCpuTemperature()
    {
        // hwmon exposes one or more thermal sensors; prefer ones named like the package/core.
        string hwmonRoot = "/sys/class/hwmon";
        if (!Directory.Exists(hwmonRoot))
        {
            return null;
        }

        foreach (string device in Directory.EnumerateDirectories(hwmonRoot))
        {
            string nameFile = Path.Combine(device, "name");
            string name = File.Exists(nameFile) ? File.ReadAllText(nameFile).Trim() : string.Empty;
            foreach (string input in Directory.EnumerateFiles(device, "temp*_input"))
            {
                if (!double.TryParse(File.ReadAllText(input).Trim(), out double milli) || milli is <= 0 or >= 150_000)
                {
                    continue;
                }

                string labelFile = Path.Combine(Path.GetDirectoryName(input)!, Path.GetFileName(input).Replace("_input", "_label"));
                string label = File.Exists(labelFile) ? File.ReadAllText(labelFile).Trim() : string.Empty;
                if (name.Contains("core", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("k10temp", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("cpu", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("Package", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                    || label.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                {
                    return milli / 1000.0;
                }
            }
        }

        return null;
    }

    [DllImport("dxgi.dll", PreserveSig = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    // IUnknown.QueryInterface through the vtable slot 0 — DXGI has no flat export for it.
    private static int QI(nint unknown, ref Guid iid, out nint outPtr)
    {
        outPtr = 0;
        nint vtbl = Marshal.ReadIntPtr(unknown);
        nint qiSlot = Marshal.ReadIntPtr(vtbl);
        return Marshal.GetDelegateForFunctionPointer<QiDelegate>(qiSlot)(unknown, ref iid, out outPtr);
    }

    private delegate int QiDelegate(nint self, ref Guid iid, out nint outPtr);

    // Generic one-argument vtable call (used for EnumAdapters).
    private static int CallComSlot(nint self, int slot, uint argument, out nint outPtr)
    {
        outPtr = 0;
        nint vtbl = Marshal.ReadIntPtr(self);
        nint slotPtr = Marshal.ReadIntPtr(vtbl, slot * nint.Size);
        return Marshal.GetDelegateForFunctionPointer<EnumAdaptersDelegate>(slotPtr)(self, argument, out outPtr);
    }

    private delegate int EnumAdaptersDelegate(nint self, uint index, out nint adapter);

    // IDXGIAdapter3 vtable (dxgi1_4.idl): IUnknown 0..2, IDXGIObject 3..6, IDXGIAdapter
    // 7..9, Adapter1 10, Adapter2 11; Adapter3's own slots are Register/UnregisterHardware
    // ContentProtection (12, 13) then QueryVideoMemoryInfo (14).
    private static int QueryVideoMemoryInfo(nint adapter3, uint generation, uint segment, ref DXGI_QUERY_VIDEO_MEMORY_INFO info)
    {
        nint vtbl = Marshal.ReadIntPtr(adapter3);
        nint slotPtr = Marshal.ReadIntPtr(vtbl, 14 * nint.Size);
        return Marshal.GetDelegateForFunctionPointer<QueryMemoryDelegate>(slotPtr)(adapter3, generation, segment, ref info);
    }

    private delegate int QueryMemoryDelegate(nint self, uint generation, uint segment, ref DXGI_QUERY_VIDEO_MEMORY_INFO info);


    private const uint DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_QUERY_VIDEO_MEMORY_INFO
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [DllImport("powrprof.dll", SetLastError = false)]
    private static extern uint PowerGetActiveScheme(nint userPowerKey, out nint activeScheme);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern nint LocalFree(nint handle);
}
