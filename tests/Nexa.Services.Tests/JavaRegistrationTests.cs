using Nexa.Services.Minecraft.Java;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask JavaRegistrationPersistsAndRemovalKeepsFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "nexa-java-registry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, OperatingSystem.IsWindows() ? "java.exe" : "java");
        await File.WriteAllTextAsync(executable, "fixture executable");
        try
        {
            var port = new InMemorySettingsPort(); var (settings, policy) = PolicyFixture(port);
            var registry = new JavaRuntimeRegistrationStore(settings);
            var candidate = Candidate("registry-java", new Version(21, 0), JavaBrand.Microsoft, false);
            candidate = candidate with
            {
                Installation = new JavaInstallation(root, executable, null,
                new Version(21, 0), JavaBrand.Microsoft, JavaArchitecture.X64, true, false)
            };
            using var locator = new RegistrationLocator(candidate); var service = new JavaRuntimeManagementService(locator, registry);
            AssertTrue((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Add, 0))).IsSuccess);
            AssertEqual(1, locator.Invalidations); AssertEqual(1L, registry.Read().Revision);
            AssertTrue(policy.Set(new("game.width", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1280"))).IsSuccess);
            // Unrelated settings revisions must not invalidate a registry command.
            AssertTrue((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Disable, 1))).IsSuccess);
            var (reopened, _) = PolicyFixture(port); var durable = new JavaRuntimeRegistrationStore(reopened);
            AssertEqual(2L, durable.Read().Revision); AssertFalse(durable.Read().Registrations.Single().Enabled);
            AssertTrue(durable.Read().Registrations.Single().Custom);
            AssertFalse((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Remove, 1))).IsSuccess);
            AssertEqual(2, locator.Invalidations); AssertEqual(1, registry.Read().Registrations.Count);
            // Removal does not inspect the executable, including after it has disappeared.
            locator.Candidate = null;
            AssertTrue((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Remove, 2))).IsSuccess);
            AssertEqual(0, registry.Read().Registrations.Count); AssertTrue(File.Exists(executable));
            AssertEqual("fixture executable", await File.ReadAllTextAsync(executable));
            // Executable registration is local-only, outside portable settings import/export.
            AssertFalse(policy.Export(new()).Value!.Contains(JavaRuntimeInventoryContract.RegistryKey, StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask JavaRegistrationFailureAndLateProbeNeverCommit()
    {
        var port = new PolicyFailingPort(); var (settings, _) = PolicyFixture(port);
        var registry = new JavaRuntimeRegistrationStore(settings);
        var candidate = Candidate("registry-failure", new Version(17, 0), JavaBrand.Microsoft, false);
        string executable = candidate.Installation.JavaExecutablePath;
        using var locator = new RegistrationLocator(candidate); var service = new JavaRuntimeManagementService(locator, registry);
        port.Fail = true;
        AssertFalse((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Add, 0))).IsSuccess);
        AssertEqual(0, locator.Invalidations); AssertEqual(0L, registry.Read().Revision);
        port.Fail = false; locator.Candidate = null;
        AssertFalse((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Add, 0))).IsSuccess);
        locator.Candidate = candidate;
        locator.Probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var pending = service.ManageAsync(new(executable, JavaRuntimeManagementAction.Add, 0), stop.Token).AsTask();
        AssertTrue(locator.Entered.Wait(TimeSpan.FromSeconds(5)));
        stop.Cancel(); locator.Probe.SetResult(candidate);
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Late Java registration committed."); }
        catch (OperationCanceledException) { }
        AssertEqual(0L, registry.Read().Revision); AssertEqual(0, locator.Invalidations);
    }

    private static async ValueTask JavaRegistrationDisablesAutomaticAndExplicitSelection()
    {
        var (settings, _) = PolicyFixture(); var registry = new JavaRuntimeRegistrationStore(settings);
        var candidate = Candidate("registry-disabled", new Version(21, 0), JavaBrand.Microsoft, false);
        using var probe = new RegistrationLocator(candidate);
        var locator = new LocalJavaRuntimeLocator(inspectionPort: probe) { RegisteredRuntimes = () => registry.Read().Registrations };
        string executable = candidate.Installation.JavaExecutablePath;
        var service = new JavaRuntimeManagementService(locator, registry);
        AssertTrue((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Disable, 0))).IsSuccess);
        var inspected = await locator.InspectAsync(executable); AssertFalse(inspected!.IsEnabled);
        var requirement = JavaRequirementResolution.Valid(new(new Version(21, 0), new Version(21, 999)));
        AssertFalse((await new JavaSelectionService(locator).SelectAsync(requirement, new ExistingJavaPreference(executable))).Success);
        AssertFalse((await new JavaSelectionService(new InMemoryJavaLocator([inspected])).SelectAsync(requirement, new AutoSelectJavaPreference())).Success);
        AssertFalse((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Remove, 1))).IsSuccess);
        AssertTrue((await service.ManageAsync(new(executable, JavaRuntimeManagementAction.Enable, 1))).IsSuccess);
        AssertTrue((await new JavaSelectionService(locator).SelectAsync(requirement, new ExistingJavaPreference(executable))).Success);
    }

    private sealed class RegistrationLocator(JavaRuntimeCandidate? candidate) : IJavaRuntimeLocator, IDisposable
    {
        public JavaRuntimeCandidate? Candidate = candidate;
        public TaskCompletionSource<JavaRuntimeCandidate?>? Probe;
        public readonly ManualResetEventSlim Entered = new();
        public int Invalidations;
        public void Invalidate() => Interlocked.Increment(ref Invalidations);
        public ValueTask<IReadOnlyList<JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<JavaRuntimeCandidate>>(Candidate is null ? [] : [Candidate]);
        public ValueTask<JavaRuntimeCandidate?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default)
        { if (Probe is not null) { Entered.Set(); return new(Probe.Task); } return ValueTask.FromResult(Candidate); }
        public void Dispose() => Entered.Dispose();
    }
}
