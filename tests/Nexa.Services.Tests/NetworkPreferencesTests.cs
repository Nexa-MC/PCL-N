using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexa.Services.Downloads;
using Nexa.Services.Foundation;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Network;
using Nexa.Services.Settings;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask NetworkTransportCapturesCommittedPreferencesAndRetainsActiveResponses()
    {
        NetworkPreferences policy = new() { DnsOverHttps = false };
        List<NetworkPoolTestHandler> generations = [];
        using var pool = new NetworkHttpClientPool(() => policy, (snapshot, redirects) =>
        {
            var handler = new NetworkPoolTestHandler(snapshot, redirects);
            generations.Add(handler); return handler;
        });
        using var client = pool.CreateClient(allowAutoRedirect: false);
        using HttpResponseMessage first = await client.GetAsync("https://example.test/first", HttpCompletionOption.ResponseHeadersRead);
        AssertEqual(1, generations.Count);
        policy = new() { ProxyMode = 2, ProxyAddress = "socks5://127.0.0.1:1080", ProxyUser = "local-user", ProxyPassword = "local-password", DnsOverHttps = false, IpStack = "ipv4" };
        using HttpResponseMessage second = await client.GetAsync("https://example.test/second", HttpCompletionOption.ResponseHeadersRead);
        AssertEqual(2, generations.Count);
        AssertFalse(generations[0].Disposed);
        AssertEqual("first", await first.Content.ReadAsStringAsync());
        first.Dispose(); AssertTrue(generations[0].Disposed);
        AssertFalse(generations[1].Disposed);
        using HttpResponseMessage third = await client.GetAsync("https://example.test/third", HttpCompletionOption.ResponseHeadersRead);
        AssertEqual(2, generations.Count);
        AssertEqual(policy, generations[1].Policy);
        AssertFalse(generations[1].Redirects);
        AssertFalse(policy.ToString().Contains("local-user", StringComparison.Ordinal));
        AssertFalse(policy.ToString().Contains("local-password", StringComparison.Ordinal));
        AssertFalse(policy.ToString().Contains("127.0.0.1", StringComparison.Ordinal));
        pool.Dispose(); AssertFalse(generations[1].Disposed);
        second.Dispose(); third.Dispose(); AssertTrue(generations[1].Disposed);
    }

    private static ValueTask NetworkProxyModesAndIpPreferencesPreserveTrust()
    {
        foreach (string valid in new[] { "http://127.0.0.1:8080", "https://proxy.example.test", "socks5://[::1]:1080" })
            AssertTrue(NetworkPreferences.IsValidProxyAddress(valid));
        foreach (string invalid in new[] { "", "127.0.0.1:8080", "ftp://proxy.test", "http://name:secret@proxy.test", "https://proxy.test/path", "https://proxy.test/?token=secret", "https://proxy.test/#fragment", "https://proxy.test\n" })
            AssertFalse(NetworkPreferences.IsValidProxyAddress(invalid));
        using var direct = NetworkHttpClientPool.CreateSocketsHandler(new() { ProxyMode = 0 }, false);
        AssertFalse(direct.UseProxy); AssertFalse(direct.AllowAutoRedirect); AssertFalse(direct.UseCookies);
        AssertTrue(direct.SslOptions.RemoteCertificateValidationCallback is null);
        using var system = NetworkHttpClientPool.CreateSocketsHandler(new(), true);
        AssertTrue(system.UseProxy); AssertTrue(system.Proxy is null);
        using var directBootstrap = DnsOverHttpsResolver.CreateBootstrapHandler(proxyMode: 0);
        using var systemBootstrap = DnsOverHttpsResolver.CreateBootstrapHandler(proxyMode: 1);
        AssertFalse(directBootstrap.UseProxy); AssertTrue(systemBootstrap.UseProxy);
        AssertTrue(directBootstrap.ConnectCallback is null); AssertTrue(systemBootstrap.ConnectCallback is null);
        AssertFalse(directBootstrap.AllowAutoRedirect); AssertFalse(systemBootstrap.AllowAutoRedirect);
        AssertFalse(directBootstrap.UseCookies); AssertFalse(systemBootstrap.UseCookies);
        AssertTrue(directBootstrap.SslOptions.RemoteCertificateValidationCallback is null);
        AssertTrue(systemBootstrap.SslOptions.RemoteCertificateValidationCallback is null);
        using var custom = NetworkHttpClientPool.CreateSocketsHandler(new() { ProxyMode = 2, ProxyAddress = "http://proxy.test:8080", ProxyUser = "user", ProxyPassword = "secret" }, false);
        AssertEqual("http://proxy.test:8080/", custom.Proxy!.GetProxy(new("https://example.test"))!.AbsoluteUri);
        var credential = custom.Proxy.Credentials!.GetCredential(new("http://proxy.test:8080"), "Basic");
        AssertEqual("user", credential!.UserName); AssertEqual("secret", credential.Password);
        IPAddress[] addresses = [IPAddress.IPv6Loopback, IPAddress.Loopback];
        AssertEqual(AddressFamily.InterNetwork, NetworkHttpClientPool.OrderAddresses(addresses, "ipv4").First().AddressFamily);
        AssertEqual(AddressFamily.InterNetworkV6, NetworkHttpClientPool.OrderAddresses(addresses.Reverse().ToArray(), "ipv6").First().AddressFamily);
        AssertEqual(addresses[0], NetworkHttpClientPool.OrderAddresses(addresses, "auto").First());
        return ValueTask.CompletedTask;
    }

    private static async ValueTask DnsOverHttpsBoundsRecordsCachesAndUsesRegionalFallback()
    {
        int queries = 0, fallback = 0;
        var handler = new NetworkDnsTestHandler(request =>
        {
            queries++;
            bool v6 = request.RequestUri!.Query.Contains("type=28", StringComparison.Ordinal);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(v6
                ? "{\"Status\":0,\"Answer\":[{\"type\":28,\"TTL\":60,\"data\":\"::1\"}]}"
                : "{\"Status\":0,\"Answer\":[{\"type\":1,\"TTL\":60,\"data\":\"127.0.0.1\"},{\"type\":28,\"TTL\":60,\"data\":\"::2\"}]}")
            };
        });
        using var resolver = new DnsOverHttpsResolver(new HttpClient(handler), false,
            (_, _) => { fallback++; return Task.FromResult(new[] { IPAddress.Parse("192.0.2.1") }); });
        var resolved = await resolver.ResolveAsync("example.test", CancellationToken.None);
        AssertEqual(2, resolved.Length); AssertEqual(AddressFamily.InterNetworkV6, resolved[0].AddressFamily);
        resolved[0] = IPAddress.Any;
        AssertEqual(IPAddress.IPv6Loopback, (await resolver.ResolveAsync("example.test", CancellationToken.None))[0]);
        AssertEqual(2, queries); AssertEqual(0, fallback);
        AssertEqual(IPAddress.Loopback, (await resolver.ResolveAsync("127.0.0.1", CancellationToken.None))[0]); AssertEqual(2, queries);
        foreach (bool china in new[] { false, true })
        {
            List<string> hosts = [];
            using var failed = new DnsOverHttpsResolver(new HttpClient(new NetworkDnsTestHandler(request =>
            {
                hosts.Add(request.RequestUri!.Host);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri.Host == "cloudflare-dns.com"
                    ? new string('x', 65537) : "{\"Status\":0,\"Answer\":[{\"type\":1,\"TTL\":0,\"data\":\"198.51.100.1\"}]}")
                };
            })), china, (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.2") }));
            var result = await failed.ResolveAsync("fallback.test", CancellationToken.None);
            AssertEqual(china ? IPAddress.Parse("198.51.100.1") : IPAddress.Parse("192.0.2.2"), result[0]);
            AssertEqual(china ? 4 : 2, hosts.Count);
            AssertEqual(china, hosts.Contains("doh.pub"));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool threw = false;
        try { await resolver.ResolveAsync("cancel.test", canceled.Token); } catch (OperationCanceledException) { threw = true; }
        AssertTrue(threw);
    }

    private static async ValueTask DownloadBandwidthCapturesGenerationsAndSerializesBodyReads()
    {
        long limit = 1024;
        using var clock = new NetworkBudgetClock();
        var limiter = new DownloadBandwidthLimiter(() => limit, clock);
        DownloadBandwidthBudget first = limiter.Capture(); AssertTrue(ReferenceEquals(first, limiter.Capture()));
        AssertEqual(102L, first.MaximumReadBytes);
        Task one = first.WaitAsync(512).AsTask();
        Task two = first.WaitAsync(512).AsTask();
        AssertEqual(1, clock.TimersCreated); AssertFalse(one.IsCompleted); AssertFalse(two.IsCompleted);
        limit = 2048; DownloadBandwidthBudget next = limiter.Capture();
        AssertFalse(ReferenceEquals(first, next)); AssertEqual(1024L, first.BytesPerSecond); AssertEqual(2048L, next.BytesPerSecond);
        clock.Advance(); await one;
        await clock.WaitForTimersAsync(2); AssertFalse(two.IsCompleted);
        clock.Advance(); await two;
        using var cancellation = new CancellationTokenSource();
        Task canceled = first.WaitAsync(512, cancellation.Token).AsTask(); cancellation.Cancel();
        bool threw = false; try { await canceled; } catch (OperationCanceledException) { threw = true; }
        AssertTrue(threw);
        Task recovered = first.WaitAsync(512).AsTask(); AssertFalse(recovered.IsCompleted); clock.Advance(); await recovered;
        limit = 0; var unlimited = limiter.Capture(); AssertEqual(0L, unlimited.BytesPerSecond);
        Task unbounded = unlimited.WaitAsync(1024 * 1024).AsTask();
        AssertTrue(unbounded.IsCompletedSuccessfully);
        await unbounded;

        using var fractionalClock = new NetworkBudgetClock();
        var fractional = new DownloadBandwidthLimiter(() => 2048, fractionalClock).Capture();
        Task fractionOne = fractional.WaitAsync(3).AsTask();
        AssertEqual(TimeSpan.FromMilliseconds(2), fractionalClock.LastDueTime);
        fractionalClock.Advance(); await fractionOne;
        Task fractionTwo = fractional.WaitAsync(3).AsTask();
        AssertEqual(TimeSpan.FromMilliseconds(1), fractionalClock.LastDueTime);
        fractionalClock.Advance(); await fractionTwo;
        AssertEqual(TimeSpan.FromMilliseconds(3), fractionalClock.Elapsed);

        using var fastClock = new NetworkBudgetClock();
        var fast = new DownloadBandwidthLimiter(() => 128L * 1024 * 1024, fastClock).Capture();
        Task fastOne = fast.WaitAsync(64 * 1024).AsTask();
        AssertFalse(fastOne.IsCompleted);
        AssertEqual(TimeSpan.FromMilliseconds(1), fastClock.LastDueTime);
        fastClock.Advance(); await fastOne;
        await fast.WaitAsync(64 * 1024);
        AssertEqual(1, fastClock.TimersCreated);
        Task fastThree = fast.WaitAsync(64 * 1024).AsTask();
        AssertFalse(fastThree.IsCompleted);
        fastClock.Advance(); await fastThree;
        await fast.WaitAsync(64 * 1024);
        AssertEqual(2, fastClock.TimersCreated);
        AssertEqual(TimeSpan.FromMilliseconds(2), fastClock.Elapsed);
    }

    private static async ValueTask DownloadEngineUsesCapturedBandwidthAndCleansUpOnCancellation()
    {
        string root = CreateTempDirectory();
        try
        {
            long cap = 1024; using var clock = new NetworkBudgetClock();
            var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder);
            var service = new DownloadService(builder.Build()) { BandwidthLimiter = new(() => cap, clock) };
            var connection = new NetworkBudgetConnection();
            using var token = new CancellationTokenSource();
            Task<DownloadTransferResult> transfer = service.DownloadAsync(new() { Sources = ["memory://budget"], DestinationPath = Path.Combine(root, "body.bin"), ConnectionFactory = _ => connection }, cancellationToken: token.Token);
            await clock.WaitForTimersAsync(1); AssertEqual(102, connection.LastReadLimit); AssertFalse(transfer.IsCompleted);
            cap = 0; AssertFalse(transfer.IsCompleted);
            token.Cancel(); bool threw = false; try { await transfer; } catch (OperationCanceledException) { threw = true; }
            AssertTrue(threw);
            WaitForDrainedState(service);
            AssertTrue(connection.Stopped);
            var unlimited = new NetworkBudgetConnection();
            var result = await service.DownloadAsync(new() { Sources = ["memory://next"], DestinationPath = Path.Combine(root, "next.bin"), ConnectionFactory = _ => unlimited });
            AssertTrue(result.Success); AssertEqual(1024L, result.TotalBytes);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask FoundationNetworkPreferencesReachActualRequestsAndManagedJavaRoots()
    {
        string root = CreateTempDirectory();
        try
        {
            string game = Path.Combine(root, "game"), java = Path.Combine(root, "managed-java");
            var port = new DiagnosticSettingsPort();
            using var host = FoundationComposer.ComposeWithJavaRuntimeRoot(port, LauncherDefaults.CreateSchema(), new ThrowingProfilePort(),
                minecraftRootDirectory: game, javaRuntimeRootDirectory: java);
            AssertEqual(2, host.JavaManagedRuntimeRoots.Count);
            AssertTrue(host.JavaManagedRuntimeRoots.Contains(Path.Combine(game, "runtime")));
            AssertTrue(host.JavaManagedRuntimeRoots.Contains(java));
            DownloadBandwidthBudget initial = host.Downloads.BandwidthLimiter!.Capture(); AssertEqual(0L, initial.BytesPerSecond);
            AssertTrue(host.SettingsPolicy.Set(new("network.bandwidth-kib", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "123"))).IsSuccess);
            AssertEqual(123L * 1024, host.Downloads.BandwidthLimiter.Capture().BytesPerSecond); AssertEqual(0L, initial.BytesPerSecond);
            AssertTrue(host.SettingsPolicy.Set(new("network.proxy-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
            AssertTrue(host.SettingsPolicy.Set(new("network.doh", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "false"))).IsSuccess);
            await using var direct = new NetworkOneRequestServer("direct");
            await using var proxy = new NetworkOneRequestServer("proxy");
            using HttpClient client = host.CreateHttpClient(allowAutoRedirect: false);
            client.Timeout = TimeSpan.FromSeconds(5);
            AssertEqual("direct", await client.GetStringAsync(direct.Address));
            AssertTrue(direct.RequestLine.StartsWith("GET / ", StringComparison.Ordinal));
            AssertTrue(host.SettingsPolicy.Set(new("network.proxy-address", SettingsLayer.Global, new(SettingsOverrideMode.Custom, proxy.Address.AbsoluteUri))).IsSuccess);
            AssertTrue(host.SettingsPolicy.Set(new("network.proxy-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))).IsSuccess);
            port.FailSave = true;
            AssertFalse(host.SettingsPolicy.Set(new("network.proxy-mode", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "0"))).IsSuccess);
            AssertEqual("proxy", await client.GetStringAsync("http://origin.invalid/body"));
            AssertTrue(proxy.RequestLine.StartsWith("GET http://origin.invalid/body ", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NetworkOneRequestServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _worker;
        public Uri Address { get; }
        public string RequestLine { get; private set; } = "";
        public NetworkOneRequestServer(string response)
        {
            _listener.Start(); Address = new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _worker = RespondAsync(response);
        }
        private async Task RespondAsync(string response)
        {
            try
            {
                using var socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                using var stream = socket.GetStream();
                byte[] header = new byte[8192]; int read = 0;
                while (read < header.Length)
                {
                    if (await stream.ReadAsync(header.AsMemory(read, 1), _stop.Token) == 0) return;
                    read++;
                    if (read >= 4 && header[read - 4] == '\r' && header[read - 3] == '\n' && header[read - 2] == '\r' && header[read - 1] == '\n') break;
                }
                RequestLine = Encoding.ASCII.GetString(header, 0, read).Split("\r\n", StringSplitOptions.None)[0];
                byte[] bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}");
                await stream.WriteAsync(bytes, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        { await _stop.CancelAsync(); _listener.Stop(); await _worker; _stop.Dispose(); }
    }

    private sealed class NetworkPoolTestHandler(NetworkPreferences policy, bool redirects) : HttpMessageHandler
    {
        public NetworkPreferences Policy = policy; public bool Redirects = redirects; public bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri!.AbsolutePath.TrimStart('/')) });
        protected override void Dispose(bool disposing) { if (disposing) Disposed = true; base.Dispose(disposing); }
    }

    private static async ValueTask LoaderAndModpackTransfersConsumeRetryAndConcurrencySettings()
    {
        foreach (bool retry in new[] { false, true })
        {
            string root = CreateTempDirectory();
            try
            {
                var (_, settings) = PolicyFixture();
                AssertTrue(settings.Set(new("network.file-retry", SettingsLayer.Global, new(SettingsOverrideMode.Custom, retry ? "true" : "false"))).IsSuccess);
                byte[] archive = InstallerFixtureArchive(new System.Text.Json.Nodes.JsonObject { ["id"] = "loader", ["mainClass"] = "bootstrap.Main" });
                using var http = new HttpClient(new StaticHttpMessageHandler(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(archive))));
                var builder = new XsrStateStoreBuilder(); DownloadService.DeclareState(builder);
                int attempts = 0; bool executed = false;
                var service = new ForgeInstallService(new(builder.Build()), http, source =>
                { AssertEqual(ForgeInstallService.InstallerUrl(InstallLoader.Forge, "1.20.1", "47.2.0"), source); attempts++; return new ServingConnection("tampered"u8.ToArray()); },
                    (_, _) => Task.FromResult("java"), (_, _) => { executed = true; return Task.CompletedTask; })
                { SettingsPolicy = settings };
                bool rejected = false;
                try { await service.InstallAsync(new(root, "1.20.1", "test", InstallLoader.Forge, "47.2.0", new()), null, default); }
                catch (IOException) { rejected = true; }
                AssertTrue(rejected); AssertFalse(executed); AssertEqual(retry ? 2 : 1, attempts);
                AssertFalse(Directory.Exists(Path.Combine(root, "versions", "test")));
            }
            finally { Directory.Delete(root, true); }
        }

        string temporary = CreateTempDirectory();
        try
        {
            var (_, settings) = PolicyFixture();
            AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "2"))).IsSuccess);
            string pack = Path.Combine(temporary, "pack.mrpack"), root = Path.Combine(temporary, "game"); Directory.CreateDirectory(root);
            WriteLocalJar(pack, ("modrinth.index.json", MrpackIndex().ToJsonString()));
            var preview = await MinecraftModpackArchive.InspectAsync(pack);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int active = 0, peak = 0;
            using var fixture = new InstallFixture(PackMetadata(), settingsPolicy: settings, connectionFactory: source =>
                source.Contains("cdn.modrinth.com", StringComparison.Ordinal)
                ? new NetworkGatedPackConnection(new ServingConnection(PayloadFor(source)), async token =>
                { int count = Interlocked.Increment(ref active); InterlockedExtensionsUpdateMaximum(ref peak, count); if (count == 2) started.TrySetResult(); await release.Task.WaitAsync(token); }, () => Interlocked.Decrement(ref active))
                : new ServingConnection(PayloadFor(source)));
            Task<Nexa.Xsr.XsrResult<MinecraftInstallResult>> install = fixture.Install.InstallModpackAsync(new(preview, root, true));
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); AssertEqual(2, active);
                AssertTrue(settings.Set(new("network.file-concurrency", SettingsLayer.Global, new(SettingsOverrideMode.Custom, "1"))).IsSuccess);
            }
            finally { release.TrySetResult(); }
            var result = await install; AssertTrue(result.IsSuccess, result.Error?.Message ?? "Modpack install failed."); AssertEqual(2, peak);
            AssertTrue(File.Exists(Path.Combine(result.Value!.InstanceDirectory, "mods", "required.jar")));
            AssertTrue(File.Exists(Path.Combine(result.Value.InstanceDirectory, "mods", "optional.jar")));
        }
        finally { Directory.Delete(temporary, true); }
    }

    private sealed class NetworkGatedPackConnection(IDownloadConnection inner, Func<CancellationToken, Task> enter, Action leave) : IDownloadConnection
    {
        private int _entered;
        public async ValueTask<DownloadConnectionInfo> StartAsync(long offset, CancellationToken token = default)
        { _entered = 1; await enter(token); return await inner.StartAsync(offset, token); }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public async ValueTask StopAsync(CancellationToken token = default)
        { await inner.StopAsync(token); if (Interlocked.Exchange(ref _entered, 0) != 0) leave(); }
    }
    private sealed class NetworkDnsTestHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class NetworkBudgetConnection : IDownloadConnection
    {
        private int _read; public int LastReadLimit; public bool Stopped;
        public ValueTask<DownloadConnectionInfo> StartAsync(long beginOffset, CancellationToken token = default) => ValueTask.FromResult(new DownloadConnectionInfo(1024, 0, 1023, false));
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { LastReadLimit = buffer.Length; int count = Math.Min(buffer.Length, 1024 - _read); buffer.Span[..count].Fill(7); _read += count; return ValueTask.FromResult(count); }
        public ValueTask StopAsync(CancellationToken token = default) { Stopped = true; return ValueTask.CompletedTask; }
    }
    private sealed class NetworkBudgetClock : TimeProvider, IDisposable
    {
        private readonly object _gate = new(); private readonly List<NetworkBudgetTimer> _timers = []; private int _created;
        private long _timestamp;
        private TimeSpan _lastDueTime;
        public int TimersCreated { get { lock (_gate) return _created; } }
        public TimeSpan LastDueTime { get { lock (_gate) return _lastDueTime; } }
        public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _timestamp; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new NetworkBudgetTimer(callback, state, _timestamp + dueTime.Ticks);
                _timers.Add(timer); _created++; _lastDueTime = dueTime;
                return timer;
            }
        }
        public void Advance()
        {
            NetworkBudgetTimer[] pending;
            lock (_gate)
            {
                var active = _timers.Where(timer => !timer.Disposed).ToArray();
                if (active.Length == 0) return;
                _timestamp = active.Min(timer => timer.DueAt);
                pending = active.Where(timer => timer.DueAt <= _timestamp).ToArray();
            }
            foreach (var timer in pending) timer.Fire();
        }
        public async Task WaitForTimersAsync(int count) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (TimersCreated < count) await Task.Delay(1, deadline.Token); }
        public void Dispose() { lock (_gate) foreach (var timer in _timers) timer.Dispose(); }
        private sealed class NetworkBudgetTimer(TimerCallback callback, object? state, long dueAt) : ITimer
        {
            private int _disposed;
            public bool Disposed => Volatile.Read(ref _disposed) != 0;
            public long DueAt { get; } = dueAt;
            public void Fire() { if (Interlocked.Exchange(ref _disposed, 1) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _disposed) == 0;
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
