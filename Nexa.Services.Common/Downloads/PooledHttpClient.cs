using Nexa.Services.Logging;

namespace Nexa.Services.Downloads;

/// <summary>Public HTTP clients share sockets, but never cookies or mutable client headers.</summary>
internal static class PooledHttpClient
{
    private static readonly HttpMessageInvoker Redirecting = CreatePool(true);
    private static readonly HttpMessageInvoker NoRedirects = CreatePool(false);
    private static HttpMessageInvoker CreatePool(bool redirects) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = redirects,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 16,
    });

    internal static HttpClient Create(bool allowAutoRedirect = true, LogService? log = null)
    {
        HttpMessageHandler handler = new SharedPoolHandler(allowAutoRedirect ? Redirecting : NoRedirects);
        if (log is not null) handler = new DiagnosticHttpHandler(log, handler);
        return new HttpClient(handler);
    }

    // Disposing a client releases its diagnostic wrapper, not the shared process-lifetime pool.
    private sealed class SharedPoolHandler(HttpMessageInvoker pool) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            pool.SendAsync(request, token);
    }
}
