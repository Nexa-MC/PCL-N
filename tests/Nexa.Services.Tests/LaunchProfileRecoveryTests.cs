using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void RecoverySettingsRestoreEffectiveProfileValuesAndCompensateOnlyThatLayer()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("profile-recovery-instance");
        long Revision() => service.Read(new(instance)).Value!.Revision;
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "4096"), instance)).IsSuccess);
        AssertTrue(service.SaveLaunchProfile(new(instance, "playing", "Playing", new Dictionary<string, SettingsOverride>
        { ["game.memory"] = new(SettingsOverrideMode.Custom, "8192") }, new(), Revision())).IsSuccess);
        AssertTrue(service.SelectLaunchProfile(new(instance, "playing", Revision())).IsSuccess);
        string baseline = service.CaptureRecoverySettings(instance);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Profile, new(SettingsOverrideMode.Custom, "10240"), instance) { ProfileId = "playing" }).IsSuccess);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "6144"), instance)).IsSuccess);
        var plan = service.PlanRecoverySettings(instance, baseline, ["game.memory"]);
        AssertEqual(SettingsLayer.Profile, plan.After.Single().Layer); AssertEqual("playing", plan.After.Single().ProfileId);
        AssertEqual("10240", plan.Before.Single().Value.Value); AssertEqual("8192", plan.After.Single().Value.Value);
        var document = SettingsPolicyService.EncodeRecoverySettingsPlan(plan);
        AssertEqual(2, document["version"]!.GetValue<int>());
        var decoded = SettingsPolicyService.DecodeRecoverySettingsPlan(document);
        AssertTrue(service.ApplyRecoverySettingsPlan(decoded, reverse: false).IsSuccess);
        AssertEqual("8192", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "7168"), instance)).IsSuccess);
        AssertTrue(service.SelectLaunchProfile(new(instance, null, Revision())).IsSuccess);
        AssertEqual("7168", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.ApplyRecoverySettingsPlan(decoded, reverse: true).IsSuccess);
        AssertEqual("7168", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.SelectLaunchProfile(new(instance, "playing", Revision())).IsSuccess);
        AssertEqual("10240", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.ApplyRecoverySettingsPlan(decoded, reverse: true).IsSuccess);
    }

    private static void RecoverySettingsPreserveProfileInheritanceAndRejectEphemeralSnapshots()
    {
        var (_, service) = PolicyFixture();
        string instance = Path.GetFullPath("profile-inherited-recovery");
        long Revision() => service.Read(new(instance)).Value!.Revision;
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "4096"), instance)).IsSuccess);
        AssertTrue(service.SaveLaunchProfile(new(instance, "empty", "Inherited", new Dictionary<string, SettingsOverride>(), new(), Revision())).IsSuccess);
        AssertTrue(service.SelectLaunchProfile(new(instance, "empty", Revision())).IsSuccess);
        string baseline = service.CaptureRecoverySettings(instance);
        AssertEqual(0, service.PreviewRecoverySettings(instance, baseline).Changes.Count);
        AssertTrue(service.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "6144"), instance)).IsSuccess);
        var plan = service.PlanRecoverySettings(instance, baseline, ["game.memory"]);
        AssertEqual(SettingsOverrideMode.Inherit, plan.Before.Single().Value.Mode);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: false).IsSuccess);
        AssertEqual("4096", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess);
        AssertEqual("6144", Effective(service, "game.memory", instance).Value.Value);
        AssertEqual(SettingsLayer.Instance, Effective(service, "game.memory", instance).Source);
        AssertTrue(service.BeginTemporaryLaunch(new(instance, "one-session", new Dictionary<string, SettingsOverride>
        { ["game.memory"] = new(SettingsOverrideMode.Custom, "8192") }, new(), Revision())).IsSuccess);
        bool blocked = false; try { service.CaptureRecoverySettings(instance); } catch (InvalidDataException) { blocked = true; }
        AssertTrue(blocked); AssertTrue(service.PreviewRecoverySettings(instance, baseline).Errors.Count > 0);
        AssertFalse(service.ApplyRecoverySettingsPlan(plan, reverse: true).IsSuccess);
        AssertEqual("8192", Effective(service, "game.memory", instance).Value.Value);
        AssertTrue(service.EndTemporaryLaunch(new(instance, Revision())).IsSuccess);
        AssertTrue(service.SelectLaunchProfile(new(instance, null, Revision())).IsSuccess);
        var instancePlan = service.PlanRecoverySettings(instance, baseline, ["game.memory"]);
        var legacy = SettingsPolicyService.EncodeRecoverySettingsPlan(instancePlan); legacy["version"] = 1;
        foreach (var entry in legacy["values"]!.AsObject())
        { entry.Value!.AsObject().Remove("layer"); entry.Value!.AsObject().Remove("profileId"); }
        var decoded = SettingsPolicyService.DecodeRecoverySettingsPlan(legacy);
        AssertEqual(SettingsLayer.Instance, decoded.After.Single().Layer); AssertEqual(null, decoded.After.Single().ProfileId);
        AssertTrue(service.ApplyRecoverySettingsPlan(decoded, reverse: false).IsSuccess);
        AssertEqual("4096", Effective(service, "game.memory", instance).Value.Value);
    }
}
