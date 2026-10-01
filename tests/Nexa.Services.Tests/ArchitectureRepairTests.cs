using Nexa.Services.Accounts;
using Nexa.Services.Minecraft.Install;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class DelayedProfilePort : IAsyncLaunchProfilePort
    {
        public TaskCompletionSource<LaunchProfileSet> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public LaunchProfileSet Load() => throw new InvalidOperationException("Synchronous initialization must never be used.");
        public ValueTask<LaunchProfileSet> LoadAsync(CancellationToken cancellationToken = default) => new(Reading.Task.WaitAsync(cancellationToken));
        public void Save(LaunchProfileSet profiles) => Writes++;
    }

    private static async ValueTask ProfileInitializationIsAsyncAndPreservesPendingStore()
    {
        DelayedProfilePort port = new();
        await using AccountService accounts = CreateAccountService(port);
        AssertTrue(!accounts.Initialization.IsCompleted);
        AssertTrue(!accounts.AddProfile(SampleProfile("Pending")).IsSuccess);
        AssertEqual(0, port.Writes);
        port.Reading.SetResult(new LaunchProfileSet { Profiles = [SampleProfile("Stored")] });
        await accounts.Initialization;
        AssertTrue(accounts.LoadError is null);
        AssertEqual("Stored", accounts.GetViews()[0].Username);
        AssertEqual(0, port.Writes);
    }

    private static async ValueTask ProfileInitializationCanBeCancelledAtShutdown()
    {
        DelayedProfilePort port = new();
        AccountService accounts = CreateAccountService(port);
        await accounts.DisposeAsync();
        AssertTrue(accounts.Initialization.IsCompleted);
        AssertEqual(0, port.Writes);
    }

    private static void InstallDraftBelongsToServiceAndRejectsIncompatibleAddons()
    {
        XsrStateStoreBuilder builder = new();
        MinecraftInstallDraftContract.DeclareState(builder);
        XsrStateStore store = builder.Build();
        MinecraftInstallDraftService drafts = new(store);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.SetVersion, "1.20.1")).IsSuccess);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.SetChosen, Chosen: true)).IsSuccess);
        AssertTrue(!drafts.Apply(new(InstallDraftChangeKind.AddAddon, Loader: InstallLoader.FabricApi)).IsSuccess);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.SetLoader, Loader: InstallLoader.Fabric)).IsSuccess);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.AddAddon, Loader: InstallLoader.FabricApi)).IsSuccess);
        var before = store.Read<MinecraftInstallDraftSnapshot>(store.Resolve(MinecraftInstallDraftContract.StateKey)).Value!;
        AssertEqual(1, before.Addons.Count);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.SetLoader, Loader: InstallLoader.Quilt)).IsSuccess);
        var after = store.Read<MinecraftInstallDraftSnapshot>(store.Resolve(MinecraftInstallDraftContract.StateKey)).Value!;
        AssertEqual(0, after.Addons.Count);
        AssertEqual(1, before.Addons.Count);
        AssertTrue(drafts.Apply(new(InstallDraftChangeKind.Reset)).IsSuccess);
        AssertTrue(!store.Read<MinecraftInstallDraftSnapshot>(store.Resolve(MinecraftInstallDraftContract.StateKey)).Value!.Chosen);
    }
    private sealed class ControlledJavaProbe(string root) : Nexa.Services.Minecraft.Java.IJavaRuntimeLocator
    {
        public int Major { get; set; } = 17;
        public bool Delay { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<Nexa.Services.Minecraft.Java.JavaRuntimeCandidate>> FindAllAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Only the inspection port is used.");
        public async ValueTask<Nexa.Services.Minecraft.Java.JavaRuntimeCandidate?> InspectAsync(string javaExecutablePath, CancellationToken cancellationToken = default)
        {
            if (!javaExecutablePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            int major = Major;
            if (Delay) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return new(new Nexa.Services.Minecraft.Java.JavaInstallation(Path.GetDirectoryName(Path.GetDirectoryName(javaExecutablePath))!,
                javaExecutablePath, null, new Version(major, 0), Nexa.Services.Minecraft.Java.JavaBrand.Unknown,
                Nexa.Services.Minecraft.Java.JavaArchitecture.X64, true, false));
        }
    }
    private static async ValueTask JavaCacheIsScopedAndInvalidationRejectsOldScan()
    {
        string directory = CreateTempDirectory();
        try
        {
            string one = Path.Combine(directory, "one"), two = Path.Combine(directory, "two");
            foreach (string root in new[] { one, two })
            {
                Directory.CreateDirectory(Path.Combine(root, "bin"));
                File.WriteAllText(Path.Combine(root, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"), "fixture");
            }
            ControlledJavaProbe probe = new(directory);
            Nexa.Services.Minecraft.Java.LocalJavaRuntimeLocator first = new(one, inspectionPort: probe);
            Nexa.Services.Minecraft.Java.LocalJavaRuntimeLocator second = new(two, inspectionPort: probe);
            var a = await first.FindAllAsync(); var b = await second.FindAllAsync();
            AssertEqual(one, a.Single().Installation.JavaHome);
            AssertEqual(two, b.Single().Installation.JavaHome);
            probe.Delay = true;
            first.Invalidate();
            Task<IReadOnlyList<Nexa.Services.Minecraft.Java.JavaRuntimeCandidate>> old = first.FindAllAsync().AsTask();
            await probe.Started.Task;
            first.Invalidate(); probe.Major = 21; probe.Delay = false; probe.Release.SetResult();
            AssertEqual(17, (await old).Single().Installation.MajorVersion);
            AssertEqual(21, (await first.FindAllAsync()).Single().Installation.MajorVersion);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

}
