using System.Text.Json.Nodes;
using Nexa.Services.Caching;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Scheduling;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstallCatalogInformationReleasesNetworkBeforeNormalizationAndPersistence()
    {
        string directory = CreateTempDirectory();
        try
        {
            using var inner = new WorkScheduler(new(1, 1, 1));
            var scheduler = new CatalogInformationExclusiveScheduler(inner);
            var source = new CatalogInformationSource { Result = Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("0.16.0", "Fabric")]) };
            XsrStateStore store = CatalogInformationStore();
            using var service = new InstallCatalogService(store, source, null, () => "official-v1|CN",
                new InstallCatalogInformationCache(directory))
            { WorkScheduler = scheduler };
            await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default).WaitAsync(TimeSpan.FromSeconds(5));
            var snapshot = CatalogInformationSnapshot(store);
            AssertTrue(snapshot.Error is null);
            AssertEqual("0.16.0", snapshot.Versions.Single().Id);
            AssertTrue(scheduler.Admissions.SequenceEqual(new[] { WorkResource.Disk, WorkResource.Http, WorkResource.Cpu, WorkResource.Disk }));
            AssertTrue(inner.Snapshot.Resources.All(resource => resource.Active == 0 && resource.Waiting == 0));
            AssertEqual(1, Directory.EnumerateFiles(directory, "*.json").Count());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask InstallCatalogInformationSurvivesRestartAndSeparatesPolicy()
    {
        string directory = CreateTempDirectory();
        try
        {
            var time = new CatalogInformationClock();
            var disk = new InstallCatalogInformationCache(directory, time);
            using (var cache = new SharedStateCache(timeProvider: time))
            {
                var source = new CatalogInformationSource { Result = Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("0.16.0", "Fabric")]) };
                using var service = new InstallCatalogService(CatalogInformationStore(), source, cache, () => "official-v1|CN", disk);
                await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default);
                AssertEqual(1, source.Calls);
            }
            using (var cache = new SharedStateCache(timeProvider: time))
            {
                var source = new CatalogInformationSource { Result = Task.FromException<IReadOnlyList<InstallCatalogVersion>>(new HttpRequestException("Offline")) };
                XsrStateStore store = CatalogInformationStore();
                using var service = new InstallCatalogService(store, source, cache, () => "official-v1|CN", disk);
                await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default);
                AssertEqual(0, source.Calls);
                var restored = CatalogInformationSnapshot(store);
                AssertEqual("0.16.0", restored.Versions.Single().Id);
                AssertTrue(restored.CacheHit == true && !restored.IsStale && restored.Error is null);
            }
            using (var cache = new SharedStateCache(timeProvider: time))
            {
                var source = new CatalogInformationSource { Result = Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("0.17.0", "Fabric")]) };
                XsrStateStore store = CatalogInformationStore();
                using var service = new InstallCatalogService(store, source, cache, () => "official-v1|US", disk);
                await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default);
                AssertEqual(1, source.Calls);
                AssertEqual("0.17.0", CatalogInformationSnapshot(store).Versions.Single().Id);
                AssertEqual(2, Directory.EnumerateFiles(directory, "*.json").Count());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask InstallCatalogInformationPublishesStaleAndRenewsWithoutBlocking()
    {
        string directory = CreateTempDirectory();
        try
        {
            var time = new CatalogInformationClock();
            var disk = new InstallCatalogInformationCache(directory, time);
            await disk.SaveAsync(CatalogInformationKey(), [new("0.16.0", "Fabric")], default);
            time.Advance(TimeSpan.FromMinutes(20));
            using var cache = new SharedStateCache(timeProvider: time);
            var response = new TaskCompletionSource<IReadOnlyList<InstallCatalogVersion>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new CatalogInformationSource { Result = response.Task };
            XsrStateStore store = CatalogInformationStore();
            using var service = new InstallCatalogService(store, source, cache, () => "official-v1|CN", disk);
            await service.ReadAsync(new("1.20.1", InstallLoader.Fabric), default).WaitAsync(TimeSpan.FromSeconds(5));
            var stale = CatalogInformationSnapshot(store);
            AssertTrue(stale.IsStale && stale.CacheHit == true && !stale.Loading);
            AssertTrue(stale.Error?.Contains("缓存", StringComparison.Ordinal) == true);
            AssertEqual("0.16.0", stale.Versions.Single().Id);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertTrue(!response.Task.IsCompleted);
            response.SetException(new HttpRequestException("Offline"));
            for (int attempt = 0; attempt < 100 && CatalogInformationSnapshot(store).Error?.Contains("失败", StringComparison.Ordinal) != true; attempt++)
                await Task.Delay(10);
            var failed = CatalogInformationSnapshot(store);
            AssertTrue(failed.IsStale && failed.Error?.Contains("失败", StringComparison.Ordinal) == true);
            AssertEqual("0.16.0", failed.Versions.Single().Id);

            source.Result = Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([new("0.17.0", "Fabric")]);
            await service.ReadAsync(new("1.20.1", InstallLoader.Fabric, Refresh: true), default);
            var renewed = CatalogInformationSnapshot(store);
            AssertTrue(!renewed.IsStale && renewed.Error is null);
            AssertEqual("0.17.0", renewed.Versions.Single().Id);
            AssertEqual("0.17.0", (await disk.ReadAsync(CatalogInformationKey(), default))!.Value.Single().Id);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async ValueTask InstallCatalogInformationRejectsDamageSecretsLinksAndEnforcesDiskBudget()
    {
        string directory = CreateTempDirectory(), outside = CreateTempDirectory();
        try
        {
            var time = new CatalogInformationClock();
            var disk = new InstallCatalogInformationCache(directory, time);
            StateCacheKey key = CatalogInformationKey();
            await disk.SaveAsync(key, [new("0.16.0", "Fabric")], default);
            string path = Directory.EnumerateFiles(directory, "*.json").Single();
            string valid = await File.ReadAllTextAsync(path);
            await disk.SaveAsync(key, [], default);
            await disk.SaveAsync(key, [new("0.17.0", "Fabric", Warning: "Provider failure")], default);
            await disk.SaveAsync(key, [new("0.18.0", "Fabric", Downloads: [new("Modrinth", "addon.jar",
                new Uri("https://cdn.example.test/addon.jar?token=private"), new string('a', 40), 1)])], default);
            AssertEqual(valid, await File.ReadAllTextAsync(path));
            AssertTrue(!valid.Contains("private", StringComparison.Ordinal));
            foreach (string damaged in new[]
            {
                "{broken", valid.Replace("0.16.0", "../outside", StringComparison.Ordinal),
                valid.Replace("1.20.1|Fabric", "1.20.1|UnknownLoader", StringComparison.Ordinal),
                valid.Replace("official-v1|CN", "other-policy", StringComparison.Ordinal),
                valid.Replace("\"Schema\":1", "\"Schema\":999", StringComparison.Ordinal),
                new string('x', 1024 * 1024 + 1)
            })
            {
                await File.WriteAllTextAsync(path, damaged);
                AssertTrue(await disk.ReadAsync(key, default) is null);
            }
            var future = JsonNode.Parse(valid)!.AsObject();
            future["StoredAt"] = time.GetUtcNow().AddDays(1).ToString("O");
            await File.WriteAllTextAsync(path, future.ToJsonString());
            AssertTrue(await disk.ReadAsync(key, default) is null);
            await File.WriteAllTextAsync(path, valid);
            time.Advance(TimeSpan.FromDays(8));
            AssertTrue(await disk.ReadAsync(key, default) is null);

            File.Delete(path);
            string external = Path.Combine(outside, "record.json");
            await File.WriteAllTextAsync(external, valid);
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(path, external);
                AssertTrue(await disk.ReadAsync(key, default) is null);
                await disk.SaveAsync(key, [new("0.19.0", "Fabric")], default);
                AssertEqual(valid, await File.ReadAllTextAsync(external));
                File.Delete(path);
                string linkedDirectory = Path.Combine(directory, "linked");
                Directory.CreateSymbolicLink(linkedDirectory, outside);
                var linked = new InstallCatalogInformationCache(linkedDirectory, time);
                await linked.SaveAsync(key, [new("0.19.0", "Fabric")], default);
                AssertEqual(1, Directory.EnumerateFiles(outside).Count());
                Directory.Delete(linkedDirectory);
            }

            IReadOnlyList<InstallCatalogVersion> large = Array.AsReadOnly(Enumerable.Range(0, 1024)
                .Select(index => new InstallCatalogVersion("build-" + index, new string('x', 512))).ToArray());
            for (int index = 0; index < 36; index++)
                await disk.SaveAsync(CatalogInformationKey("1.20.1-" + index), large, default);
            var files = Directory.EnumerateFiles(directory, "*.json").Select(file => new FileInfo(file)).ToArray();
            AssertTrue(files.Length is > 0 and < 32);
            AssertTrue(files.Sum(file => file.Length) <= 16L * 1024 * 1024);
            AssertTrue(files.All(file => file.Length <= 1024 * 1024));
            AssertEqual(0, Directory.EnumerateFiles(directory, "*.tmp").Count());
        }
        finally { Directory.Delete(directory, true); Directory.Delete(outside, true); }
    }

    private static StateCacheKey CatalogInformationKey(string game = "1.20.1") => new("minecraft.install-catalog", game + "|Fabric", "official-v1|CN");
    private static XsrStateStore CatalogInformationStore()
    {
        XsrStateStoreBuilder builder = new(); InstallCatalogStateContract.DeclareState(builder); return builder.Build();
    }
    private static InstallCatalogSnapshot CatalogInformationSnapshot(XsrStateStore store) =>
        ((InstallCatalogState)store.ReadAppliedValue(store.Resolve(InstallCatalogStateContract.StateKey))!).Catalogs.Single(catalog => catalog.Loader == InstallLoader.Fabric);
    private sealed class CatalogInformationClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class CatalogInformationSource : IInstallCatalogSource
    {
        public Task<IReadOnlyList<InstallCatalogVersion>> Result { get; set; } = Task.FromResult<IReadOnlyList<InstallCatalogVersion>>([]);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public Task<IReadOnlyList<InstallCatalogVersion>> GetGamesAsync(CancellationToken token) => GetLoadersAsync(InstallLoader.Fabric, "", token);
        public Task<IReadOnlyList<InstallCatalogVersion>> GetLoadersAsync(InstallLoader loader, string game, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref _calls); Started.TrySetResult(); return Result; }
    }
    private sealed class CatalogInformationExclusiveScheduler(IWorkScheduler inner) : IWorkScheduler
    {
        private int _active;
        public List<WorkResource> Admissions { get; } = [];
        public WorkPriority CurrentPriority => inner.CurrentPriority;
        public IDisposable UsePriority(WorkPriority priority) => inner.UsePriority(priority);
        public IWorkQuietLease EnterQuiet() => inner.EnterQuiet();
        public async ValueTask<IDisposable> AcquireAsync(WorkPriority priority, WorkResource resource, CancellationToken token = default)
        {
            if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
                throw new InvalidOperationException("A one-slot worker cannot acquire disk/CPU while retaining HTTP.");
            try
            {
                IDisposable lease = await inner.AcquireAsync(priority, resource, token);
                Admissions.Add(resource);
                return new CatalogInformationResourceLease(() => { lease.Dispose(); Volatile.Write(ref _active, 0); });
            }
            catch { Volatile.Write(ref _active, 0); throw; }
        }
    }
    private sealed class CatalogInformationResourceLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
