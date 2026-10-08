using Nexa.Core.Media;
using Nexa.Services.Caching;
using Nexa.Services.Scheduling;

namespace Nexa.Services.Resources;

public sealed class ResourceIconService : IDisposable
{
    private readonly HttpClient _http;
    private readonly ISharedStateCache _cache;
    private readonly bool _ownsCache;
    private static readonly StateCachePolicy IconPolicy = new(TimeSpan.FromDays(1), TimeSpan.FromDays(1), 1_048_576);
    private static readonly StateCachePolicy StandalonePolicy = new(TimeSpan.MaxValue, TimeSpan.MaxValue);
    public IWorkScheduler? WorkScheduler { get; init; }
    private readonly SemaphoreSlim _slots = new(4);
    private readonly object _gate = new();
    private bool _disposed;

    public ResourceIconService(HttpClient http) : this(http, null) { }

    public ResourceIconService(HttpClient http, ISharedStateCache? cache) : this(http, 32 * 1_048_576, 256, cache) { }

    internal ResourceIconService(HttpClient http, long byteBudget, int entryBudget) : this(http, byteBudget, entryBudget, null) { }

    internal ResourceIconService(HttpClient http, long byteBudget, int entryBudget, ISharedStateCache? cache)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryBudget);
        _http = http; _ownsCache = cache is null;
        _cache = cache ?? new SharedStateCache(entryBudget, byteBudget, maximumPending: Math.Max(64, entryBudget));
    }

    private bool Retain(StateCacheKey key, ResourceIconResult result)
    {
        if (result.Image is not { } image) return false;
        if (!_ownsCache) return true;
        lock (_gate)
        {
            if (!_disposed)
                _cache.Store(key, result, StandalonePolicy with { SizeBytes = image.Bytes.Length });
        }
        // Only the private owner uses exact encoded sizes. Application flights keep the cache's
        // guarded publication so invalidation cannot be undone by an older icon download.
        return false;
    }

    public static bool IsAllowed(string url) => url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (uri.Host == "cdn.modrinth.com" && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal) || uri.Host is "media.forgecdn.net" or "mediafilez.forgecdn.net" && uri.AbsolutePath.StartsWith("/avatars/", StringComparison.Ordinal));

    public async Task<ResourceIconResult> ReadAsync(ResourceIconQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsAllowed(query.Url)) return new(null);
        lock (_gate)
        {
            if (_disposed) return new(null);
        }
        StateCacheKey key = new("resources.icon", query.Url, "static-image-admission-v1");
        try
        {
            ResourceIconResult result = await _cache.GetOrCreateAsync(key, IconPolicy,
                ct => ReadCoreAsync(query, ct), shouldStore: value => Retain(key, value), cancellationToken: token).ConfigureAwait(false);
            lock (_gate) return _disposed ? new(null) : result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null); }
        catch (ObjectDisposedException) { return new(null); }
    }

    private async ValueTask<ResourceIconResult> ReadCoreAsync(ResourceIconQuery query, CancellationToken token)
    {
        IDisposable? admission = null;
        bool entered = false;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_ownsCache && _disposed) return new(null);
            }
            // All icon work is optional. Bound the entire encoded/decode pipeline locally,
            // before taking shared HTTP admission, so quiet CPU waits cannot retain HTTP slots.
            admission = WorkScheduler is null ? null
                : await WorkScheduler.AcquireAsync(WorkPriority.Background, WorkResource.Http, token).ConfigureAwait(false);
            using var output = new MemoryStream();
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, query.Url);
                request.Headers.UserAgent.ParseAdd("NexaCL/2.0 (https://github.com/PCL-N-Edition/PCL-N)");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1_048_576) return new(null);
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                byte[] buffer = new byte[16384];
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, 1_048_577 - output.Length)), token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > 1_048_576) return new(null);
                    output.Write(buffer, 0, read);
                }
            }
            admission?.Dispose(); admission = null;
            using IDisposable? decode = WorkScheduler is null ? null
                : await WorkScheduler.AcquireAsync(WorkPriority.Background, WorkResource.Cpu, token).ConfigureAwait(false);
            var image = PngImage.TryCreateResourceIcon(output.GetBuffer().AsSpan(0, (int)output.Length));
            token.ThrowIfCancellationRequested();
            return new(image);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException) { return new(null); }
        finally { admission?.Dispose(); if (entered) _slots.Release(); }
    }
    // The runtime cancels outstanding queries before disposal. Do not dispose a semaphore
    // while canceled HTTP operations are still unwinding and releasing their slots.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_ownsCache) ((IDisposable)_cache).Dispose();
    }
}
