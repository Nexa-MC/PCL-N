using System.Runtime.InteropServices;

namespace Nexa.Services.Capabilities;

internal sealed record BatteryObservation(bool? Present, long? CurrentCapacity, long? MaximumCapacity, bool? Charging);

internal static partial class MacPowerProbe
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    internal static List<ICapability> Collect(DateTimeOffset timestamp)
    {
        nint info = 0, sources = 0;
        try
        {
            info = IOPSCopyPowerSourcesInfo();
            if (info == 0) return Unavailable(timestamp, "无法读取 macOS 电源信息");
            sources = IOPSCopyPowerSourcesList(info);
            if (sources == 0) return Unavailable(timestamp, "无法枚举 macOS 电源");
            nint count = CFArrayGetCount(sources);
            if (count < 0 || count > 64) return Unavailable(timestamp, "macOS 电源数量超出检测上限");
            List<BatteryObservation> batteries = [];
            bool complete = true;
            for (nint index = 0; index < count; index++)
            {
                nint description = IOPSGetPowerSourceDescription(info, CFArrayGetValueAtIndex(sources, index));
                if (description == 0) { complete = false; continue; }
                string? type = ReadString(Read(description, "Type"));
                if (type is null) { complete = false; continue; }
                if (type != "InternalBattery") continue;
                batteries.Add(new(ReadBoolean(Read(description, "Is Present")),
                    ReadNumber(Read(description, "Current Capacity")), ReadNumber(Read(description, "Max Capacity")),
                    ReadBoolean(Read(description, "Is Charging"))));
            }
            return Project(timestamp, ReadString(IOPSGetProvidingPowerSourceType(info)), batteries, complete, ReadLowPowerMode());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not AccessViolationException)
        {
            return Unavailable(timestamp, "macOS 电源查询失败：" + exception.GetType().Name);
        }
        finally
        {
            if (sources != 0) CFRelease(sources);
            if (info != 0) CFRelease(info);
        }
    }

    internal static List<ICapability> Project(DateTimeOffset timestamp, string? powerSource,
        IReadOnlyList<BatteryObservation> batteries, bool complete, bool? lowPowerMode)
    {
        const string source = "macOS IOKit IOPowerSources";
        bool? present = batteries.Any(item => item.Present == true) ? true
            : complete && batteries.All(item => item.Present == false) ? false : null;
        BatteryObservation[] active = batteries.Where(item => item.Present == true).ToArray();
        bool capacitiesKnown = complete && batteries.All(item => item.Present.HasValue)
            && active.Length == 1 && active.All(item => item.CurrentCapacity is >= 0
                && item.MaximumCapacity is > 0 && item.CurrentCapacity <= item.MaximumCapacity);
        // IOPS capacity units are provider-specific. Independent batteries' capacity
        // numbers cannot be summed as if they were a common energy measurement.
        int? level = capacitiesKnown ? (int)(100m * active[0].CurrentCapacity!.Value / active[0].MaximumCapacity!.Value) : null;
        bool? charging = active.Any(item => item.Charging == true) ? true
            : complete && batteries.All(item => item.Present.HasValue) && active.Length > 0
                && active.All(item => item.Charging.HasValue) ? false : null;
        CapabilityAvailability batteryAvailability = present == false ? CapabilityAvailability.DependencyMissing : CapabilityAvailability.Unknown;
        string batteryReason = present == false ? "无内部电池" : "内部电池状态不可读";
        return
        [
            powerSource is "AC Power" or "Battery Power" or "UPS Power"
                ? MachineEnvironmentCatalog.PowerSource.Observe(powerSource switch { "AC Power" => "外接电源", "Battery Power" => "电池", _ => "UPS" }, timestamp, source)
                : MachineEnvironmentCatalog.PowerSource.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源来源不可读"),
            present.HasValue ? MachineEnvironmentCatalog.PowerBatteryPresent.Observe(present.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryPresent.Unavailable(CapabilityAvailability.Unknown, timestamp, "内部电池枚举不完整"),
            level.HasValue ? MachineEnvironmentCatalog.PowerBatteryLevelPercent.Observe(level.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(batteryAvailability, timestamp,
                    active.Length > 1 ? "多个内部电池的容量单位不可比较" : batteryReason),
            charging.HasValue ? MachineEnvironmentCatalog.PowerBatteryCharging.Observe(charging.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(batteryAvailability, timestamp, batteryReason),
            lowPowerMode.HasValue ? MachineEnvironmentCatalog.PowerProfileCurrent.Observe(
                lowPowerMode.Value ? "节能" : "标准（未启用低电量模式）", timestamp, "macOS NSProcessInfo lowPowerModeEnabled")
                : MachineEnvironmentCatalog.PowerProfileCurrent.Unavailable(CapabilityAvailability.Unknown, timestamp, "系统未提供低电量模式状态"),
        ];
    }

    private static List<ICapability> Unavailable(DateTimeOffset timestamp, string reason) =>
        MachineCapabilityCatalog.CreateRegistry().Definitions.Where(item => item.Provider == MachineEnvironmentCatalog.PowerProviderId)
            .Select(item => item.Unavailable(CapabilityAvailability.TemporarilyUnavailable, timestamp, reason)).ToList();

    private static nint Read(nint dictionary, string name)
    {
        nint key = CFStringCreateWithCString(0, name, 0x08000100);
        if (key == 0) return 0;
        try { return CFDictionaryGetValue(dictionary, key); }
        finally { CFRelease(key); }
    }

    private static unsafe string? ReadString(nint value)
    {
        if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return null;
        byte* buffer = stackalloc byte[128];
        return CFStringGetCString(value, buffer, 128, 0x08000100) != 0
            ? Marshal.PtrToStringUTF8((nint)buffer) : null;
    }

    private static bool? ReadBoolean(nint value) => value != 0 && CFGetTypeID(value) == CFBooleanGetTypeID()
        ? CFBooleanGetValue(value) != 0 : null;

    private static long? ReadNumber(nint value) => value != 0 && CFGetTypeID(value) == CFNumberGetTypeID()
        && CFNumberGetValue(value, 4, out long number) != 0 ? number : null; // kCFNumberSInt64Type

    private static bool? ReadLowPowerMode()
    {
        // Loading Foundation registers NSProcessInfo before objc_getClass. The handle is
        // balanced; framework objects returned by processInfo are borrowed.
        nint framework = 0;
        try
        {
            framework = NativeLibrary.Load(Foundation);
            nint type = objc_getClass("NSProcessInfo");
            if (type == 0) return null;
            nint processInfo = SendObject(type, sel_registerName("processInfo"));
            nint selector = sel_registerName("isLowPowerModeEnabled");
            if (processInfo == 0 || SendResponds(processInfo, sel_registerName("respondsToSelector:"), selector) == 0) return null;
            return SendBoolean(processInfo, selector) != 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { if (framework != 0) NativeLibrary.Free(framework); }
    }

    [LibraryImport(IOKit)] private static partial nint IOPSCopyPowerSourcesInfo();
    [LibraryImport(IOKit)] private static partial nint IOPSCopyPowerSourcesList(nint info);
    [LibraryImport(IOKit)] private static partial nint IOPSGetPowerSourceDescription(nint info, nint source);
    [LibraryImport(IOKit)] private static partial nint IOPSGetProvidingPowerSourceType(nint info);
    [LibraryImport(CoreFoundation)] private static partial nint CFArrayGetCount(nint array);
    [LibraryImport(CoreFoundation)] private static partial nint CFArrayGetValueAtIndex(nint array, nint index);
    [LibraryImport(CoreFoundation)] private static partial nint CFDictionaryGetValue(nint dictionary, nint key);
    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)] private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
    [LibraryImport(CoreFoundation)] private static unsafe partial byte CFStringGetCString(nint value, byte* buffer, nint size, uint encoding);
    [LibraryImport(CoreFoundation)] private static partial nuint CFGetTypeID(nint value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFStringGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial nuint CFBooleanGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial byte CFBooleanGetValue(nint value);
    [LibraryImport(CoreFoundation)] private static partial nuint CFNumberGetTypeID();
    [LibraryImport(CoreFoundation)] private static partial byte CFNumberGetValue(nint value, nint type, out long number);
    [LibraryImport(CoreFoundation)] private static partial void CFRelease(nint value);
    [LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint objc_getClass(string name);
    [LibraryImport(ObjectiveC, StringMarshalling = StringMarshalling.Utf8)] private static partial nint sel_registerName(string name);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial nint SendObject(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial byte SendBoolean(nint receiver, nint selector);
    [LibraryImport(ObjectiveC, EntryPoint = "objc_msgSend")] private static partial byte SendResponds(nint receiver, nint selector, nint argument);
}
