using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Nexa.Services.Network;

/// <summary>Host-owned socket pools selected from the committed policy for every new request.</summary>
public sealed partial class NetworkHttpClientPool : IDisposable
{
    private readonly Func<NetworkPreferences> _capture;
    private readonly Func<NetworkPreferences, bool, HttpMessageHandler> _factory;
    private readonly Dictionary<bool, Generation> _current = [];
    private readonly object _gate = new();
    private readonly bool _mainlandChina;
    private bool _disposed;

    public NetworkHttpClientPool(Func<NetworkPreferences> capture, bool mainlandChina = false)
        : this(capture, (policy, redirects) => CreateTransport(policy, redirects, mainlandChina)) { _mainlandChina = mainlandChina; }

    internal NetworkHttpClientPool(Func<NetworkPreferences> capture,
        Func<NetworkPreferences, bool, HttpMessageHandler> factory)
    { _capture = capture ?? throw new ArgumentNullException(nameof(capture)); _factory = factory; }

    public HttpClient CreateClient(bool allowAutoRedirect = true) => new(CreateHandler(allowAutoRedirect));
    public HttpMessageHandler CreateHandler(bool allowAutoRedirect = true) => new RoutingHandler(this, allowAutoRedirect);
    /// <summary>Separate TLS/certificate policy with the same committed network preference provider.</summary>
    public NetworkHttpClientPool CreateSpecialized(Action<SocketsHttpHandler> configureTransport)
    {
        ArgumentNullException.ThrowIfNull(configureTransport);
        return new(_capture, (policy, redirects) => CreateTransport(policy, redirects, _mainlandChina, configureTransport));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool redirects, CancellationToken token)
    {
        NetworkPreferences policy = _capture();
        policy.Validate();
        bool probe = request.Options.TryGetValue(ProbeOption, out bool marked) && marked;
        long started = Stopwatch.GetTimestamp();
        if (!NetworkProviderAdmission.IsAllowed(policy, request.RequestUri!))
        {
            RecordTrace(policy, request, probe, started, null, "ProviderDisabled");
            throw new HttpRequestException("The selected content provider is disabled.");
        }
        Generation generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_current.TryGetValue(redirects, out generation!) || !SameTransport(generation.Policy, policy))
            {
                var next = new Generation(policy, new HttpMessageInvoker(_factory(policy, redirects)));
                generation?.Retire();
                generation = next;
                _current[redirects] = generation;
            }
            generation.Acquire();
        }
        try
        {
            HttpResponseMessage response = await generation.Transport.SendAsync(request, token).ConfigureAwait(false);
            RecordTrace(policy, request, probe, started, (int)response.StatusCode, null);
            if (policy.AutoDiagnose && !probe && (int)response.StatusCode >= 500) StartDiagnosticProbe(request.RequestUri!);
            response.Content = new LeasedContent(response.Content, generation);
            return response;
        }
        catch (Exception error)
        {
            RecordTrace(policy, request, probe, started, null, error.GetType().Name);
            if (policy.AutoDiagnose && !probe && !token.IsCancellationRequested) StartDiagnosticProbe(request.RequestUri!);
            generation.Release(); throw;
        }
    }

    private static bool SameTransport(NetworkPreferences first, NetworkPreferences second) =>
        first.ProxyMode == second.ProxyMode && first.ProxyAddress == second.ProxyAddress && first.ProxyUser == second.ProxyUser
        && first.ProxyPassword == second.ProxyPassword && first.DnsOverHttps == second.DnsOverHttps && first.IpStack == second.IpStack;

    internal static SocketsHttpHandler CreateSocketsHandler(NetworkPreferences policy, bool redirects)
    {
        policy.Validate();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16,
            UseProxy = policy.ProxyMode != 0
        };
        if (policy.ProxyMode == 2)
        {
            handler.Proxy = new WebProxy(new Uri(policy.ProxyAddress))
            {
                Credentials = policy.ProxyUser.Length == 0 && policy.ProxyPassword.Length == 0 ? null
                : new NetworkCredential(policy.ProxyUser, policy.ProxyPassword)
            };
        }
        return handler;
    }

    private static HttpMessageHandler CreateTransport(NetworkPreferences policy, bool redirects, bool mainlandChina,
        Action<SocketsHttpHandler>? configureTransport = null)
    {
        SocketsHttpHandler handler = CreateSocketsHandler(policy, redirects);
        try { configureTransport?.Invoke(handler); }
        catch { handler.Dispose(); throw; }
        if ((!policy.DnsOverHttps || policy.ProxyMode == 2) && policy.IpStack == "auto") return handler;
        DnsOverHttpsResolver? resolver = policy.DnsOverHttps && policy.ProxyMode != 2
            ? new(mainlandChina, policy.ProxyMode) : null;
        handler.ConnectCallback = async (context, token) =>
        {
            // A system proxy owns destination resolution; its own hostname uses system DNS.
            bool destination = resolver is not null && string.Equals(context.DnsEndPoint.Host,
                context.InitialRequestMessage.RequestUri?.IdnHost, StringComparison.OrdinalIgnoreCase);
            IPAddress[] addresses = destination
                ? await resolver!.ResolveAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false)
                : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            foreach (IPAddress address in OrderAddresses(addresses, policy.IpStack))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(4));
                    await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("The destination could not be connected.");
        };
        return resolver is null ? handler : new OwnedResolverHandler(handler, resolver);
    }

    internal static IEnumerable<IPAddress> OrderAddresses(IPAddress[] addresses, string preference) => preference == "auto" ? addresses
        : addresses.OrderBy(address => address.AddressFamily == (preference == "ipv4" ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6) ? 0 : 1);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Generation generation in _current.Values) generation.Retire();
            _current.Clear();
        }
        _diagnosticStop.Cancel();
        _diagnosticStop.Dispose();
    }

    private sealed class RoutingHandler(NetworkHttpClientPool owner, bool redirects) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            owner.SendAsync(request, redirects, token);
    }
    private sealed class OwnedResolverHandler(HttpMessageHandler transport, DnsOverHttpsResolver resolver) : DelegatingHandler(transport)
    {
        protected override void Dispose(bool disposing) { if (disposing) resolver.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Generation(NetworkPreferences policy, HttpMessageInvoker transport) : IDisposable
    {
        public NetworkPreferences Policy { get; } = policy;
        public HttpMessageInvoker Transport { get; } = transport;
        private readonly object _gate = new();
        private int _leases;
        private bool _retired;
        public void Acquire() { lock (_gate) _leases++; }
        public void Release() { lock (_gate) { _leases--; if (_retired && _leases == 0) Transport.Dispose(); } }
        public void Retire() { lock (_gate) { _retired = true; if (_leases == 0) Transport.Dispose(); } }
        public void Dispose() => Retire();
    }
    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private Generation? _lease;
        public LeasedContent(HttpContent inner, Generation lease)
        { _inner = inner; _lease = lease; foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value); }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => _inner.CopyToAsync(stream, token);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => _inner.ReadAsStreamAsync(token);
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength is not null; }
        protected override void Dispose(bool disposing)
        { if (disposing) { _inner.Dispose(); Interlocked.Exchange(ref _lease, null)?.Release(); } base.Dispose(disposing); }
    }
}
