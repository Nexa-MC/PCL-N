using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Nexa.Services.Accounts;
using Nexa.Services.Caching;
using Nexa.Services.Capabilities;
using Nexa.Services.Foundation;
using Nexa.Services.Logging;
using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Java;
using Nexa.Services.Minecraft.Launch;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Settings;
using Nexa.Xsr;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    internal static async ValueTask DiscoverySnapshotDirectoryLeaseKeepsConcurrentBudgetsAndForeignFiles()
    {
        string workspace = CreateTempDirectory(); string directory = Path.Combine(workspace, "cache");
        try
        {
            MinecraftDiscoverySnapshotStore first = new(directory), second = new(directory);
            string identity = new('A', 64);
            for (int index = 0; index < 31; index++)
            {
                string root = Path.Combine(workspace, "seed-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await first.SaveAsync(root, identity, [LibraryDescriptor(root, "local")], default);
            }
            string[] foreign = [Path.Combine(directory, "user.json"), Path.Combine(directory, new string('a', 64) + ".json"),
                Path.Combine(directory, new string('B', 64) + ".JSON")];
            foreach (string path in foreign)
            {
                File.WriteAllText(path, "{\"owner\":\"user\"}");
                File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
            string rootA = Path.Combine(workspace, "a"), rootB = Path.Combine(workspace, "b");
            Task writeA, writeB;
            using (FileStream heldLease = new(Path.Combine(directory, ".minecraft-discovery.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                writeA = first.SaveAsync(rootA, identity, [LibraryDescriptor(rootA, "local")], default).AsTask();
                writeB = second.SaveAsync(rootB, identity, [LibraryDescriptor(rootB, "local")], default).AsTask();
                AssertFalse(writeA.IsCompleted); AssertFalse(writeB.IsCompleted);
                AssertEqual(31, OwnedFiles().Length);
                AssertFalse(Directory.EnumerateFiles(directory, "*.tmp").Any());
            }
            await Task.WhenAll(writeA, writeB).WaitAsync(TimeSpan.FromSeconds(10));
            AssertEqual(32, OwnedFiles().Length);
            AssertTrue(await first.LoadAsync(rootA, default) is not null);
            AssertTrue(await second.LoadAsync(rootB, default) is not null);

            // Valid existing documents padded with JSON whitespace fill the 32 MiB budget.
            byte[] padded = new byte[1024 * 1024];
            foreach (FileInfo file in OwnedFiles())
            {
                Array.Fill(padded, (byte)' ');
                File.ReadAllBytes(file.FullName).CopyTo(padded, 0);
                File.WriteAllBytes(file.FullName, padded);
            }
            string rootC = Path.Combine(workspace, "c"), rootD = Path.Combine(workspace, "d");
            var largeC = LargeSnapshot(rootC); var largeD = LargeSnapshot(rootD);
            await Task.WhenAll(first.SaveAsync(rootC, identity, largeC, default).AsTask(),
                second.SaveAsync(rootD, identity, largeD, default).AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
            var retained = OwnedFiles();
            AssertTrue(retained.Length <= 32);
            AssertTrue(retained.Sum(file => file.Length) <= 32L * 1024 * 1024);
            AssertEqual(32, (await first.LoadAsync(rootC, default))!.Instances.Length);
            AssertEqual(32, (await second.LoadAsync(rootD, default))!.Instances.Length);
            foreach (string path in foreign) AssertEqual("{\"owner\":\"user\"}", File.ReadAllText(path));
            AssertFalse(Directory.EnumerateFiles(directory, "*.tmp").Any());

            FileInfo[] OwnedFiles() => Directory.EnumerateFiles(directory, "*.json")
                .Where(path => Path.GetFileName(path) is { Length: 69 } name && name.EndsWith(".json", StringComparison.Ordinal)
                    && name[..64].All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F'))
                .Select(path => new FileInfo(path)).ToArray();
            static MinecraftInstanceDescriptor[] LargeSnapshot(string root) => Enumerable.Range(0, 32)
                .Select(index => LibraryDescriptor(root, "local-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    with
                { Metadata = new() { DisplayName = new string('x', 32768) } }).ToArray();
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    internal static async ValueTask DiscoverySnapshotReusesCatalogAcrossRestartWithFreshMetadata()
    {
        string workspace = CreateTempDirectory(); string root = Path.Combine(workspace, "game"); string snapshots = Path.Combine(workspace, "cache");
        try
        {
            string versionDirectory = Path.Combine(root, "versions", "local"); Directory.CreateDirectory(versionDirectory);
            File.WriteAllText(Path.Combine(versionDirectory, "local.json"), "{\"id\":\"local\",\"type\":\"release\",\"mainClass\":\"example.Main\"}");
            MinecraftInstanceMetadataStore metadata = new();
            await metadata.SaveAsync(versionDirectory, new() { DisplayName = "My game", Tags = ["survival"], PreLaunchCommand = "private-launch-command", AuthServerAddress = "https://private-auth.invalid/", InstanceIsolation = false });
            using (SharedStateCache firstCache = new())
            {
                MinecraftInstanceDiscovery first = new(firstCache, snapshots);
                AssertEqual(1, (await first.DiscoverAsync(root)).Count);
            }
            string persisted = File.ReadAllText(Directory.GetFiles(snapshots, "*.json").Single());
            AssertFalse(persisted.Contains("private-launch-command", StringComparison.Ordinal)); AssertFalse(persisted.Contains("private-auth", StringComparison.Ordinal));
            using SharedStateCache restoredCache = new(); using LogService log = CreateLogService(); log.MaximumLevel = LogLevel.Debug;
            MinecraftInstanceDiscovery restored = new(restoredCache, snapshots, versionDiscovery: new MinecraftVersionDiscovery(log));
            MinecraftInstanceDiscoverySnapshot? pending = await restored.LoadSnapshotAsync(root);
            AssertTrue(pending is not null); AssertEqual("My game", pending!.Instances[0].Metadata.DisplayName);
            AssertEqual("", pending.Instances[0].Metadata.PreLaunchCommand);
            var reconciled = await restored.DiscoverAsync(root);
            AssertEqual("private-launch-command", reconciled[0].Metadata.PreLaunchCommand); AssertFalse(reconciled[0].Metadata.InstanceIsolation);
            AssertEqual(0, log.GetSnapshot().Count(entry => entry.Module == "VersionScan"));
            await metadata.UpdateAsync(versionDirectory, value => value with { DisplayName = "Changed", PreLaunchCommand = "live-command" });
            AssertEqual("live-command", (await restored.DiscoverAsync(root))[0].Metadata.PreLaunchCommand);
            AssertEqual("Changed", (await restored.LoadSnapshotAsync(root))!.Instances[0].Metadata.DisplayName);
            AssertEqual(0, log.GetSnapshot().Count(entry => entry.Module == "VersionScan"));
            await restored.RefreshAsync(root); AssertEqual(1, log.GetSnapshot().Count(entry => entry.Module == "VersionScan"));
            Directory.Move(root, root + ".offline");
            AssertEqual("Changed", (await restored.LoadSnapshotAsync(root))!.Instances[0].Metadata.DisplayName);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    internal static async ValueTask DiscoverySnapshotRejectsSameStampEditsAndMalformedHints()
    {
        string workspace = CreateTempDirectory(); string root = Path.Combine(workspace, "game"); string snapshots = Path.Combine(workspace, "cache");
        try
        {
            string versionDirectory = Path.Combine(root, "versions", "local"); Directory.CreateDirectory(versionDirectory);
            string manifest = Path.Combine(versionDirectory, "local.json");
            File.WriteAllText(manifest, "{\"id\":\"local\",\"type\":\"release\",\"mainClass\":\"a.Main\"}");
            using SharedStateCache cache = new(); MinecraftInstanceDiscovery source = new(cache, snapshots);
            AssertEqual("a.Main", (await source.DiscoverAsync(root))[0].Version.MainClass);
            DateTime stamp = File.GetLastWriteTimeUtc(manifest);
            File.WriteAllText(manifest, "{\"id\":\"local\",\"type\":\"release\",\"mainClass\":\"b.Main\"}"); File.SetLastWriteTimeUtc(manifest, stamp);
            AssertEqual("b.Main", (await source.DiscoverAsync(root))[0].Version.MainClass);
            string snapshotPath = Directory.GetFiles(snapshots, "*.json").Single(); File.WriteAllText(snapshotPath, "{\"schemaVersion\":99}");
            AssertTrue(await source.LoadSnapshotAsync(root) is null);
            using SharedStateCache restartCache = new(); MinecraftInstanceDiscovery restarted = new(restartCache, snapshots);
            AssertEqual("b.Main", (await restarted.DiscoverAsync(root))[0].Version.MainClass);
            Directory.Delete(versionDirectory, recursive: true); AssertEqual(0, (await restarted.DiscoverAsync(root)).Count);
            AssertEqual(0, (await restarted.LoadSnapshotAsync(root))!.Instances.Count);
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    internal static async ValueTask PrimaryInstanceQueriesReadAuthoritativeMetadataAfterEdits()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "local"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "local.json"), "{\"id\":\"local\",\"type\":\"release\",\"mainClass\":\"example.Main\"}");
            MinecraftInstanceMetadataStore store = new(); await store.SaveAsync(instance, new() { InstanceIsolation = true, PreLaunchCommand = "first" });
            using SharedStateCache cache = new(); var query = new MachineCapabilityQuery(instance, "local", root);
            var first = await MinecraftPrimaryInstanceScope.ResolveAsync(root, query, cache, default);
            AssertEqual(instance, first!.GameDirectory);
            await store.UpdateAsync(instance, metadata => metadata with { InstanceIsolation = false, PreLaunchCommand = "changed" });
            var second = await MinecraftPrimaryInstanceScope.ResolveAsync(root, query, cache, default);
            AssertEqual(root, second!.GameDirectory); AssertEqual("changed", second.Instance.Metadata.PreLaunchCommand);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static async ValueTask LaunchPreparationBypassesSelfConsistentPersistedCatalogHints()
    {
        string workspace = CreateTempDirectory(); string root = Path.Combine(workspace, "game"); string snapshots = Path.Combine(workspace, "cache");
        try
        {
            string instance = Path.Combine(root, "versions", "local"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "local.json"), "{\"id\":\"1.20.1\",\"type\":\"release\",\"mainClass\":\"example.RealMain\",\"javaVersion\":{\"majorVersion\":17}}");
            using (SharedStateCache firstCache = new()) await new MinecraftInstanceDiscovery(firstCache, snapshots).DiscoverAsync(root);
            string path = Directory.GetFiles(snapshots, "*.json").Single();
            var document = JsonSerializer.Deserialize(File.ReadAllBytes(path), MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheDocument)!;
            var old = document.Instances[0];
            var changed = new[] { old with { VersionId = "cache-only-version", Version = old.Version with { Id = "cache-only-version", MainClass = "cache.Main" } } };
            string checksum = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changed,
                MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheInstanceArray)));
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(document with { Instances = changed, PayloadSha256 = checksum },
                MinecraftDiscoveryCacheJsonContext.Default.MinecraftDiscoveryCacheDocument));
            using SharedStateCache cache = new(); MinecraftInstanceDiscovery discovery = new(cache, snapshots);
            AssertEqual("cache-only-version", (await discovery.DiscoverAsync(root))[0].VersionId);
            string javaHome = Path.Combine(workspace, "java"), java = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            Directory.CreateDirectory(Path.GetDirectoryName(java)!); File.WriteAllBytes(java, [0]);
            JavaRuntimeCandidate runtime = new(new JavaInstallation(javaHome, java, null, new Version(17, 0, 10),
                JavaBrand.EclipseTemurin, JavaArchitecture.X64, is64Bit: true, isJre: false));
            using FoundationHost host = FoundationComposer.Compose(new InMemorySettingsPort(), LauncherDefaults.CreateSchema(),
                new LaunchProfileFilePort(Path.Combine(workspace, "profiles.json")));
            AssertTrue(host.Accounts.AddProfile(new() { Username = "Player", Uuid = "player-uuid", Kind = LaunchProfileKind.Offline }).IsSuccess);
            MinecraftLaunchCoordinator coordinator = new(root, Path.Combine(root, "runtime"), discovery, host.Accounts, host.Settings,
                new JavaSelectionService(new InMemoryJavaLocator([runtime])), new NeverJavaInstaller(),
                new MinecraftLaunchExecutor(new MinecraftProcessService(hostStore: host.StateStore)), settingsPolicy: host.SettingsPolicy);
            var prepared = await coordinator.PrepareAsync("local", 0);
            AssertTrue(prepared.IsSuccess); AssertEqual("1.20.1", prepared.Value.Request.VersionId);
            AssertEqual("example.RealMain", prepared.Value.Request.VersionJson["mainClass"]!.ToString());
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }

    internal static async ValueTask DiscoveryRefreshDoesNotJoinPersistedHintFlights()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = Path.Combine(root, "versions", "local"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "local.json"), "{\"id\":\"local\",\"type\":\"release\",\"mainClass\":\"live.Main\"}");
            using SharedStateCache inner = new(); var cache = new DiscoveryHintFlightGate(inner);
            MinecraftInstanceDiscovery discovery = new(cache);
            Task<IReadOnlyList<MinecraftInstanceDescriptor>> hints = discovery.DiscoverAsync(root).AsTask();
            await cache.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                var fresh = await discovery.RefreshAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                AssertEqual("live.Main", fresh[0].Version.MainClass); AssertFalse(hints.IsCompleted);
            }
            finally { cache.Release.TrySetResult(); }
            AssertEqual("live.Main", (await hints)[0].Version.MainClass);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class DiscoveryHintFlightGate(ISharedStateCache inner) : ISharedStateCache
    {
        private int _blocks = 1;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryGet<T>(StateCacheKey key, [NotNullWhen(true)] out StateCacheSnapshot<T>? snapshot, bool allowStale = false) =>
            inner.TryGet(key, out snapshot, allowStale);
        public ValueTask<T> GetOrCreateAsync<T>(StateCacheKey key, StateCachePolicy policy, Func<CancellationToken, ValueTask<T>> factory,
            bool refresh = false, Func<T, bool>? shouldStore = null, CancellationToken cancellationToken = default) =>
            inner.GetOrCreateAsync<T>(key, policy, key.Scope == "minecraft.installed-catalog" && Interlocked.Exchange(ref _blocks, 0) == 1
                ? token => BlockedAsync(factory, token) : factory, refresh, shouldStore, cancellationToken);
        public void Store<T>(StateCacheKey key, T value, StateCachePolicy policy, DateTimeOffset? storedAt = null) => inner.Store(key, value, policy, storedAt);
        public void Invalidate(StateCacheKey key) => inner.Invalidate(key);
        public void InvalidateScope(string scope) => inner.InvalidateScope(scope);

        private async ValueTask<T> BlockedAsync<T>(Func<CancellationToken, ValueTask<T>> factory, CancellationToken token)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(token).ConfigureAwait(false);
            return await factory(token).ConfigureAwait(false);
        }
    }
}
