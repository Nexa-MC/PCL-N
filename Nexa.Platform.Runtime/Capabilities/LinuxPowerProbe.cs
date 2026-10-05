using System.Globalization;

namespace Nexa.Services.Capabilities;

internal static class LinuxPowerProbe
{
    internal static List<ICapability> Collect(DateTimeOffset timestamp, string root = "/sys/class/power_supply",
        string governorPath = "/sys/devices/system/cpu/cpufreq/policy0/scaling_governor")
    {
        const string source = "Linux sysfs power_supply";
        List<(string Path, bool? Present, string? Status)> batteries = [];
        List<bool?> external = [];
        bool complete = Directory.Exists(root);
        try
        {
            if (complete)
            {
                string[] devices = Directory.EnumerateDirectories(root).Take(65).ToArray();
                if (devices.Length > 64) complete = false;
                foreach (string path in devices.Take(64))
                {
                    string? type = Read(Path.Combine(path, "type"));
                    if (type is null) { complete = false; continue; }
                    if (Read(Path.Combine(path, "scope")) == "Device") continue;
                    if (type == "Battery")
                        batteries.Add((path, ReadLong(Path.Combine(path, "present")) switch { 0 => false, 1 => true, _ => null }, Read(Path.Combine(path, "status"))));
                    else if (type is "Mains" or "Wireless" || type.StartsWith("USB", StringComparison.Ordinal))
                        external.Add(ReadLong(Path.Combine(path, "online")) switch { 0 => false, 1 => true, _ => null });
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            complete = false;
        }
        bool? present = batteries.Any(item => item.Present == true) ? true
            : complete && batteries.All(item => item.Present == false) ? false : null;
        var active = batteries.Where(item => item.Present == true).ToArray();
        bool batteryComplete = complete && batteries.All(item => item.Present.HasValue);
        bool? charging = active.Any(item => item.Status == "Charging") ? true
            : batteryComplete && active.Length > 0 && active.All(item => item.Status is "Discharging" or "Not charging" or "Full") ? false : null;
        string? power = external.Any(item => item == true) ? "外接电源"
            : complete && active.Any(item => item.Status == "Discharging") && external.All(item => item == false) ? "电池" : null;
        int? level = null;
        if (batteryComplete && active.Length > 0)
        {
            var capacities = active.Select(item => (Current: ReadLong(Path.Combine(item.Path, "energy_now")),
                Maximum: ReadLong(Path.Combine(item.Path, "energy_full")))).ToArray();
            if (capacities.All(item => item.Current is >= 0 && item.Maximum is > 0 && item.Current <= item.Maximum))
                level = (int)(100m * capacities.Sum(item => (decimal)item.Current!.Value) / capacities.Sum(item => (decimal)item.Maximum!.Value));
            else if (active.Length == 1 && ReadLong(Path.Combine(active[0].Path, "capacity")) is { } capacity && capacity is >= 0 and <= 100)
                level = (int)capacity;
        }
        string? governor = Read(governorPath);
        CapabilityAvailability batteryAvailability = present == false ? CapabilityAvailability.DependencyMissing : CapabilityAvailability.Unknown;
        string batteryReason = present == false ? "无电池" : "电池状态不可读";
        return
        [
            power is not null ? MachineEnvironmentCatalog.PowerSource.Observe(power, timestamp, source)
                : MachineEnvironmentCatalog.PowerSource.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源来源不可读"),
            present.HasValue ? MachineEnvironmentCatalog.PowerBatteryPresent.Observe(present.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryPresent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电池枚举不完整"),
            level.HasValue ? MachineEnvironmentCatalog.PowerBatteryLevelPercent.Observe(level.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryLevelPercent.Unavailable(batteryAvailability, timestamp, batteryReason),
            charging.HasValue ? MachineEnvironmentCatalog.PowerBatteryCharging.Observe(charging.Value, timestamp, source)
                : MachineEnvironmentCatalog.PowerBatteryCharging.Unavailable(batteryAvailability, timestamp, batteryReason),
            !string.IsNullOrEmpty(governor) ? MachineEnvironmentCatalog.PowerProfileCurrent.Observe(
                governor switch { "performance" => "高性能", "powersave" => "节能", _ => governor }, timestamp, "Linux cpufreq governor")
                : MachineEnvironmentCatalog.PowerProfileCurrent.Unavailable(CapabilityAvailability.Unknown, timestamp, "电源模式不可读"),
        ];
    }

    private static long? ReadLong(string path) => long.TryParse(Read(path), NumberStyles.None, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static string? Read(string path)
    {
        try
        {
            using StreamReader reader = new(path);
            char[] buffer = new char[1025];
            int count = reader.ReadBlock(buffer, 0, buffer.Length);
            return count <= 1024 ? new string(buffer, 0, count).Trim() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
