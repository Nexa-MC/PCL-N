using Nexa.Services.Caching;
using Nexa.Services.Scheduling;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Install;

/// <summary>Pure legacy availability gates. Catalog metadata makes the final support decision.</summary>
public static partial class InstallCompatibility
{
    public static string? UnavailableReason(InstallLoader loader, string game)
    {
        string numeric = game.Split('-', 2)[0];
        string[] parts = numeric.Split('.');
        bool parsed = parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _);
        int major = parsed ? int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
        int minor = parsed ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        // Unknown snapshots are resolved by metadata, rather than declared incompatible.
        int drop = major == 1 ? minor * 10 : major >= 25 ? major * 10 + minor : 0;
        bool allowed = loader switch
        {
            InstallLoader.Cleanroom => game.Equals("1.12.2", StringComparison.OrdinalIgnoreCase),
            InstallLoader.Forge => game.StartsWith("1.", StringComparison.Ordinal) || major > 25,
            InstallLoader.LiteLoader => drop < 130,
            InstallLoader.NeoForge => drop == 0 || drop >= 200,
            InstallLoader.Fabric => drop == 0 || drop > 130,
            InstallLoader.LegacyFabric => drop <= 130,
            InstallLoader.Quilt => drop == 0 || drop >= 140,
            InstallLoader.LabyMod => drop == 0 || drop >= 80,
            _ => true,
        };
        if (loader == InstallLoader.Quilt && numeric.StartsWith("1.14.", StringComparison.Ordinal)
            && Version.TryParse(numeric, out Version? quiltGame)) allowed = quiltGame >= new Version(1, 14, 4);
        return allowed ? null : $"{loader} 不支持 Minecraft {game}。";
    }
    public static bool IsAddon(InstallLoader loader) => loader is InstallLoader.FabricApi or InstallLoader.Qsl or InstallLoader.OptiFabric;
    public static bool CanCombine(InstallLoader first, InstallLoader second, string game) => first == second
        || first == InstallLoader.Fabric && second is InstallLoader.OptiFine or InstallLoader.FabricApi or InstallLoader.OptiFabric
        || second == InstallLoader.Fabric && first is InstallLoader.OptiFine or InstallLoader.FabricApi or InstallLoader.OptiFabric
        || first == InstallLoader.OptiFine && second is InstallLoader.OptiFabric or InstallLoader.FabricApi
        || second == InstallLoader.OptiFine && first is InstallLoader.OptiFabric or InstallLoader.FabricApi
        || first == InstallLoader.FabricApi && second == InstallLoader.OptiFabric
        || second == InstallLoader.FabricApi && first == InstallLoader.OptiFabric
        || first == InstallLoader.Quilt && second == InstallLoader.Qsl || second == InstallLoader.Quilt && first == InstallLoader.Qsl
        || first == InstallLoader.OptiFine && CanCombineWithOptiFine(second, game)
        || second == InstallLoader.OptiFine && CanCombineWithOptiFine(first, game);
    public static bool CanCombineWithOptiFine(InstallLoader loader, string game) =>
        loader == InstallLoader.Cleanroom && game == "1.12.2" || loader == InstallLoader.LiteLoader || loader == InstallLoader.Forge
        && (!Version.TryParse(game.Split('-', 2)[0], out Version? version)
            || version < new Version(1, 13) || version > new Version(1, 14, 3));
}

