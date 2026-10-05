using System.Security.Cryptography;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Services.Setup;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask StorageMigrationRejectsInstalledJavaReceiptsAndPreservesLifecycle()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "nexa-storage-installed-java-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string source = Path.Combine(temporary, "source"), target = Path.Combine(temporary, "target"), locatorPath = Path.Combine(temporary, "locator.json");
            string runtimeRoot = Path.Combine(source, "games", "runtime");
            string relative = OperatingSystem.IsWindows() ? "bin/java.exe" : "bin/java";
            var metadata = new FakeInstallerMetadataProvider(JavaRuntimeInstaller.DetectPlatform().ToMojangKey(), relative,
                "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d");
            using var client = new HttpClient(new StaticHttpMessageHandler("hello"));
            using var installer = new JavaRuntimeInstaller(new JavaRuntimeDownloadPlanService(metadata), client);
            string executable = await installer.InstallAsync("java-runtime-test", runtimeRoot);
            string stage = Directory.GetDirectories(Path.Combine(runtimeRoot, ".nexa-java-jobs")).Single();
            byte[] originalIntent = await File.ReadAllBytesAsync(Path.Combine(stage, "intent.json"));
            byte[] originalPlan = await File.ReadAllBytesAsync(Path.Combine(stage, "plan.json"));
            AssertTrue(File.Exists(Path.Combine(stage, "complete")));
            LauncherStorageLocation.Save(locatorPath, source);
            using var storage = new StoragePreferencesService(new(source), locatorPath, isIdle: () => true);
            var preview = await storage.PreviewMigrationAsync(new(target));
            AssertFalse(preview.IsSuccess); AssertEqual(XsrErrorKind.Rejected, preview.Error!.Kind);
            AssertTrue(preview.Error.Message.Contains("Java", StringComparison.Ordinal));
            AssertFalse(File.Exists(locatorPath + ".migration.json")); AssertFalse(Directory.Exists(target));
            // An already queued local request must independently recheck root-bound data at startup.
            string transaction = Guid.NewGuid().ToString("N");
            var pending = new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = 1,
                ["transaction"] = transaction,
                ["source"] = source,
                ["destination"] = target,
                ["revision"] = new string('A', 64)
            };
            await File.WriteAllTextAsync(locatorPath + ".migration.json", pending.ToJsonString());
            var startup = await StoragePreferencesService.CompletePendingMigrationAsync(source, locatorPath);
            AssertFalse(startup.IsSuccess); AssertEqual(XsrErrorKind.Rejected, startup.Error!.Kind);
            AssertTrue(startup.Error.Message.Contains("Java", StringComparison.Ordinal));
            AssertEqual(source, LauncherStorageLocation.Read(locatorPath)); AssertFalse(Directory.Exists(target));
            byte[] retainedIntent = await File.ReadAllBytesAsync(Path.Combine(stage, "intent.json"));
            byte[] retainedPlan = await File.ReadAllBytesAsync(Path.Combine(stage, "plan.json"));
            AssertTrue(originalIntent.SequenceEqual(retainedIntent));
            AssertTrue(originalPlan.SequenceEqual(retainedPlan));
            AssertTrue((await storage.CancelQueuedMigrationAsync(new(transaction))).IsSuccess);
            var (settings, _) = PolicyFixture(); var registry = new JavaRuntimeRegistrationStore(settings);
            var candidate = new JavaRuntimeCandidate(new JavaInstallation(Path.GetDirectoryName(Path.GetDirectoryName(executable))!, executable,
                null, new Version(21, 0, 2), JavaBrand.OpenJdk, JavaArchitecture.X64, true, true));
            using var locator = new RegistrationLocator(candidate);
            var inventory = new JavaRuntimeInventoryService(locator, registry, [runtimeRoot]);
            var owned = (await inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
            AssertEqual(executable, owned.Executable);
            var management = new JavaRuntimeManagementService(locator, registry, [runtimeRoot]);
            var deletion = new JavaRuntimeManageCommand(executable, JavaRuntimeManagementAction.DeleteManaged, 0)
            { ExpectedManagedIdentity = owned.Identity };
            using (var use = await JavaRuntimeUseLease.AcquireAsync(executable))
            {
                AssertTrue(use is not null);
                AssertFalse((await management.ManageAsync(deletion)).IsSuccess);
                AssertEqual("hello", await File.ReadAllTextAsync(executable));
            }
            AssertTrue((await management.ManageAsync(deletion)).IsSuccess); AssertFalse(File.Exists(executable));
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async ValueTask ManagedJavaRemovalCommitsOwnershipAndPreservesExternalFiles()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var inventory = await fixture.Inventory.ReadAsync(new());
        var owned = inventory.Value!.ManagedRuntimes.Single();
        AssertEqual(fixture.Executable, owned.Executable);
        AssertTrue((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.Add, 0))).IsSuccess);
        AssertFalse((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity })).IsSuccess);
        AssertFalse((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 1))).IsSuccess);
        string external = Path.Combine(fixture.Root, "external-java"); await File.WriteAllTextAsync(external, "user runtime");
        var externalRemoval = await fixture.Management.ManageAsync(new(external, JavaRuntimeManagementAction.DeleteManaged, 1)
        { ExpectedManagedIdentity = owned.Identity });
        AssertFalse(externalRemoval.IsSuccess);
        AssertEqual(XsrSemanticId.Parse("java.runtime.registry.rejected"), externalRemoval.Error!.Code);
        AssertTrue((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 1)
        { ExpectedManagedIdentity = owned.Identity })).IsSuccess);
        AssertFalse(Directory.Exists(fixture.Plan.TargetDirectory)); AssertTrue(File.Exists(external));
        AssertEqual(0, fixture.Registry.Read().Registrations.Count);
        var (reopened, _) = PolicyFixture(fixture.Port);
        AssertEqual(2L, new JavaRuntimeRegistrationStore(reopened).Read().Revision);
        AssertEqual(2, fixture.Locator.Invalidations);
        AssertEqual(0, (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Count);
    }

    private static async ValueTask ManagedJavaRemovalRejectsStaleOwnershipReceipts()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var original = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var refreshed = fixture.Plan with { VersionName = "21.0.1" };
        var journal = await JavaInstallJournal.CreateAsync(fixture.RuntimeRoot, refreshed.ComponentName, default);
        await journal.SavePlanAsync(refreshed, default); await journal.CompleteExistingAsync(default);
        File.SetLastWriteTimeUtc(Path.Combine(journal.Stage, "complete"), DateTime.UtcNow.AddMinutes(1));
        var current = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        AssertFalse(string.Equals(original.Identity, current.Identity, StringComparison.Ordinal));
        AssertFalse((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = original.Identity })).IsSuccess);
        AssertTrue(File.Exists(fixture.Executable)); AssertEqual(0L, fixture.Registry.Read().Revision);
        AssertTrue((await fixture.Management.ManageAsync(new(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = current.Identity })).IsSuccess);
    }

    private static async ValueTask ManagedJavaRemovalRejectsChangedExtraAndLinkedFiles()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var command = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        await File.WriteAllTextAsync(fixture.Executable, "changed");
        AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
        AssertEqual("changed", await File.ReadAllTextAsync(fixture.Executable));
        await File.WriteAllTextAsync(fixture.Executable, "owned java");
        string extra = Path.Combine(fixture.Plan.TargetDirectory, "user.txt"); await File.WriteAllTextAsync(extra, "preserve");
        AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess); AssertTrue(File.Exists(extra));
        File.Delete(extra);
        if (!OperatingSystem.IsWindows())
        {
            string outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
            string file = Path.Combine(outside, "private.txt"); await File.WriteAllTextAsync(file, "untouched");
            string linked = Path.Combine(fixture.Plan.TargetDirectory, "linked"); Directory.CreateSymbolicLink(linked, outside);
            AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
            AssertEqual("untouched", await File.ReadAllTextAsync(file)); Directory.Delete(linked);
            string bin = Path.GetDirectoryName(fixture.Executable)!;
            Directory.Move(bin, bin + "-original"); Directory.CreateSymbolicLink(bin, bin + "-original");
            AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
            Directory.Delete(bin); Directory.Move(bin + "-original", bin);
        }
        AssertEqual(0L, fixture.Registry.Read().Revision); AssertEqual(0, fixture.Locator.Invalidations);
    }

    private static async ValueTask ManagedJavaRemovalRollsBackPersistenceAndCancellation()
    {
        var failing = new PolicyFailingPort();
        using var fixture = await ManagedJavaFixture.CreateAsync(failing);
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var command = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        failing.Fail = true;
        AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
        AssertTrue(File.Exists(fixture.Executable)); AssertEqual("owned java", await File.ReadAllTextAsync(fixture.Executable));
        AssertEqual(0L, fixture.Registry.Read().Revision); AssertEqual(0, fixture.Locator.Invalidations);
        failing.Fail = false;
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await fixture.Management.ManageAsync(command, stop.Token); throw new InvalidOperationException("Canceled deletion completed."); }
        catch (OperationCanceledException) { }
        AssertTrue(File.Exists(fixture.Executable)); AssertEqual(0L, fixture.Registry.Read().Revision);
        using var lateStop = new CancellationTokenSource();
        var stopping = new JavaRuntimeManagementService(fixture.Locator, new CancelingJavaStore(fixture.Registry, lateStop), [fixture.RuntimeRoot]);
        try { await stopping.ManageAsync(command, lateStop.Token); throw new InvalidOperationException("Late-canceled deletion completed."); }
        catch (OperationCanceledException) { }
        AssertTrue(lateStop.IsCancellationRequested); AssertTrue(File.Exists(fixture.Executable));
        AssertEqual(0L, fixture.Registry.Read().Revision); AssertEqual(0, fixture.Locator.Invalidations);
    }

    private sealed class CancelingJavaStore(IJavaRuntimeRegistrationStore inner, CancellationTokenSource stop) : IJavaRuntimeRegistrationStore
    {
        private int _reads;
        public JavaRuntimeRegistrySnapshot Read()
        {
            var snapshot = inner.Read();
            if (++_reads == 2) stop.Cancel();
            return snapshot;
        }
        public XsrResult Write(long expectedRevision, IReadOnlyList<JavaRuntimeRegistration> registrations) => inner.Write(expectedRevision, registrations);
    }

    private static async ValueTask ManagedJavaRemovalExcludesActiveUsesAndInstallers()
    {
        using var fixture = await ManagedJavaFixture.CreateAsync();
        var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
        var command = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
        { ExpectedManagedIdentity = owned.Identity };
        using (var first = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable))
        {
            AssertTrue(first is not null);
            using var second = await JavaRuntimeUseLease.AcquireAsync(fixture.Executable);
            AssertTrue(second is not null);
            AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
            AssertTrue(File.Exists(fixture.Executable));
        }
        using (var install = JavaRuntimeManagedStore.AcquireRoot(fixture.RuntimeRoot))
            AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
        var pending = await JavaInstallJournal.CreateAsync(fixture.RuntimeRoot, fixture.Plan.ComponentName, default);
        await pending.SavePlanAsync(fixture.Plan, default);
        AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
        await pending.MarkCanceledAsync();
        string uses = Path.Combine(fixture.RuntimeRoot, ".nexa-java-uses", fixture.Plan.ComponentName);
        string unknownLease = Path.Combine(uses, Guid.NewGuid().ToString("N") + ".lease");
        await File.WriteAllTextAsync(unknownLease, "stale");
        AssertFalse((await fixture.Management.ManageAsync(command)).IsSuccess);
        AssertTrue(File.Exists(unknownLease)); AssertTrue(File.Exists(fixture.Executable));
        File.Delete(unknownLease);
        AssertTrue((await fixture.Management.ManageAsync(command)).IsSuccess);
        AssertFalse(File.Exists(fixture.Executable));
    }

    private static async ValueTask ManagedJavaDirectLaunchOwnsUseLeaseUntilProcessExit()
    {
        foreach (string scenario in new[] { "running", "hook-failure", "start-failure", "cancel-hook", "cancel-process" })
        {
            using var fixture = await ManagedJavaFixture.CreateAsync();
            var owned = (await fixture.Inventory.ReadAsync(new())).Value!.ManagedRuntimes.Single();
            var removal = new JavaRuntimeManageCommand(fixture.Executable, JavaRuntimeManagementAction.DeleteManaged, 0)
            { ExpectedManagedIdentity = owned.Identity };
            using var cancellation = new CancellationTokenSource();
            var processPort = new LongLivedProcessPort();
            await using var processes = new MinecraftProcessService(processPort);
            var hook = new ManagedJavaLeaseHook();
            var host = new ManagedJavaLeaseHost(processes)
            {
                FailStart = scenario == "start-failure",
                CancelAfterStart = scenario == "cancel-process" ? cancellation : null,
            };
            var executor = new MinecraftLaunchExecutor(host, log: null, hooks: hook);
            var plan = HookPlan(Path.Combine(fixture.Root, "game")) with
            {
                JavaExecutablePath = fixture.Executable,
                PreLaunchCommand = "managed-java-fixture",
            };
            Task<MinecraftProcessSession> launch = executor.ExecuteAsync(plan, scenario,
                cancellationToken: cancellation.Token).AsTask();
            try
            {
                await hook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                AssertTrue(processPort.LastProcess is null);
                AssertFalse((await fixture.Management.ManageAsync(removal)).IsSuccess);
                AssertTrue(File.Exists(fixture.Executable));
                string uses = Path.Combine(fixture.RuntimeRoot, ".nexa-java-uses", fixture.Plan.ComponentName);
                AssertEqual(1, Directory.GetFiles(uses, "*.lease").Length);

                if (scenario == "cancel-hook") cancellation.Cancel();
                else hook.Complete(scenario == "hook-failure" ? 17 : 0);
                if (scenario == "running")
                {
                    MinecraftProcessSession session = await launch.WaitAsync(TimeSpan.FromSeconds(5));
                    AssertFalse(session.Process.HasExited);
                    AssertFalse((await fixture.Management.ManageAsync(removal)).IsSuccess);
                    AssertTrue(File.Exists(fixture.Executable));
                    session.Cancel();
                    await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    bool rejected = false;
                    try { await launch.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) when (scenario is "cancel-hook" or "cancel-process") { rejected = true; }
                    catch (InvalidOperationException error) when (scenario == "hook-failure" && error.Message.Contains("exit code 17", StringComparison.Ordinal)) { rejected = true; }
                    catch (IOException error) when (scenario == "start-failure" && error.Message == "managed-java-start-failed") { rejected = true; }
                    AssertTrue(rejected);
                    AssertTrue(processPort.LastProcess is null || processPort.LastProcess.HasExited);
                }
                AssertTrue(hook.Disposed);
                using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (Directory.EnumerateFiles(uses, "*.lease").Any())
                    await Task.Delay(10, releaseDeadline.Token);
                AssertTrue((await fixture.Management.ManageAsync(removal)).IsSuccess);
                AssertFalse(File.Exists(fixture.Executable));
            }
            finally
            {
                cancellation.Cancel();
                hook.Complete(0);
                try
                {
                    MinecraftProcessSession session = await launch.WaitAsync(TimeSpan.FromSeconds(5));
                    session.Cancel();
                    await session.WaitForExitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException) { }
            }
        }
    }

    private sealed class ManagedJavaLeaseHook : IMinecraftLaunchHookPort, IMinecraftLaunchHookSession
    {
        private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed { get; private set; }
        internal void Complete(int exitCode) => _completion.TrySetResult(exitCode);
        public ValueTask<IMinecraftLaunchHookSession> StartAsync(string command, string workingDirectory, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IMinecraftLaunchHookSession>(this);
        }
        public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }
        public void Detach(Action<int>? exited = null) => throw new InvalidOperationException("This fixture waits for its hook.");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManagedJavaLeaseHost(MinecraftProcessService processes) : IJvmHost
    {
        internal bool FailStart { get; init; }
        internal CancellationTokenSource? CancelAfterStart { get; init; }
        public async ValueTask<MinecraftProcessSession> StartAsync(MinecraftLaunchPlan plan, string instanceId, CancellationToken cancellationToken = default)
        {
            if (FailStart) throw new IOException("managed-java-start-failed");
            MinecraftProcessSession session = await processes.StartAsync(plan, instanceId, cancellationToken);
            CancelAfterStart?.Cancel();
            return session;
        }
        public JvmHostEnvironment Describe(MinecraftLaunchPlan plan) => new JvmHostService(processes).Describe(plan);
        public JvmHostControlResult Suspend(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult ResumeProcess(MinecraftProcessSession session) => throw new NotSupportedException();
        public JvmHostControlResult SetPriority(MinecraftProcessSession session, System.Diagnostics.ProcessPriorityClass priority) => new(true, "fixture", "fixture");
        public JvmHostControlResult SetAffinity(MinecraftProcessSession session, nint affinityMask) => throw new NotSupportedException();
    }

    private sealed class ManagedJavaFixture : IDisposable
    {
        internal required string Root, RuntimeRoot, Executable;
        internal required JavaRuntimeDownloadPlan Plan;
        internal required ISettingsPort Port;
        internal required JavaRuntimeRegistrationStore Registry;
        internal required RegistrationLocator Locator;
        internal required JavaRuntimeManagementService Management;
        internal required JavaRuntimeInventoryService Inventory;
        internal static async Task<ManagedJavaFixture> CreateAsync(ISettingsPort? port = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "nexa-managed-java-" + Guid.NewGuid().ToString("N"));
            string runtimeRoot = Path.Combine(root, "runtime");
            string home = Path.Combine(runtimeRoot, "java-runtime-gamma");
            string relative = "bin/" + (OperatingSystem.IsWindows() ? "java.exe" : "java");
            string executable = Path.Combine(home, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            byte[] payload = "owned java"u8.ToArray(); await File.WriteAllBytesAsync(executable, payload);
            var plan = new JavaRuntimeDownloadPlan("java-runtime-gamma", "21", "https://example.test/manifest.json", home,
                [new(relative, executable, "https://example.test/java", Convert.ToHexString(SHA1.HashData(payload)), payload.Length)]);
            var journal = await JavaInstallJournal.CreateAsync(runtimeRoot, plan.ComponentName, default);
            await journal.SavePlanAsync(plan, default); await journal.CompleteExistingAsync(default);
            port ??= new InMemorySettingsPort(); var (settings, _) = PolicyFixture(port);
            var registry = new JavaRuntimeRegistrationStore(settings);
            var candidate = new JavaRuntimeCandidate(new JavaInstallation(home, executable, null,
                new Version(21, 0), JavaBrand.Microsoft, JavaArchitecture.X64, true, true));
            var locator = new RegistrationLocator(candidate);
            return new()
            {
                Root = root,
                RuntimeRoot = runtimeRoot,
                Executable = executable,
                Plan = plan,
                Port = port,
                Registry = registry,
                Locator = locator,
                Management = new(locator, registry, [runtimeRoot]),
                Inventory = new(locator, registry, [runtimeRoot])
            };
        }
        public void Dispose() { Locator.Dispose(); Directory.Delete(Root, true); }
    }
}
