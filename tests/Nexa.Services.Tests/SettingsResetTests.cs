using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void SettingsResetIsScopedPreviewedAndPreservesReservedValues()
    {
        var port = new InMemorySettingsPort();
        // Older files may contain the retired preference. Preserve it on reset, while
        // refusing new writes that would imply compatibility checks can be disabled.
        port.Save(new Dictionary<string, string>
        {
            [SettingsPolicySchema.StorageKey] = "{\"version\":1,\"global\":{\"java.compatibility\":{\"mode\":\"Custom\",\"value\":\"false\"}},\"instances\":{}}"
        });
        var (store, policy) = PolicyFixture(port);
        string first = Path.GetFullPath("reset-instance-a"), second = Path.GetFullPath("reset-instance-b");
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1234"), first)).IsSuccess);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "1500"), second)).IsSuccess);
        AssertTrue(policy.Set(new("appearance.low-power", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "true"))).IsSuccess);
        AssertTrue(policy.Set(new("network.proxy-password", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "keep-private"))).IsSuccess);
        long beforeRejectedWrite = store.Revision;
        AssertFalse(policy.Set(new("java.compatibility", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "true"))).IsSuccess);
        AssertEqual(beforeRejectedWrite, store.Revision);
        AssertEqual("false", Effective(policy, "java.compatibility").Value.Value);
        AssertTrue(policy.Set(new("appearance.animation-fps", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "144"))).IsSuccess);
        long revision = store.Revision;
        var instance = policy.PreviewReset(new(first));
        AssertEqual(0, instance.Errors.Count); AssertEqual(1, instance.Changes.Count);
        AssertEqual(revision, store.Revision); AssertEqual("1234", Effective(policy, "game.width", first).Value.Value);
        AssertTrue(policy.ApplyReset(new(instance.Revision, first)).IsSuccess);
        AssertEqual("1024", Effective(policy, "game.width", first).Value.Value);
        AssertEqual(SettingsLayer.Global, Effective(policy, "game.width", first).Source);
        AssertEqual("1500", Effective(policy, "game.width", second).Value.Value);
        var global = policy.PreviewReset(new());
        AssertTrue(global.Changes.All(change => change.Layer == SettingsLayer.Global && change.InstanceId is null));
        AssertTrue(global.Changes.Any(change => change.Key == "network.proxy-password"));
        AssertFalse(global.Changes.Any(change => change.Key == "java.compatibility"));
        AssertTrue(global.Changes.Any(change => change.Key == "appearance.low-power"));
        AssertEqual("1024", Effective(policy, "game.width").Value.Value);
        AssertTrue(policy.ApplyReset(new(global.Revision)).IsSuccess);
        AssertEqual("854", Effective(policy, "game.width").Value.Value);
        AssertEqual(59, store.GetValue<int>("UiAniFPS").Value);
        AssertEqual("false", Effective(policy, "appearance.low-power").Value.Value);
        AssertEqual(false, store.GetValue<bool>("UiUltraLowPowerMode").Value);
        AssertEqual("", Effective(policy, "network.proxy-password").Value.Value);
        AssertEqual("false", Effective(policy, "java.compatibility").Value.Value);
        var (_, reopened) = PolicyFixture(port);
        AssertEqual("854", Effective(reopened, "game.width", first).Value.Value);
        AssertEqual("1500", Effective(reopened, "game.width", second).Value.Value);
        long after = store.Revision;
        AssertEqual(0, policy.PreviewReset(new()).Changes.Count);
        AssertTrue(policy.ApplyReset(new(after)).IsSuccess);
        AssertEqual(after, store.Revision);
    }

    private static void SettingsResetRejectsStaleRevisionAndRollsBackSaveFailure()
    {
        var port = new PolicyFailingPort(); var (store, policy) = PolicyFixture(port);
        AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1024"))).IsSuccess);
        var preview = policy.PreviewReset(new());
        AssertTrue(policy.Set(new("game.height", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "720"))).IsSuccess);
        AssertFalse(policy.ApplyReset(new(preview.Revision)).IsSuccess);
        AssertEqual("1024", Effective(policy, "game.width").Value.Value);
        preview = policy.PreviewReset(new());
        port.Fail = true;
        AssertFalse(policy.ApplyReset(new(preview.Revision)).IsSuccess);
        AssertEqual(preview.Revision, store.Revision);
        AssertEqual("1024", Effective(policy, "game.width").Value.Value);
        AssertEqual("720", Effective(policy, "game.height").Value.Value);
        var (_, reopened) = PolicyFixture(port);
        AssertEqual("1024", Effective(reopened, "game.width").Value.Value);
        AssertTrue(policy.PreviewReset(new("relative-instance")).Errors.Count > 0);
        AssertFalse(policy.ApplyReset(new(store.Revision, "relative-instance")).IsSuccess);
    }
}
