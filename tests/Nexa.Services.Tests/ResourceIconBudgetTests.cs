using System.Net;
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

    private sealed class BudgetIconHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
