using System.Net;
using Nexa.Services.Caching;
using Nexa.Services.Resources;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static readonly byte[] BudgetIcon = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a3ioAAAAASUVORK5CYII=");
    private static string IconUrl(string name) => $"https://cdn.modrinth.com/data/{name}/icon.png";

    private static async ValueTask ResourceIconCacheHonorsBytesRecencyAndCancellation()
    {
        // Exercise each budget independently, through actual HTTP calls and image ownership.
        foreach ((long bytes, int entries) in new[] { (BudgetIcon.Length * 2L, 8), (1_048_576L, 2) })
        {
            var calls = new Dictionary<string, int>(StringComparer.Ordinal);
            using var http = new HttpClient(new ResourceHttp(request =>
            {
                string url = request.RequestUri!.AbsoluteUri;
                calls[url] = calls.GetValueOrDefault(url) + 1;
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(BudgetIcon) };
            }));
            using var service = new ResourceIconService(http, bytes, entries);
            var a = (await service.ReadAsync(new(IconUrl("a")), default)).Image!;
            var b = (await service.ReadAsync(new(IconUrl("b")), default)).Image!;
            AssertTrue(ReferenceEquals(a, (await service.ReadAsync(new(IconUrl("a")), default)).Image));
            await service.ReadAsync(new(IconUrl("c")), default);
            AssertTrue(ReferenceEquals(a, (await service.ReadAsync(new(IconUrl("a")), default)).Image));
            AssertTrue(!ReferenceEquals(b, (await service.ReadAsync(new(IconUrl("b")), default)).Image));
            AssertEqual(1, calls[IconUrl("a")]); AssertEqual(2, calls[IconUrl("b")]);
            // Eviction releases cache ownership, not an active page's encoded image.
            AssertTrue(b.Bytes.Span.SequenceEqual(BudgetIcon));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            bool canceled = false;
            try { await service.ReadAsync(new(IconUrl("a")), stop.Token); }
            catch (OperationCanceledException) { canceled = true; }
            AssertTrue(canceled); AssertEqual(1, calls[IconUrl("a")]);
        }
        int uncached = 0;
        using var smallHttp = new HttpClient(new ResourceHttp(_ =>
        {
            uncached++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(BudgetIcon) };
        }));
        using var small = new ResourceIconService(smallHttp, BudgetIcon.Length - 1, 8);
        AssertTrue((await small.ReadAsync(new(IconUrl("large")), default)).Image is not null);
        AssertTrue((await small.ReadAsync(new(IconUrl("large")), default)).Image is not null);
        AssertEqual(2, uncached);
    }

    private static async ValueTask ResourceIconDisposalRejectsLateAndQueuedWork()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var http = new HttpClient(new BudgetIconHttp(async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 4) admitted.SetResult();
            await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(BudgetIcon) };
        }));
        using var service = new ResourceIconService(http);
        Task<ResourceIconResult>[] pending = Enumerable.Range(0, 6)
            .Select(i => service.ReadAsync(new(IconUrl($"late{i}")), default)).ToArray();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Dispose(); release.SetResult();
        foreach (var result in await Task.WhenAll(pending)) AssertTrue(result.Image is null);
        AssertEqual(4, calls);
        AssertTrue((await service.ReadAsync(new(IconUrl("late0")), default)).Image is null);
        AssertEqual(4, calls);
        service.Dispose();
    }

    private static async ValueTask ResourceIconsShareApplicationFlightsAndIsolateCallerCancellation()
    {
        using SharedStateCache cache = new(maximumEntries: 8, maximumBytes: 2 * 1_048_576);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        CancellationToken producerToken = default;
        using var http = new HttpClient(new BudgetIconHttp(async (_, token) =>
        {
            Interlocked.Increment(ref calls); producerToken = token; admitted.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(BudgetIcon) };
        }));
        using ResourceIconService first = new(http, cache), second = new(http, cache);
        using CancellationTokenSource canceledCaller = new();
        ResourceIconQuery query = new(IconUrl("application-shared"));
        Task<ResourceIconResult> canceled = first.ReadAsync(query, canceledCaller.Token);
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<ResourceIconResult> live = second.ReadAsync(query, default);
        canceledCaller.Cancel();
        bool observedCancellation = false;
        try { await canceled; } catch (OperationCanceledException) { observedCancellation = true; }
        AssertTrue(observedCancellation);
        AssertFalse(producerToken.IsCancellationRequested);
        AssertEqual(1, calls);
        first.Dispose(); // The application factory still belongs to the other live reader.
        AssertFalse(producerToken.IsCancellationRequested);
        release.SetResult();
        ResourceIconResult completed = await live;
        AssertTrue(completed.Image is not null);
        second.Dispose();
        using ResourceIconService later = new(http, cache);
        ResourceIconResult reused = await later.ReadAsync(query, default);
        AssertTrue(ReferenceEquals(completed.Image, reused.Image));
        AssertEqual(1, calls);
        AssertTrue(completed.Image!.Bytes.Span.SequenceEqual(BudgetIcon));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        observedCancellation = false;
        try { await later.ReadAsync(query, stop.Token); } catch (OperationCanceledException) { observedCancellation = true; }
        AssertTrue(observedCancellation);
        AssertEqual(1, calls);
    }

    private static async ValueTask ResourceIconsDoNotRetainFailuresOrExceedSharedByteAdmission()
    {
        int invalidCalls = 0;
        using var invalidHttp = new HttpClient(new ResourceHttp(_ =>
        {
            invalidCalls++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        }));
        using SharedStateCache cache = new();
        using ResourceIconService invalid = new(invalidHttp, cache);
        AssertTrue((await invalid.ReadAsync(new(IconUrl("invalid")), default)).Image is null);
        AssertTrue((await invalid.ReadAsync(new(IconUrl("invalid")), default)).Image is null);
        AssertEqual(2, invalidCalls);
        AssertTrue((await invalid.ReadAsync(new("https://example.com/icon.png"), default)).Image is null);
        AssertEqual(2, invalidCalls);

        int uncached = 0;
        using var smallHttp = new HttpClient(new ResourceHttp(_ =>
        {
            uncached++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(BudgetIcon) };
        }));
        // Application retention conservatively reserves the entire allowed encoded maximum.
        using SharedStateCache smallCache = new(maximumBytes: 1_048_575);
        using ResourceIconService small = new(smallHttp, smallCache);
        AssertTrue((await small.ReadAsync(new(IconUrl("shared-budget")), default)).Image is not null);
        AssertTrue((await small.ReadAsync(new(IconUrl("shared-budget")), default)).Image is not null);
        AssertEqual(2, uncached);
    }

    private sealed class BudgetIconHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