/// <summary>Background per-catalog acquisition; immutable aggregate publications never drop sibling results.</summary>
public sealed partial class InstallCatalogService : IDisposable
{
    public IWorkScheduler? WorkScheduler { get; init; }
    private readonly XsrStateStore _store;
    private readonly XsrStateId _state;
    private readonly IInstallCatalogSource _source;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly ISharedStateCache _cache;
    private readonly InstallCatalogInformationCache? _informationCache;
    private readonly bool _ownsCache;
    private readonly Func<string> _sourcePolicyIdentity;
    private readonly Dictionary<InstallLoader, InstallCatalogSnapshot> _loaders = [];
    private readonly Dictionary<(string, InstallLoader?, string), Request> _pending = [];
    private InstallCatalogSnapshot _games = new(0, "", null, [], false);
    private string _game = "";
    private long _revision;
    private bool _disposed;
    private sealed class Request(CancellationTokenSource cancellation, WorkPriority priority)
    {
        private int _priority = (int)priority;
        private readonly object _priorityGate = new();
        public WorkPriority Priority => (WorkPriority)Volatile.Read(ref _priority);
        public TaskCompletionSource Promoted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Promote(WorkPriority priority)
        {
            lock (_priorityGate)
            {
                if ((int)priority >= _priority) return;
                Volatile.Write(ref _priority, (int)priority);
                Promoted.TrySetResult();
            }
        }
        public void BeginBackgroundRenewal()
        {
            lock (_priorityGate)
                if (!Promoted.Task.IsCompleted) Volatile.Write(ref _priority, (int)WorkPriority.Background);
        }
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<XsrResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public InstallCatalogService(XsrStateStore store, IInstallCatalogSource source)
        : this(store, source, null, null, null) { }
    public InstallCatalogService(XsrStateStore store, IInstallCatalogSource source,
        ISharedStateCache? cache, Func<string>? sourcePolicyIdentity = null,
        InstallCatalogInformationCache? informationCache = null)
    {
        _store = store; _source = source; _state = store.Resolve(InstallCatalogStateContract.StateKey);
        _ownsCache = cache is null;
        _cache = cache ?? new SharedStateCache();
        _informationCache = informationCache;
        string isolatedSource = Guid.NewGuid().ToString("N");
        _sourcePolicyIdentity = sourcePolicyIdentity ?? (() => isolatedSource);
        Publish(_games);
    }
    private void SelectGame(string game)
    {
        if (_game == game) return;
        _game = game; _loaders.Clear();
        foreach (var key in _pending.Keys.Where(key => key.Item2 is not null).ToArray())
        {
            Request retired = _pending[key];
            _pending.Remove(key);
            retired.Cancellation.Cancel();
        }
    }
    public Task<XsrResult> PrefetchAsync(InstallCatalogPrefetchCommand command, CancellationToken token)
    {
        List<Task<XsrResult>> tasks = [];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SelectGame(command.GameVersion);
            Publish(_games);
            if (command.GameVersion.Length > 0)
                foreach (InstallLoader loader in Enum.GetValues<InstallLoader>())
                {
                    InstallLoader baseLoader = loader switch { InstallLoader.FabricApi or InstallLoader.OptiFabric => InstallLoader.Fabric, InstallLoader.Qsl => InstallLoader.Quilt, _ => loader };
                    if (InstallCompatibility.UnavailableReason(baseLoader, command.GameVersion) is null)
                        tasks.Add(ReadAsync(new(command.GameVersion, loader), WorkPriority.Background, token));
                }
        }
        return CompleteAll(tasks);
    }
    private static async Task<XsrResult> CompleteAll(List<Task<XsrResult>> tasks)
    {
        await Task.WhenAll(tasks).ConfigureAwait(false); return XsrResult.Success();
    }
    public Task<XsrResult> ReadAsync(InstallCatalogReadCommand command, CancellationToken token)
        => ReadAsync(command, WorkPriority.Interactive, token);
    private Task<XsrResult> ReadAsync(InstallCatalogReadCommand command, WorkPriority priority, CancellationToken token)
    {
        string game = command.Loader is null ? "" : command.GameVersion.Trim();
        var key = (game, command.Loader, _sourcePolicyIdentity());
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (command.Loader is not null) SelectGame(game);
            if (token.IsCancellationRequested)
            {
                Publish(new(++_revision, game, command.Loader, [], false, Error: "请求已取消。"));
                return Task.FromResult(XsrResult.Success());
            }
            if (command.Loader is { } requested && (!Enum.IsDefined(requested)
                || game.Length > 0 && !MinecraftVersionPaths.IsSafeReference(game)))
            {
                Publish(new(++_revision, game, requested, [], false, Unsupported: "安装目录请求无效。"));
                return Task.FromResult(XsrResult.Success());
            }
            if (!command.Refresh && _pending.TryGetValue(key, out Request? pending))
            {
                pending.Promote(priority);
                return pending.Completion.Task.WaitAsync(token);
            }
            if (_pending.Remove(key, out Request? old)) { old.Promote(priority); old.Cancellation.Cancel(); }
            if (!command.Refresh && _cache.TryGet<IReadOnlyList<InstallCatalogVersion>>(CacheKey(key), out var cached))
            {
                Publish(new(++_revision, game, command.Loader, cached.Value, false) { CacheHit = true }); return Task.FromResult(XsrResult.Success());
            }
            if (command.Loader is { } loader && (game.Length == 0 || InstallCompatibility.UnavailableReason(loader, game) is not null))
            {
                Publish(new(++_revision, game, loader, [], false, Unsupported: game.Length == 0 ? "请先选择 Minecraft 版本。" : InstallCompatibility.UnavailableReason(loader, game)));
                return Task.FromResult(XsrResult.Success());
            }
            Request request = new(CancellationTokenSource.CreateLinkedTokenSource(token), priority); _pending[key] = request;
            Publish(new(++_revision, game, command.Loader, [], true));
            // Queue provider invocation too: even a synchronously completing HTTP/cache/parser cannot occupy the UI thread.
            _ = Task.Run(() => FetchAsync(key, request, command.Refresh), CancellationToken.None);
            return request.Completion.Task;
        }
    }
    private static StateCacheKey CacheKey((string Game, InstallLoader? Loader, string Policy) key)
        => new("minecraft.install-catalog", key.Game + "|" + key.Loader, key.Policy);
    private async Task FetchAsync((string Game, InstallLoader? Loader, string Policy) key, Request request, bool refresh)
    {
        CancellationToken token = request.Cancellation.Token;
        StateCacheSnapshot<IReadOnlyList<InstallCatalogVersion>>? retained = null;
        try
        {
            StateCacheKey cacheKey = CacheKey(key);
            if (!_cache.TryGet(cacheKey, out retained, allowStale: true) && _informationCache is not null)
            {
                using IDisposable? disk = WorkScheduler is null ? null
                    : await AdmitAsync(request, WorkResource.Disk, token).ConfigureAwait(false);
                var persisted = await _informationCache.ReadAsync(cacheKey, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (persisted is not null && !_cache.TryGet(cacheKey, out retained, allowStale: true))
                {
                    _cache.Store(cacheKey, persisted.Value, InstallCatalogInformationCache.Policy, persisted.StoredAt);
                    _cache.TryGet(cacheKey, out retained, allowStale: true);
                }
            }
            if (!refresh && retained is not null)
            {
                lock (_gate)
                {
                    if (!Current()) return;
                    Publish(new(++_revision, key.Game, key.Loader, retained.Value, false,
                        Error: retained.IsStale ? InstallCatalogInformationCache.StaleNotice : null)
                    { CacheHit = true, IsStale = retained.IsStale });
                }
                if (!retained.IsStale) return;
                // The display completes immediately; its generation remains pending until renewal finishes.
                request.Completion.TrySetResult(XsrResult.Success());
                request.BeginBackgroundRenewal();
            }
            int? inputCount = null;
            double? normalizeMs = null;
            IReadOnlyList<InstallCatalogVersion> versions = await _cache.GetOrCreateAsync<IReadOnlyList<InstallCatalogVersion>>(CacheKey(key),
                InstallCatalogInformationCache.Policy, async sharedToken =>
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(sharedToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(45));
                    IReadOnlyList<InstallCatalogVersion> result;
                    // The shared producer owns admission. Waiter cancellation never releases a
                    // resource underneath active HTTP, and HTTP never overlaps CPU/disk leases.
                    using (IDisposable? http = await AdmitAsync(request, WorkResource.Http, deadline.Token).ConfigureAwait(false))
                    {
                        await _concurrency.WaitAsync(deadline.Token).ConfigureAwait(false);
                        try
                        {
                            result = key.Loader is { } loader
                                ? await _source.GetLoadersAsync(loader, key.Game, deadline.Token).ConfigureAwait(false)
                                : await _source.GetGamesAsync(deadline.Token).ConfigureAwait(false);
                        }
                        finally { _concurrency.Release(); }
                    }
                    IReadOnlyList<InstallCatalogVersion> normalized;
                    using (IDisposable? cpu = await AdmitAsync(request, WorkResource.Cpu, deadline.Token).ConfigureAwait(false))
                    {
                        inputCount = result.Count;
                        long started = System.Diagnostics.Stopwatch.GetTimestamp();
                        normalized = InstallCatalogInformationCache.Normalize(result);
                        normalizeMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    }
                    if (_informationCache is not null)
                    {
                        try
                        {
                            using IDisposable? disk = await AdmitAsync(request, WorkResource.Disk, deadline.Token).ConfigureAwait(false);
                            await _informationCache.SaveAsync(CacheKey(key), normalized, deadline.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!sharedToken.IsCancellationRequested)
                        { /* Optional disk persistence cannot discard an already acquired catalog. */ }
                    }
                    return normalized;
                }, refresh: refresh, shouldStore: InstallCatalogInformationCache.Positive, cancellationToken: token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (Current())
                {
                    _cache.TryGet(CacheKey(key), out retained, allowStale: true);
                    if (retained is not null && !InstallCatalogInformationCache.Positive(versions))
                        Publish(new(++_revision, key.Game, key.Loader, retained.Value, false,
                            Error: "正在显示缓存版本资料，安装来源未返回完整资料，请重试。")
                        { CacheHit = true, IsStale = true });
                    else Publish(new(++_revision, key.Game, key.Loader, versions, false)
                    { CacheHit = false, InputCount = inputCount, NormalizeMilliseconds = normalizeMs });
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            lock (_gate) if (Current())
                {
                    // A retained record may expire while its bounded network renewal is running.
                    _cache.TryGet(CacheKey(key), out retained, allowStale: true);
                    Publish(new(++_revision, key.Game, key.Loader, retained?.Value ?? [], false,
                        Error: retained is not null ? "正在显示缓存版本资料，重新连接安装来源失败，请重试。"
                            : error is OperationCanceledException ? "请求已取消。" : error.Message)
                    { CacheHit = retained is not null, IsStale = retained is not null });
                }
        }
        finally
        {
            lock (_gate)
            {
                if (_pending.TryGetValue(key, out Request? current) && ReferenceEquals(current, request)) _pending.Remove(key);
                request.Cancellation.Dispose();
            }
            request.Completion.TrySetResult(XsrResult.Success());
        }
        bool Current() => !_disposed && _sourcePolicyIdentity() == key.Policy
            && (key.Loader is null || _game == key.Game)
            && _pending.TryGetValue(key, out Request? current) && ReferenceEquals(current, request);
    }
    private async ValueTask<IDisposable?> AdmitAsync(Request request, WorkResource resource, CancellationToken token)
    {
        if (WorkScheduler is null) return null;
        WorkPriority priority = request.Priority;
        if ((int)priority <= (int)WorkPriority.Interactive || request.Promoted.Task.IsCompleted)
            return await WorkScheduler.AcquireAsync(priority, resource, token).ConfigureAwait(false);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IDisposable> admission = WorkScheduler.AcquireAsync(priority, resource, waiting.Token).AsTask();
        if (await Task.WhenAny(admission, request.Promoted.Task).ConfigureAwait(false) == admission)
            return await admission.ConfigureAwait(false);
        // Promotion retires the queued background admission; a racing grant is disposed.
        await waiting.CancelAsync().ConfigureAwait(false);
        try { (await admission.ConfigureAwait(false)).Dispose(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        return await WorkScheduler.AcquireAsync(request.Priority, resource, token).ConfigureAwait(false);
    }
    private void Publish(InstallCatalogSnapshot snapshot)
    {
        if (snapshot.Loader is { } loader) _loaders[loader] = snapshot; else _games = snapshot;
        _store.Publish(_state, new InstallCatalogState(++_revision, _game,
            Array.AsReadOnly(new[] { _games }.Concat(_loaders.OrderBy(pair => pair.Key).Select(pair => pair.Value)).ToArray())));
    }
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (Request request in _pending.Values) request.Cancellation.Cancel();
            _pending.Clear();
            if (_ownsCache) ((IDisposable)_cache).Dispose();
        }
    }
}
