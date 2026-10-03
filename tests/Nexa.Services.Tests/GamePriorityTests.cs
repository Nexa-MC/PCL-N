using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void GamePriorityCapturesScopedPreferenceAndLegacyMeaning()
    {
        var port = new InMemorySettingsPort(); var (store, policy) = PolicyFixture(port);
        string root = CreateTempDirectory(), instance = Path.Combine(root, "versions", "priority-fixture");
        try
        {
            Directory.CreateDirectory(instance); File.WriteAllBytes(Path.Combine(instance, "priority-fixture.jar"), [1]);
            var original = new MinecraftLaunchRequest
            {
                VersionJson = new JsonObject { ["id"] = "priority-fixture", ["mainClass"] = "fixture.Main", ["minecraftArguments"] = "--username ${auth_player_name}" },
                VersionId = "priority-fixture",
                InstanceDirectory = instance,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
            };
            AssertTrue(original.ProcessPriority is null);
            foreach (var (value, legacy, priority) in new[]
            {
                ("normal", "1", ProcessPriorityClass.Normal), ("below-normal", "2", ProcessPriorityClass.BelowNormal),
                ("above-normal", "0", ProcessPriorityClass.AboveNormal), ("high", "3", ProcessPriorityClass.High), ("real-time", "4", ProcessPriorityClass.RealTime)
            })
            {
                AssertTrue(policy.Set(new("game.process-priority", SettingsLayer.Global, new(SettingsOverrideMode.Custom, value))).IsSuccess);
                AssertEqual(legacy, store.GetValue<int>("LaunchArgumentPriority").Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var (_, reopened) = PolicyFixture(port); AssertEqual(value, Effective(reopened, "game.process-priority").Value.Value);
                var request = MinecraftLaunchCoordinator.ApplySettings(original, policy.Read(new(instance)).Value!);
                AssertEqual(priority, request.ProcessPriority!.Value);
                var plan = MinecraftLaunchPlanner.CreatePlan(request);
                AssertEqual(priority, plan.ProcessPriority!.Value);
                AssertTrue(policy.Set(new("game.process-priority", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "normal"), instance)).IsSuccess);
                AssertEqual(ProcessPriorityClass.Normal, MinecraftLaunchCoordinator.ApplySettings(original, policy.Read(new(instance)).Value!).ProcessPriority!.Value);
                AssertEqual(priority, plan.ProcessPriority!.Value); // Previously prepared launch remains captured.
                AssertTrue(policy.Set(new("game.process-priority", SettingsLayer.Instance, new(SettingsOverrideMode.Inherit), instance)).IsSuccess);
                AssertEqual(priority, MinecraftLaunchCoordinator.ApplySettings(original, policy.Read(new(instance)).Value!).ProcessPriority!.Value);
                port.Save(new Dictionary<string, string> { ["LaunchArgumentPriority"] = legacy });
                var (_, imported) = PolicyFixture(port); AssertEqual(value, Effective(imported, "game.process-priority").Value.Value);
            }
            AssertFalse(policy.Set(new("game.process-priority", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "idle"))).IsSuccess);
            AssertTrue(policy.Set(new("game.process-priority", SettingsLayer.Global, new(SettingsOverrideMode.Inherit))).IsSuccess);
            AssertEqual(1, store.GetValue<int>("LaunchArgumentPriority").Value);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask GamePriorityControlFailureCannotFailStartedSession()
    {
        foreach (int outcome in new[] { 0, 1, 2 })
        {
            string root = CreateTempDirectory();
            try
            {
                await using var processes = new MinecraftProcessService(new LongLivedProcessPort());
                var host = new PriorityFixtureHost(processes, outcome);
                var executor = new MinecraftLaunchExecutor(host);
                var plan = new MinecraftLaunchPlan("java", root, ["fixture.Main"], [], [],
                    new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []))
                { MainClassIndex = 0, ProcessPriority = ProcessPriorityClass.BelowNormal };
                var session = await executor.ExecuteAsync(plan, "priority-fixture");
                AssertEqual(1, host.PriorityCalls); AssertEqual(ProcessPriorityClass.BelowNormal, host.RequestedPriority);
                AssertFalse(session.Process.HasExited);
                if (outcome == 0 && OperatingSystem.IsWindows())
                    AssertEqual(ProcessPriorityClass.BelowNormal, session.Process.PriorityClass);
                var second = await executor.ExecuteAsync(plan with { ProcessPriority = null }, "priority-unspecified");
                AssertEqual(1, host.PriorityCalls); AssertFalse(second.Process.HasExited);
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private sealed class PriorityFixtureHost(MinecraftProcessService processes, int outcome) : IJvmHost
    {
        public int PriorityCalls { get; private set; }
        public ProcessPriorityClass RequestedPriority { get; private set; }
        public ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId, CancellationToken cancellationToken = default)
            => processes.StartAsync(plan, instanceId, cancellationToken);
        public JvmHostEnvironment Describe(MinecraftLaunchPlan plan) => throw new NotSupportedException();
        public JvmHostControlResult Suspend(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult ResumeProcess(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask) => throw new NotSupportedException();
        public JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority)
        {
            PriorityCalls++; RequestedPriority = priority;
            if (outcome == 2) throw new InvalidOperationException("Fixture scheduling failure.");
            if (outcome == 1) return new(false, "priority_unavailable", "Fixture denied.");
            var applied = new Nexa.Platform.PlatformProcessControl().SetPriority(session.Process, priority);
            AssertTrue(applied.Succeeded);
            return new(applied.Succeeded, applied.Code, applied.Message);
        }
    }
}
