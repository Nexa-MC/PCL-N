using Nexa.Services.Capabilities;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LinuxDisplayProjectsActiveOutputsAndExplicitPrimary()
    {
        const string text = """
            Screen 0: minimum 8 x 8, current 4480 x 1440, maximum 32767 x 32767
            eDP-1 connected primary 1920x1080+0+0 (normal left inverted right x axis y axis)
               1920x1080     60.01*+  59.93
            HDMI-1 connected 2560x1440+1920+0 (normal left inverted right x axis y axis)
               2560x1440     144.00*+  120.00  60.00
            DP-1 connected (normal left inverted right x axis y axis)
               1920x1080     60.00+
            DP-2 disconnected (normal left inverted right x axis y axis)
            """;
        string root = Path.Combine(Path.GetTempPath(), "nexa-drm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "card0-eDP-1"));
        File.WriteAllText(Path.Combine(root, "card0-eDP-1", "status"), "connected\n");
        try
        {
            var outputs = LinuxDisplayProbe.Parse(text);
            AssertEqual(2, outputs.Count);
            var facts = LinuxDisplayProbe.Project(outputs, DateTimeOffset.UtcNow, root);
            AssertEqual(2, ((Capability<int>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayCount.Id)).Value);
            AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryInternal.Id)).Value);
            AssertEqual("1920×1080", ((Capability<string>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryResolution.Id)).Value);
            AssertEqual(60.01, ((Capability<double>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryRefreshHz.Id)).Value);
            facts = LinuxDisplayProbe.Project(outputs.Select(item => item with { Primary = false }).ToArray(), DateTimeOffset.UtcNow, root);
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryResolution.Id).Availability);
            facts = LinuxDisplayProbe.Project([outputs[1] with { Primary = false }], DateTimeOffset.UtcNow, root);
            AssertEqual("2560×1440", ((Capability<string>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryResolution.Id)).Value);
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayPrimaryInternal.Id).Availability);
            foreach (string invalid in new[] { new string('x', 65537), "Screen 0: current 1920 x 1080", text.Replace("60.01*+", "NaN*+", StringComparison.Ordinal), text.Replace("HDMI-1 connected", "HDMI-1 connected primary", StringComparison.Ordinal) })
            {
                bool rejected = false;
                try { LinuxDisplayProbe.Parse(invalid); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void MacPowerProjectionPreservesUnavailableAndBatteryUnits()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var facts = MacPowerProbe.Project(now, "AC Power", [new(true, 3500, 7000, true)], true, true);
        AssertEqual(5, facts.Count);
        AssertEqual("外接电源", ((Capability<string>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerSource.Id)).Value);
        AssertEqual(50, ((Capability<int>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id)).Value);
        AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryCharging.Id)).Value);
        facts = MacPowerProbe.Project(now, "AC Power", [], true, false);
        AssertEqual(false, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id)).Value);
        AssertEqual(CapabilityAvailability.DependencyMissing, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id).Availability);
        facts = MacPowerProbe.Project(now, null, [new(true, 50, 100, false), new(true, 3500, 7000, false)], true, null);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id).Availability);
        facts = MacPowerProbe.Project(now, "Battery Power", [new(true, 8000, 7000, null)], false, null);
        AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id)).Value);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id).Availability);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryCharging.Id).Availability);
        AssertFalse(facts.Any(item => item.Availability == CapabilityAvailability.NotImplemented));
    }

    private static void LinuxPowerEnumeratesSystemBatteriesAndWeightsEnergy()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-power-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            void Device(string name, params (string Name, string Value)[] fields)
            {
                string path = Path.Combine(root, name); Directory.CreateDirectory(path);
                foreach (var (field, value) in fields) File.WriteAllText(Path.Combine(path, field), value);
            }
            string governor = Path.Combine(root, "governor"); File.WriteAllText(governor, "powersave\n");
            Device("BAT1", ("type", "Battery"), ("present", "1"), ("status", "Charging"), ("energy_now", "10000"), ("energy_full", "20000"));
            Device("BAT2", ("type", "Battery"), ("present", "1"), ("status", "Full"), ("energy_now", "40000"), ("energy_full", "40000"));
            Device("Mouse", ("type", "Battery"), ("scope", "Device"), ("capacity", "1"));
            Device("AC", ("type", "USB_PD"), ("online", "1"));
            var facts = LinuxPowerProbe.Collect(DateTimeOffset.UtcNow, root, governor);
            AssertEqual(5, facts.Count);
            AssertEqual(83, ((Capability<int>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id)).Value);
            AssertEqual("外接电源", ((Capability<string>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerSource.Id)).Value);
            AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryCharging.Id)).Value);
            File.WriteAllText(Path.Combine(root, "BAT2", "energy_full"), new string('9', 1025));
            facts = LinuxPowerProbe.Collect(DateTimeOffset.UtcNow, root, governor);
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id).Availability);
            Directory.CreateDirectory(Path.Combine(root, "unreadable"));
            facts = LinuxPowerProbe.Collect(DateTimeOffset.UtcNow, root, governor);
            AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id)).Value);
            facts = LinuxPowerProbe.Collect(DateTimeOffset.UtcNow, Path.Combine(root, "missing"), governor);
            AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id).Availability);
            AssertFalse(facts.Any(item => item.Availability == CapabilityAvailability.NotImplemented));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void DedicatedGpuBudgetDoesNotUseStaticCapacityOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var facts = GpuProbes.CollectGpu(DateTimeOffset.UtcNow);
        AssertEqual(3, facts.Count);
        AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.PlatformUnsupported));
        AssertTrue(facts.All(item => !string.IsNullOrWhiteSpace(item.Reason)));
    }

    private static async ValueTask FormFactorFailedProbesPreserveUnknown()
    {
        bool Fail() => throw new IOException("fixture probe unavailable");
        FormFactorCapabilityProvider provider = new(Fail, Fail, Fail, Fail, Fail);
        var facts = await provider.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual(4, facts.Count);
        AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.Unknown));
        provider = new(() => true, Fail, () => false, () => true, () => false);
        facts = await provider.CollectAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == FormFactorCatalog.FormFactorType.Id).Availability);
        AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == FormFactorCatalog.FormFactorPortable.Id)).Value);
        AssertEqual(false, ((Capability<bool>)facts.Single(item => item.Id == FormFactorCatalog.FormFactorHandheld.Id)).Value);
        facts = FormFactorCapabilityProvider.Project(DateTimeOffset.UtcNow, true, true, true, null, true);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == FormFactorCatalog.FormFactorType.Id).Availability);
        AssertEqual(CapabilityAvailability.Unknown, facts.Single(item => item.Id == FormFactorCatalog.FormFactorHandheld.Id).Availability);
        facts = FormFactorCapabilityProvider.Project(DateTimeOffset.UtcNow, false, null, null, null, null);
        AssertEqual("Desktop", ((Capability<string>)facts.Single(item => item.Id == FormFactorCatalog.FormFactorType.Id)).Value);
        AssertEqual(false, ((Capability<bool>)facts.Single(item => item.Id == FormFactorCatalog.FormFactorHandheld.Id)).Value);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        bool observed = false;
        try { await provider.CollectAsync(DateTimeOffset.UtcNow, canceled.Token); }
        catch (OperationCanceledException) { observed = true; }
        AssertTrue(observed);
    }

    private static void WindowsPowerProjectionRecognizesUnknownSentinels()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var facts = GpuProbes.ProjectPowerWindowsStatus(now, true, 255, 255, 255);
        AssertEqual(4, facts.Count);
        AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.Unknown));
        facts = GpuProbes.ProjectPowerWindowsStatus(now, false, 1, 8, 50);
        AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.Unknown));
        facts = GpuProbes.ProjectPowerWindowsStatus(now, true, 1, 128, 255);
        AssertEqual(false, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id)).Value);
        AssertEqual(CapabilityAvailability.DependencyMissing, facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id).Availability);
        facts = GpuProbes.ProjectPowerWindowsStatus(now, true, 1, 8, 50);
        AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryPresent.Id)).Value);
        AssertEqual(true, ((Capability<bool>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryCharging.Id)).Value);
        AssertEqual(50, ((Capability<int>)facts.Single(item => item.Id == MachineEnvironmentCatalog.PowerBatteryLevelPercent.Id)).Value);
    }

    private static void WindowsDisplayEmbeddedConnectorsPreserveUnknown()
    {
        var layout = DisplayCapabilityProvider.DisplayConfigLayout();
        AssertEqual((20, 72, 64, 84, 420, 20), layout);
        foreach (int technology in new[] { unchecked((int)0x80000000), 6, 11, 13 })
            AssertEqual((bool?)true, DisplayCapabilityProvider.ProjectOutputTechnology(technology));
        foreach (int technology in new[] { 0, 4, 5, 10, 12, 15, 16, 17 })
            AssertEqual((bool?)false, DisplayCapabilityProvider.ProjectOutputTechnology(technology));
        AssertEqual((bool?)null, DisplayCapabilityProvider.ProjectOutputTechnology(-1));
        AssertEqual((bool?)null, DisplayCapabilityProvider.ProjectOutputTechnology(int.MaxValue));
    }

    private static async ValueTask LinuxDisplayQueryHonorsBoundsTimeoutAndCancellation()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "nexa-xrandr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string executable = Path.Combine(root, "xrandr-fixture");
            void Script(string body)
            {
                File.WriteAllText(executable, "#!/bin/sh\n" + body + "\n");
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            Script("printf 'eDP-1 connected primary 1920x1080+0+0\\n   1920x1080 60.00*+\\n'");
            var facts = await LinuxDisplayProbe.QueryAsync(DateTimeOffset.UtcNow, CancellationToken.None, executable, root);
            AssertEqual(1, ((Capability<int>)facts.Single(item => item.Id == MachineEnvironmentCatalog.DisplayCount.Id)).Value);
            Script("head -c 65537 /dev/zero | tr '\\000' x");
            facts = await LinuxDisplayProbe.QueryAsync(DateTimeOffset.UtcNow, CancellationToken.None, executable, root);
            AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.Unknown));
            Script("sleep 10");
            facts = await LinuxDisplayProbe.QueryAsync(DateTimeOffset.UtcNow, CancellationToken.None, executable, root);
            AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.TemporarilyUnavailable));
            using CancellationTokenSource canceled = new(TimeSpan.FromMilliseconds(50));
            bool observed = false;
            try { await LinuxDisplayProbe.QueryAsync(DateTimeOffset.UtcNow, canceled.Token, executable, root); }
            catch (OperationCanceledException) { observed = true; }
            AssertTrue(observed);
            facts = await LinuxDisplayProbe.QueryAsync(DateTimeOffset.UtcNow, CancellationToken.None, Path.Combine(root, "missing"), root);
            AssertTrue(facts.All(item => item.Availability == CapabilityAvailability.DependencyMissing));
        }
        finally { Directory.Delete(root, true); }
    }
}
