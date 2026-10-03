using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JavaVendorPreferenceStaysWithinCompatibility()
    {
        JavaRuntimeCandidate[] candidates =
        [
            Candidate("vendor-temurin", new Version(17, 0, 10), JavaBrand.EclipseTemurin, false),
            Candidate("vendor-microsoft", new Version(17, 0, 10), JavaBrand.Microsoft, false),
            Candidate("vendor-oracle-disabled", new Version(17, 0, 10), JavaBrand.Oracle, false) with { IsEnabled = false },
            Candidate("vendor-oracle-newer", new Version(25, 0), JavaBrand.Oracle, false),
        ];
        var selector = new JavaSelectionService(new InMemoryJavaLocator(candidates));
        var requirement = JavaRequirementResolution.Valid(new(new Version(17, 0), new Version(17, 999)));
        var port = new InMemorySettingsPort(); var (_, policy) = PolicyFixture(port);
        string instance = Path.GetFullPath("vendor-instance");
        JavaPreference Read() => MinecraftLaunchCoordinator.ApplyJavaPreference(new AutoSelectJavaPreference(), policy.Read(new(instance)).Value!);
        AssertTrue(policy.Set(new("java.vendor", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "Microsoft"))).IsSuccess);
        AssertEqual(JavaBrand.Microsoft, (await selector.SelectAsync(requirement, Read())).SelectedJava!.Installation.Brand);
        AssertTrue(policy.Set(new("java.vendor", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "Oracle"), instance)).IsSuccess);
        AssertEqual(JavaBrand.EclipseTemurin, (await selector.SelectAsync(requirement, Read())).SelectedJava!.Installation.Brand);
        AssertTrue(policy.Set(new("java.vendor", SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), instance)).IsSuccess);
        AssertEqual(JavaBrand.Microsoft, (await selector.SelectAsync(requirement, Read())).SelectedJava!.Installation.Brand);
        var (_, restarted) = PolicyFixture(port);
        AssertEqual("Microsoft", restarted.Read(new(instance)).Value!.Values.Single(item => item.Key == "java.vendor").Value.Value);
        AssertFalse(policy.Set(new("java.vendor", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "NotAJavaVendor"))).IsSuccess);
        AssertTrue(policy.Set(new("java.runtime", SettingsLayer.Instance,
            new(SettingsOverrideMode.Custom, candidates[0].Installation.JavaExecutablePath), instance)).IsSuccess);
        AssertTrue(Read() is ExistingJavaPreference);
        AssertEqual(JavaBrand.EclipseTemurin, (await selector.SelectAsync(requirement, Read())).SelectedJava!.Installation.Brand);
        AssertTrue(policy.Set(new("java.runtime", SettingsLayer.Instance, new(SettingsOverrideMode.Auto), instance)).IsSuccess);
        AssertTrue(policy.Set(new("java.vendor", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, ""), instance)).IsSuccess);
        AssertEqual(JavaBrand.EclipseTemurin, (await selector.SelectAsync(requirement, Read())).SelectedJava!.Installation.Brand);
    }
}
