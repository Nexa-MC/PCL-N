using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Platform;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LaunchProfilesPersistResolveAndTemporaryLayersRevoke()
    {
        var port = new InMemorySettingsPort(); var (_, policy) = PolicyFixture(port);
        string instance = Path.GetFullPath("launch-profile-fixture");
        AssertTrue(policy.Set(new("game.memory", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "4096"))).IsSuccess);
        AssertTrue(policy.Set(new("game.memory", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "6144"), instance)).IsSuccess);
        long Revision() => policy.Read(new(instance)).Value!.Revision;
        var values = new Dictionary<string, SettingsOverride> { ["game.memory"] = new(SettingsOverrideMode.Custom, "8192") };
        AssertTrue(policy.SaveLaunchProfile(new(instance, "playing", "Playing", values, new(), Revision())).IsSuccess);
        AssertTrue(policy.SelectLaunchProfile(new(instance, "playing", Revision())).IsSuccess);
        AssertEqual("8192", Effective(policy, "game.memory", instance).Value.Value);
        AssertEqual(SettingsLayer.Profile, Effective(policy, "game.memory", instance).Source);
        AssertTrue(policy.Set(new("game.memory", SettingsLayer.Profile, new(SettingsOverrideMode.Custom, "10240"), instance) { ProfileId = "playing" }).IsSuccess);
        var captured = policy.Read(new(instance)).Value!;
        AssertTrue(policy.BeginTemporaryLaunch(new(instance, "session", values, new(SafeLaunch: true), Revision())).IsSuccess);
        AssertEqual("8192", Effective(policy, "game.memory", instance).Value.Value);
        AssertEqual(SettingsLayer.Temporary, Effective(policy, "game.memory", instance).Source);
        AssertTrue(policy.ReadLaunchOverlay(instance).SafeLaunch);
        AssertEqual("10240", captured.Values.Single(value => value.Key == "game.memory").Value.Value);
        var (_, reopenedWhileTemporary) = PolicyFixture(port);
        AssertEqual("10240", Effective(reopenedWhileTemporary, "game.memory", instance).Value.Value);
        AssertEqual(null, reopenedWhileTemporary.ReadLaunchProfiles(new(instance)).Value!.TemporaryId);
        AssertFalse(reopenedWhileTemporary.ReadLaunchOverlay(instance).SafeLaunch);
        AssertTrue(policy.EndTemporaryLaunch(new(instance, Revision())).IsSuccess);
        AssertEqual("10240", Effective(policy, "game.memory", instance).Value.Value);
        AssertFalse(policy.Read(new(instance) { TemporaryId = "session" }).IsSuccess);
        AssertTrue(policy.DeleteLaunchProfile(new(instance, "playing", Revision())).IsSuccess);
        AssertEqual("6144", Effective(policy, "game.memory", instance).Value.Value);
        AssertFalse(policy.SaveLaunchProfile(new(instance, "../../bad", "Bad", values, new(), Revision())).IsSuccess);
        AssertFalse(policy.BeginTemporaryLaunch(new(instance, "stale", values, new(), Revision() - 1)).IsSuccess);
        AssertFalse(policy.SaveLaunchProfile(new(instance, "global", "Bad", new Dictionary<string, SettingsOverride>
        { ["general.language"] = new(SettingsOverrideMode.Custom, "en") }, new(), Revision())).IsSuccess);
        AssertEqual(0, policy.ReadLaunchProfiles(new(instance)).Value!.Profiles.Count);
    }

    private static void UnsupportedCompatibilityMutationsCannotClaimToDisableChecks()
    {
        var (_, policy) = PolicyFixture(new InMemorySettingsPort());
        long revision = policy.Read(new()).Value!.Revision;
        var result = policy.Set(new("java.compatibility", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false")));
        AssertFalse(result.IsSuccess); AssertTrue(result.Error!.Message.Contains("mandatory", StringComparison.Ordinal));
        AssertEqual(revision, policy.Read(new()).Value!.Revision);
        const string document = "{\"version\":1,\"scope\":\"global\",\"values\":{\"java.compatibility\":{\"mode\":\"Custom\",\"value\":\"false\"}}}";
        AssertTrue(policy.PreviewImport(new(document)).Errors.Count > 0);
        AssertFalse(policy.ApplyImport(new(document, revision)).IsSuccess);
        AssertFalse(policy.Export(new()).Value!.Contains("java.compatibility", StringComparison.Ordinal));
        AssertEqual("true", Effective(policy, "java.compatibility").Value.Value);
    }

    private static void LaunchEnvironmentClasspathAndExitHookCaptureLiteralArguments()
    {
        string root = CreateTempDirectory();
        try
        {
            string head1 = Path.Combine(root, "head one.jar"), head2 = Path.Combine(root, "head-two.jar");
            File.WriteAllBytes(head1, [1]); File.WriteAllBytes(head2, [2]); File.WriteAllBytes(Path.Combine(root, "fixture.jar"), [3]);
            var environment = MinecraftLaunchHooks.ParseEnvironment("NEXA_FIXTURE=a=b ${literal}\nEMPTY=\n");
            AssertEqual("a=b ${literal}", environment["NEXA_FIXTURE"]); AssertEqual("", environment["EMPTY"]);
            foreach (string invalid in new[] { "BAD KEY=value", "A=1\nA=2", "A", "1BAD=value", "A=1\0", new string('x', 32769) })
            {
                bool rejected = false; try { _ = MinecraftLaunchHooks.ParseEnvironment(invalid); } catch (ArgumentException) { rejected = true; }
                AssertTrue(rejected);
            }
            AssertTrue(MinecraftLaunchHooks.ParseClasspathHead(head1 + "\n" + head2).SequenceEqual([head1, head2]));
            bool invalidHead = false; try { _ = MinecraftLaunchHooks.ParseClasspathHead("relative.jar"); } catch (ArgumentException) { invalidHead = true; }
            AssertTrue(invalidHead);
            var (_, policy) = PolicyFixture(new InMemorySettingsPort());
            foreach (var setting in new[] { ("game.environment", "NEXA_FIXTURE=a=b ${literal}"), ("game.classpath-head", head1 + "\n" + head2),
                ("game.post-exit", "echo exited"), ("game.safe-launch", "false") })
                AssertTrue(policy.Set(new(setting.Item1, SettingsLayer.Instance, new(SettingsOverrideMode.Custom, setting.Item2), root)).IsSuccess);
            MinecraftLaunchRequest request = new()
            {
                VersionId = "fixture",
                VersionJson = new JsonObject { ["id"] = "fixture", ["mainClass"] = "fixture.Main" },
                InstanceDirectory = root,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
            };
            request = MinecraftLaunchCoordinator.ApplySettings(request, policy.Read(new(root)).Value!);
            var plan = MinecraftLaunchPlanner.CreatePlan(request);
            AssertTrue(plan.ClasspathEntries.Take(3).SequenceEqual([Path.Combine(root, "fixture.jar"), head1, head2]));
            AssertEqual("a=b ${literal}", plan.ToStartInfo().Environment["NEXA_FIXTURE"]);
            AssertEqual("echo exited", plan.PostExitCommand); AssertFalse(plan.Overlay.SafeLaunch);
            AssertTrue(policy.Set(new("game.environment", SettingsLayer.Instance, new(SettingsOverrideMode.Custom, "NEXA_FIXTURE=changed"), root)).IsSuccess);
            AssertEqual("a=b ${literal}", plan.EnvironmentVariables["NEXA_FIXTURE"]);
            var safe = MinecraftSafeLaunchPolicy.Apply(request with
            {
                Overlay = new(true, ConfigSource: root),
                WrapperCommand = "wrapper",
                PreLaunchCommand = "before",
                CustomJvmArguments = "-Dcustom=1",
                CustomGameArguments = "--custom"
            });
            AssertTrue(safe.Overlay.SafeLaunch); AssertEqual(null, safe.Overlay.ConfigSource);
            AssertEqual(null, safe.CustomJvmArguments); AssertEqual(null, safe.CustomGameArguments);
            AssertEqual("", safe.WrapperCommand); AssertEqual("", safe.PreLaunchCommand); AssertEqual("", safe.PostExitCommand);
            AssertEqual(0, safe.EnvironmentVariables.Count); AssertEqual(0, safe.ClasspathHeadEntries.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask LaunchOverlayRestoresOriginalsAndRejectsUnownedLinks()
    {
        string root = CreateTempDirectory(), game = Path.Combine(root, "game"), source = Path.Combine(root, "source");
        try
        {
            Directory.CreateDirectory(Path.Combine(game, "mods")); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(game, "mods", "original.jar"), "original");
            File.WriteAllText(Path.Combine(source, "temporary.jar"), "temporary");
            await using (var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game,
                new(ModsSource: source, ResourcePacksSource: source, ShaderPacksSource: source, ConfigSource: source)))
            {
                AssertFalse(File.Exists(Path.Combine(game, "mods", "original.jar")));
                AssertEqual("temporary", File.ReadAllText(Path.Combine(game, "mods", "temporary.jar")));
                File.WriteAllText(Path.Combine(game, "mods", "game-generated.jar"), "generated");
                AssertTrue(Directory.Exists(Path.Combine(game, "resourcepacks")));
                bool locked = false;
                try { await using var second = await MinecraftLaunchOverlayLease.AcquireAsync(game, new()); }
                catch (IOException) { locked = true; }
                AssertTrue(locked);
            }
            AssertEqual("original", File.ReadAllText(Path.Combine(game, "mods", "original.jar")));
            AssertFalse(File.Exists(Path.Combine(game, "mods", "temporary.jar")));
            AssertFalse(Directory.Exists(Path.Combine(game, "resourcepacks")));
            AssertFalse(Directory.Exists(Path.Combine(game, "config")));
            await using (var safe = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(SafeLaunch: true, ModsSource: source)))
            { AssertEqual(0, Directory.GetFiles(Path.Combine(game, "mods")).Length); AssertTrue(Directory.Exists(Path.Combine(game, "config"))); }
            AssertEqual("original", File.ReadAllText(Path.Combine(game, "mods", "original.jar")));
            AssertEqual("temporary", File.ReadAllText(Path.Combine(source, "temporary.jar")));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(true), cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            AssertTrue(stopped); AssertTrue(File.Exists(Path.Combine(game, "mods", "original.jar")));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(source, "linked.jar"), Path.Combine(game, "mods", "original.jar"));
                bool rejected = false;
                try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(ModsSource: source)); }
                catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected); AssertEqual("original", File.ReadAllText(Path.Combine(game, "mods", "original.jar")));
            }
            AssertFalse(Directory.Exists(Path.Combine(game, ".nexacl-launch-overlay")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask LaunchOverlayRecoversCrashJournalAndProtectsLiveGames()
    {
        string root = CreateTempDirectory();
        try
        {
            string game = Path.Combine(root, "game"), workspace = Path.Combine(game, ".nexacl-launch-overlay");
            string original = Path.Combine(workspace, "original", "mods");
            Directory.CreateDirectory(original); Directory.CreateDirectory(Path.Combine(game, "mods"));
            File.WriteAllText(Path.Combine(original, "original.jar"), "original");
            File.WriteAllText(Path.Combine(game, "mods", "temporary.jar"), "temporary");
            var journal = new JsonObject
            {
                ["version"] = 1,
                ["game"] = game,
                ["directories"] = new JsonObject { ["mods"] = new JsonObject { ["hadOriginal"] = true } }
            };
            using var current = Process.GetCurrentProcess();
            journal["processId"] = current.Id; journal["processStartTicks"] = current.StartTime.ToUniversalTime().Ticks;
            var processes = new LaunchProcessIdentityFixture(new(PlatformProcessIdentityState.Running, current.StartTime.ToUniversalTime().Ticks));
            await File.WriteAllTextAsync(Path.Combine(workspace, "journal.json"), journal.ToJsonString());
            bool protectedLive = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(), processIdentity: processes); }
            catch (IOException) { protectedLive = true; }
            AssertTrue(protectedLive); AssertTrue(File.Exists(Path.Combine(original, "original.jar")));
            processes.Observation = new(PlatformProcessIdentityState.Unknown);
            bool protectedUnknown = false;
            try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(), processIdentity: processes); }
            catch (IOException) { protectedUnknown = true; }
            AssertTrue(protectedUnknown); AssertTrue(File.Exists(Path.Combine(original, "original.jar")));
            processes.Observation = new(PlatformProcessIdentityState.Running, current.StartTime.ToUniversalTime().Ticks - 1);
            await using (var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(), processIdentity: processes))
                AssertEqual("original", File.ReadAllText(Path.Combine(game, "mods", "original.jar")));
            AssertFalse(Directory.Exists(workspace)); AssertFalse(File.Exists(Path.Combine(game, "mods", "temporary.jar")));
            Directory.CreateDirectory(workspace);
            foreach (string damaged in new[] { "{damaged", new string('x', 8193) })
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, "journal.json"), damaged);
                bool rejected = false;
                try { await using var overlay = await MinecraftLaunchOverlayLease.AcquireAsync(game, new(), processIdentity: processes); }
                catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected); AssertEqual("original", File.ReadAllText(Path.Combine(game, "mods", "original.jar")));
                AssertEqual(damaged, File.ReadAllText(Path.Combine(workspace, "journal.json")));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask PostExitHookWaitsForGameAndOverlayRecovery()
    {
        string root = CreateTempDirectory();
        try
        {
            string game = Path.Combine(root, "game"), mods = Path.Combine(game, "mods"); Directory.CreateDirectory(mods);
            string source = Path.Combine(root, "post-exit-source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(mods, "original.jar"), "original");
            await using var processes = new MinecraftProcessService(new LongLivedProcessPort());
            var hook = new ExitHookCompletionPort(mods);
            var executor = new MinecraftLaunchExecutor(new HookFixtureHost(processes, []), null, hook);
            var session = await executor.ExecuteAsync(HookPlan(game) with { PostExitCommand = "exit-hook", Overlay = new(ModsSource: source) }, "post-exit-fixture");
            AssertTrue(executor.HasPendingFinalization);
            AssertFalse(hook.Started.Task.IsCompleted); AssertFalse(File.Exists(Path.Combine(mods, "original.jar")));
            session.Cancel(); await session.WaitForExitAsync();
            AssertTrue(await hook.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await executor.WaitForFinalizationAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertFalse(executor.HasPendingFinalization);
            AssertEqual("original", File.ReadAllText(Path.Combine(mods, "original.jar")));
            var failed = new MinecraftLaunchExecutor(new HookFixtureHost(processes, []) { FailStart = true }, null, hook);
            bool failureRestored = false;
            try { await failed.ExecuteAsync(HookPlan(game) with { Overlay = new(true) }, "failed-overlay-fixture"); }
            catch (IOException) { failureRestored = true; }
            AssertTrue(failureRestored); AssertFalse(failed.HasPendingFinalization);
            AssertEqual("original", File.ReadAllText(Path.Combine(mods, "original.jar")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ExitHookCompletionPort(string mods) : IMinecraftLaunchHookPort
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(command == "exit-hook" && File.Exists(Path.Combine(mods, "original.jar")));
            return ValueTask.FromResult<IMinecraftLaunchHookSession>(new CompletedExitHookSession());
        }
    }

    private sealed class CompletedExitHookSession : IMinecraftLaunchHookSession
    {
        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
        public void Detach(Action<int>? exited = null) => exited?.Invoke(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LaunchProcessIdentityFixture(PlatformProcessIdentityObservation observation) : IPlatformProcessIdentity
    {
        internal PlatformProcessIdentityObservation Observation { get; set; } = observation;
        public PlatformProcessIdentityObservation Observe(int processId) => Observation;
    }
}
