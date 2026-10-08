using System.Diagnostics;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.ModLoaders;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void LaunchHooksCaptureScopedLegacyAndEmptyOverrides()
    {
        var port = new InMemorySettingsPort(); var (legacy, policy) = PolicyFixture(port);
        string root = CreateTempDirectory(), instance = Path.Combine(root, "versions", "hooks");
        try
        {
            Directory.CreateDirectory(instance); File.WriteAllBytes(Path.Combine(instance, "hooks.jar"), [1]);
            MinecraftLaunchRequest original = new()
            {
                VersionJson = new JsonObject { ["id"] = "hooks", ["mainClass"] = "fixture.Main" },
                VersionId = "hooks",
                InstanceDirectory = instance,
                MinecraftRootDirectory = root,
                PlayerName = "fixture",
                PlayerUuid = "fixture",
            };
            Set("game.wrapper", SettingsLayer.Global, "global-wrapper --mode 'two words'");
            Set("game.pre-launch", SettingsLayer.Global, "echo global");
            Set("game.pre-launch-wait", SettingsLayer.Global, "false");
            AssertEqual(false, legacy.GetValue<bool>("LaunchAdvanceRunWait").Value);
            MinecraftLaunchRequest Read(MinecraftLaunchRequest? request = null, string? directory = null)
                => MinecraftLaunchCoordinator.ApplySettings(request ?? original, policy.Read(new(directory ?? instance)).Value!);
            var global = Read();
            AssertEqual("global-wrapper --mode 'two words'", global.WrapperCommand);
            AssertEqual("echo global", global.PreLaunchCommand); AssertFalse(global.WaitForPreLaunchCommand);
            var metadata = original with { WrapperCommand = "metadata-wrapper", PreLaunchCommand = "echo metadata", WaitForPreLaunchCommand = true };
            var preserved = Read(metadata);
            AssertEqual("metadata-wrapper", preserved.WrapperCommand); AssertEqual("echo metadata", preserved.PreLaunchCommand);
            AssertTrue(preserved.WaitForPreLaunchCommand);
            Set("game.wrapper", SettingsLayer.Instance, ""); Set("game.pre-launch", SettingsLayer.Instance, "");
            AssertEqual("", Read(metadata).WrapperCommand); AssertEqual("", Read(metadata).PreLaunchCommand);
            AssertFalse(Read(metadata).WaitForPreLaunchCommand);
            Set("game.wrapper", SettingsLayer.Instance, "instance-wrapper");
            Set("game.pre-launch", SettingsLayer.Instance, "echo instance");
            Set("game.pre-launch-wait", SettingsLayer.Instance, "true");
            var captured = MinecraftLaunchPlanner.CreatePlan(Read());
            AssertEqual("instance-wrapper", captured.WrapperCommand); AssertEqual("echo instance", captured.PreLaunchCommand);
            AssertTrue(captured.WaitForPreLaunchCommand);
            Set("game.pre-launch", SettingsLayer.Instance, "echo changed");
            AssertEqual("echo instance", captured.PreLaunchCommand);
            string sameIdOtherRoot = Path.Combine(root, "other-root", "versions", "hooks");
            AssertEqual("echo global", Read(directory: sameIdOtherRoot).PreLaunchCommand);
            AssertTrue(SettingsPolicySchema.ByKey["game.pre-launch-wait"].InstanceOverride);
            AssertEqual(SettingsApplyTiming.NextLaunch, SettingsPolicySchema.ByKey["game.pre-launch-wait"].Timing);
            foreach (string key in new[] { "game.wrapper", "game.pre-launch" })
                AssertFalse(SettingsPolicySchema.ByKey[key].Exportable);
            AssertTrue(SettingsPolicySchema.ByKey["game.pre-launch-wait"].Exportable);
            foreach (string malformed in new[] { "\"unclosed", "bad\nwrapper", new string('x', MinecraftLaunchHooks.MaximumCommandLength + 1) })
                AssertFalse(policy.Set(new("game.wrapper", SettingsLayer.Global, new(SettingsOverrideMode.Custom, malformed))).IsSuccess);

            void Set(string key, SettingsLayer layer, string value)
                => AssertTrue(policy.Set(new(key, layer, new(SettingsOverrideMode.Custom, value), layer == SettingsLayer.Instance ? instance : null)).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void LaunchWrapperSyntaxIsBoundedAndPathCompatible()
    {
        AssertTrue(MinecraftLaunchHooks.ParseWrapper("  ").Count == 0);
        AssertTrue(MinecraftLaunchHooks.ParseWrapper("wrapper 'two words' \"\" '--literal=${auth_access_token}'").SequenceEqual(
            ["wrapper", "two words", "", "--literal=${auth_access_token}"]));
        AssertTrue(MinecraftLaunchHooks.ParseWrapper("\"C:\\Program Files\\Wrapper\\wrap.exe\" --cache \\\\server\\share").SequenceEqual(
            ["C:\\Program Files\\Wrapper\\wrap.exe", "--cache", "\\\\server\\share"]));
        AssertTrue(MinecraftLaunchHooks.ParseWrapper("wrapper \"a\\\"b\" 'a\\b'").SequenceEqual(["wrapper", "a\"b", "a\\b"]));
        foreach (string malformed in new[] { "''", "\"missing", "missing'", "wrap\narg", "wrap\rarg", "wrap\0arg", new string('x', 32769), string.Join(' ', Enumerable.Repeat("arg", 257)) })
        {
            bool rejected = false;
            try { _ = MinecraftLaunchHooks.ParseWrapper(malformed); }
            catch (ArgumentException) { rejected = true; }
            AssertTrue(rejected);
        }
        MinecraftLaunchHooks.ValidatePreLaunch("echo first\necho second");
        foreach (string malformed in new[] { "\0", new string('x', 32769) })
        {
            bool rejected = false;
            try { MinecraftLaunchHooks.ValidatePreLaunch(malformed); }
            catch (ArgumentException) { rejected = true; }
            AssertTrue(rejected);
        }
    }

    private static async ValueTask LaunchWrapperPreservesPrivateBootstrapTransport()
    {
        string root = CreateTempDirectory();
        try
        {
            const string credential = "not-a-real-hook-test-credential";
            var plan = HookPlan(root) with
            {
                Arguments = ["fixture.Main", "--accessToken", credential],
                WrapperCommand = "\"/fixture wrapper\" --flag 'two words' \"\"",
            };
            string hostPath = Path.Combine(root, "fixture-jvm-host");
            var port = new RecordingArgumentPort();
            await using var service = new MinecraftProcessService(port, jvmHostExecutable: hostPath);
            try { await service.StartAsync(plan, "wrapper-fixture"); }
            catch (IOException error) when (error.Message == "recording-port-stop") { }
            AssertEqual(1, port.Calls);
            AssertEqual("/fixture wrapper", port.StartInfo!.FileName);
            AssertTrue(port.StartInfo.ArgumentList.SequenceEqual(["--flag", "two words", "", hostPath, "--jvm-host"]));
            AssertTrue(port.StartInfo.RedirectStandardInput);
            AssertFalse(port.StartInfo.ArgumentList.Any(argument => argument.Contains(credential, StringComparison.Ordinal)));
            var host = new JvmHostService(service);
            AssertEqual(plan.WrapperCommand, host.Describe(plan).Wrapper);
            var malformed = plan with { WrapperCommand = "\"bad" };
            bool rejected = false;
            try { await service.StartAsync(malformed, "malformed-wrapper"); }
            catch (ArgumentException) { rejected = true; }
            AssertTrue(rejected); AssertEqual(1, port.Calls);
            var publicPort = new RecordingArgumentPort();
            await using var refused = new MinecraftProcessService(publicPort);
            rejected = false;
            try { await refused.StartAsync(plan, "public-wrapper"); }
            catch (InvalidOperationException) { rejected = true; }
            AssertTrue(rejected); AssertEqual(0, publicPort.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask LaunchHooksExecuteInOrderAndOwnFailureCancellation()
    {
        string root = CreateTempDirectory();
        try
        {
            List<string> order = [];
            await using var processes = new MinecraftProcessService(new LongLivedProcessPort());
            var hooks = new HookFixturePort(order);
            var host = new HookFixtureHost(processes, order);
            var executor = new MinecraftLaunchExecutor(host, log: null, hooks: hooks);
            var plan = HookPlan(root) with { PreLaunchCommand = "echo exact 'user text'" };
            var session = await executor.ExecuteAsync(plan, "hook-fixture", stage: order.Add);
            AssertTrue(order.SequenceEqual(["pre_launch", "custom_command", "hook_start", "hook_wait", "start_process", "jvm_start", "hook_dispose"]));
            AssertEqual(plan.PreLaunchCommand, hooks.Command); AssertEqual(root, hooks.Directory);
            AssertFalse(hooks.Last!.Detached); AssertTrue(hooks.Last.Disposed);
            session.Cancel(); await session.WaitForExitAsync();

            order.Clear(); hooks.ExitCode = 17;
            bool failed = false;
            try { await executor.ExecuteAsync(plan, "failed-hook"); }
            catch (InvalidOperationException error) when (error.Message.Contains("exit code 17", StringComparison.Ordinal)) { failed = true; }
            AssertTrue(failed); AssertTrue(hooks.Last!.Disposed); AssertFalse(order.Contains("jvm_start"));

            order.Clear(); hooks.ExitCode = 0; host.FailStart = true;
            try { await executor.ExecuteAsync(plan with { WaitForPreLaunchCommand = false }, "nonwait-failure"); }
            catch (IOException error) when (error.Message == "fixture-start-failed") { }
            AssertFalse(hooks.Last!.Detached); AssertTrue(hooks.Last.Disposed); AssertFalse(order.Contains("hook_wait"));

            order.Clear(); host.FailStart = false;
            session = await executor.ExecuteAsync(plan with { WaitForPreLaunchCommand = false }, "nonwait-success");
            AssertTrue(hooks.Last!.Detached); AssertTrue(hooks.Last.Disposed); AssertFalse(order.Contains("hook_wait"));
            AssertTrue(order.IndexOf("hook_detach") > order.IndexOf("jvm_start"));
            session.Cancel(); await session.WaitForExitAsync();

            order.Clear(); hooks.BlockWait = true;
            using var cancellation = new CancellationTokenSource();
            var previousHook = hooks.Last;
            Task<HookFixtureSession> nextHook = hooks.NextStart;
            Task<MinecraftProcessSession> waiting = executor.ExecuteAsync(plan, "cancelled-hook", cancellationToken: cancellation.Token).AsTask();
            HookFixtureSession cancelledHook = await nextHook.WaitAsync(TimeSpan.FromSeconds(5));
            AssertFalse(ReferenceEquals(previousHook, cancelledHook));
            await cancelledHook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            bool cancelled = false;
            try { await waiting; } catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled); AssertTrue(ReferenceEquals(cancelledHook, hooks.Last));
            AssertTrue(cancelledHook.Disposed); AssertFalse(cancelledHook.Detached); AssertFalse(order.Contains("jvm_start"));
            AssertTrue(order.SequenceEqual(["hook_start", "hook_wait", "hook_dispose"]));

            order.Clear(); hooks.BlockWait = false;
            nextHook = hooks.NextStart;
            cancelled = false;
            try { await executor.ExecuteAsync(plan, "already-cancelled", cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled); AssertEqual(0, order.Count); AssertFalse(nextHook.IsCompleted);
        }
        finally { await WaitForLaunchGameDirectoryReleasedAsync(root); Directory.Delete(root, true); }
    }

    private static async ValueTask LaunchWrapperRealProcessForwardsPrivateBootstrapAndExit()
    {
        if (OperatingSystem.IsWindows()) return; // Unix fixture uses executable shell scripts.
        string root = CreateTempDirectory();
        try
        {
            string wrapper = Path.Combine(root, "wrapper fixture.sh");
            string host = Path.Combine(root, "private host fixture.sh");
            await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nexec \"$@\"\n");
            await File.WriteAllTextAsync(host, "#!/bin/sh\n[ \"$1\" = '--jvm-host' ] || exit 19\ncat > bootstrap.bin\nexit 0\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            File.SetUnixFileMode(host, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var plan = HookPlan(root) with
            {
                Arguments = ["-Dfixture=secret-placeholder", "fixture.Main", "--accessToken", "fake-private-token"],
                MainClassIndex = 1,
                JavaExecutablePath = Path.Combine(root, "fixture-java"),
                WrapperCommand = "/bin/sh \"" + wrapper + "\"",
            };
            await using var processes = new MinecraftProcessService(jvmHostExecutable: host);
            var session = await processes.StartAsync(plan, "private-wrapper-fixture");
            AssertEqual(0, await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            AssertEqual(MinecraftProcessState.Exited, session.Snapshot.State);
            await using var stream = File.OpenRead(Path.Combine(root, "bootstrap.bin"));
            var received = await JvmHostBootstrap.ReadAsync(stream);
            AssertEqual(plan.JavaExecutablePath, received.JavaExecutable);
            AssertEqual("fixture.Main", received.MainClass);
            AssertTrue(received.JvmArguments.SequenceEqual(["-Dfixture=secret-placeholder"]));
            AssertTrue(received.GameArguments.SequenceEqual(["--accessToken", "fake-private-token"]));
            AssertFalse(session.Process.StartInfo.ArgumentList.Any(argument => argument.Contains("fake-private-token", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static MinecraftLaunchPlan HookPlan(string root) => new("java", root, ["fixture.Main"], [], [],
        new MinecraftModLoaderDescriptor(MinecraftModLoaderKind.Vanilla, null, "fixture.Main", []))
    { MainClassIndex = 0, NativesDirectory = Path.Combine(root, "natives") };

    private sealed class HookFixturePort(List<string> order) : IMinecraftLaunchHookPort
    {
        private TaskCompletionSource<HookFixtureSession> _nextStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExitCode { get; set; }
        public bool BlockWait { get; set; }
        public string? Command { get; private set; }
        public string? Directory { get; private set; }
        public HookFixtureSession? Last { get; private set; }
        public Task<HookFixtureSession> NextStart => _nextStart.Task;
        public ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            AssertTrue(System.IO.Directory.Exists(Path.Combine(workingDirectory, "natives")));
            Command = command; Directory = workingDirectory; order.Add("hook_start");
            Last = new(order, ExitCode, BlockWait);
            var nextStart = _nextStart;
            _nextStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
            nextStart.TrySetResult(Last);
            return ValueTask.FromResult<IMinecraftLaunchHookSession>(Last);
        }
    }

    private sealed class HookFixtureSession(List<string> order, int exitCode, bool block) : IMinecraftLaunchHookSession
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Detached { get; private set; }
        public bool Disposed { get; private set; }
        public async ValueTask<int> WaitForExitAsync(CancellationToken token = default)
        {
            order.Add("hook_wait"); Entered.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return exitCode;
        }
        public void Detach(Action<int>? exited = null) { Detached = true; order.Add("hook_detach"); }
        public ValueTask DisposeAsync() { Disposed = true; order.Add("hook_dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class HookFixtureHost(MinecraftProcessService processes, List<string> order) : IJvmHost
    {
        public bool FailStart { get; set; }
        public ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId, CancellationToken token = default)
        {
            order.Add("jvm_start");
            if (FailStart) throw new IOException("fixture-start-failed");
            return processes.StartAsync(plan, instanceId, token);
        }
        public JvmHostEnvironment Describe(MinecraftLaunchPlan plan) => new JvmHostService(processes).Describe(plan);
        public JvmHostControlResult Suspend(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult ResumeProcess(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult SetPriority(MinecraftProcessSession session, ProcessPriorityClass priority) => new(true, "fixture", "fixture");
        public JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask) => throw new NotSupportedException();
    }
}
