using Nexa.Core.Media;
using Nexa.Services.Scheduling;

namespace Nexa.Services.Resources;

public sealed class ResourceIconService(HttpClient http) : IDisposable
{
    public IWorkScheduler? WorkScheduler { get; init; }
    private readonly SemaphoreSlim _slots = new(4);
    private readonly Dictionary<string, PngImage> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public static bool IsAllowed(string url) => url.Length <= 2048 && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && (uri.Host == "cdn.modrinth.com" && uri.AbsolutePath.StartsWith("/data/", StringComparison.Ordinal) || uri.Host is "media.forgecdn.net" or "mediafilez.forgecdn.net" && uri.AbsolutePath.StartsWith("/avatars/", StringComparison.Ordinal));

    public async Task<ResourceIconResult> ReadAsync(ResourceIconQuery query, CancellationToken token)
    {
        if (!IsAllowed(query.Url)) return new(null);
        lock (_gate) if (_cache.TryGetValue(query.Url, out var cached)) return new(cached);
        IDisposable? admission = null;
        bool entered = false;
        try
        {
            await _slots.WaitAsync(token).ConfigureAwait(false); entered = true;
            lock (_gate) if (_cache.TryGetValue(query.Url, out var cached)) return new(cached);
            // All icon work is optional. Bound the entire encoded/decode pipeline locally,
            // before taking shared HTTP admission, so quiet CPU waits cannot retain HTTP slots.
            admission = WorkScheduler is null ? null
                : await WorkScheduler.AcquireAsync(WorkPriority.Background, WorkResource.Http, token).ConfigureAwait(false);
            using var output = new MemoryStream();
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, query.Url);
                request.Headers.UserAgent.ParseAdd("NexaCL/2.0 (https://github.com/PCL-N-Edition/PCL-N)");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
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
            if (image is not null)
            {
                lock (_gate)
                {
                    if (_cache.Count >= 32) _cache.Remove(_cache.Keys.First());
                    _cache[query.Url] = image;
                }
            }
            return new(image);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(null); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException) { return new(null); }
        finally { admission?.Dispose(); if (entered) _slots.Release(); }
    }
    // The runtime cancels outstanding queries before disposal. Do not dispose a semaphore
    // while canceled HTTP operations are still unwinding and releasing their slots.
    public void Dispose() { lock (_gate) _cache.Clear(); }
}
