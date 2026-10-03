using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexa.Platform.Updates;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask AutomaticUpdateLoopbackHttpTransaction()
    {
        var fixture = CreateAutomaticUpdateFixture();
        await using var server = new UpdateFixtureServer(fixture.Source, CreateAutomaticUpdateFixture().Source.Signature);
        Console.WriteLine($"Local update server: {server.Address} (test authority, isolated directory)");
        using var client = new HttpClient(new LoopbackUpdateTransport(server.Address)) { Timeout = TimeSpan.FromSeconds(20) };
        var source = new GitHubUpdateReleaseSource(client);
        foreach (UpdateHttpMode mode in new[] { UpdateHttpMode.Ready, UpdateHttpMode.PrefixedTag,
                     UpdateHttpMode.TruncatedPackage, UpdateHttpMode.BadPackage, UpdateHttpMode.BadSignature, UpdateHttpMode.OfflineRecovery })
        {
            string path = CreateTempDirectory();
            try
            {
                using var directory = new UpdateFixtureDirectory(path);
                using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                server.Mode = mode;
                var transaction = new AutomaticUpdateTransaction(directory, fixture.Identity, fixture.Verifier, source)
                {
                    FaultBoundary = phase => { if (mode == UpdateHttpMode.OfflineRecovery && phase == "high-water") throw new IOException("Simulated exit after acceptance"); },
                };
                int requestsBefore = server.Requests, packagesBefore = server.PackageRequests;
                if (mode is UpdateHttpMode.TruncatedPackage or UpdateHttpMode.BadPackage or UpdateHttpMode.BadSignature or UpdateHttpMode.OfflineRecovery)
                {
                    bool rejected = false;
                    try { await transaction.InstallAsync("2.0.0.alpha.6", "alpha", budget.Token); }
                    catch (Exception failure) when (failure is IOException or InvalidDataException) { rejected = true; }
                    AssertTrue(rejected, $"Expected failure for {mode}");
                    using (FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName)) AssertNull(UpdateTransactionJournal.Read(active));
                    using (FileStream water = directory.OpenRead("highest-accepted-version.journal"))
                        AssertEqual(mode == UpdateHttpMode.OfflineRecovery ? "2.0.0.alpha.6" : null, UpdateHighWaterJournal.Read(water).Version);
                    if (mode == UpdateHttpMode.BadSignature)
                    {
                        AssertEqual(packagesBefore, server.PackageRequests);
                        Console.WriteLine("PASS HTTP: wrong signing key rejected before package download");
                        continue;
                    }
                    int attempts = server.Requests;
                    server.Mode = mode == UpdateHttpMode.OfflineRecovery ? UpdateHttpMode.Offline : UpdateHttpMode.Ready;
                    transaction = new(directory, fixture.Identity, fixture.Verifier, source);
                    string resumed = await transaction.InstallAsync("2.0.0.alpha.6", "alpha", budget.Token);
                    AssertFixtureActivation(directory, resumed);
                    if (mode == UpdateHttpMode.OfflineRecovery) AssertEqual(attempts, server.Requests);
                    else AssertEqual(packagesBefore + 2, server.PackageRequests);
                    Console.WriteLine($"PASS HTTP: {mode} preserves old activation and recovers");
                }
                else
                {
                    string slot = await transaction.InstallAsync("2.0.0.alpha.6", "alpha", budget.Token);
                    AssertFixtureActivation(directory, slot);
                    AssertEqual(mode == UpdateHttpMode.PrefixedTag ? 6 : 3, server.Requests - requestsBefore);
                    Console.WriteLine($"PASS HTTP: {mode} verifies and activates signed bytes");
                }
                transaction.Rollback();
                using (FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName))
                    AssertEqual("2.0.0.alpha.5", UpdateTransactionJournal.Read(active)![0]);
                using (FileStream water = directory.OpenRead("highest-accepted-version.journal"))
                    AssertEqual("2.0.0.alpha.6", UpdateHighWaterJournal.Read(water).Version);
                await ExpectReleaseRejection(() => transaction.InstallAsync("2.0.0.alpha.6", "alpha", budget.Token));
                Console.WriteLine("PASS HTTP: rollback restores previous selection without weakening replay protection");
            }
            finally { Directory.Delete(path, recursive: true); }
        }
        Console.WriteLine($"Local update server smoke passed; HTTP requests={server.Requests}, package transfers={server.PackageRequests}.");
    }

    private static void AssertFixtureActivation(IUpdateDirectory directory, string slotName)
    {
        using (IUpdateDirectory slot = directory.OpenDirectory(slotName))
        using (FileStream image = slot.OpenRead("Nexa.Desktop.exe")) AssertEqual(3L, image.Length);
        using FileStream active = directory.OpenRead(AutomaticUpdateTransaction.ActivationName);
        string[] selected = UpdateTransactionJournal.Read(active)!;
        AssertEqual("2.0.0.alpha.6", selected[0]);
        AssertEqual(slotName, selected[1]);
    }

    private static async Task<int> ServeAutomaticUpdateFixture()
    {
        var fixture = CreateAutomaticUpdateFixture();
        await using var server = new UpdateFixtureServer(fixture.Source, CreateAutomaticUpdateFixture().Source.Signature);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) => { args.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            Console.WriteLine($"Test-only update server: {server.Address}");
            Console.WriteLine("GET /test-key.asc or /PCL-N-Edition/PCL-N/releases/download/2.0.0.alpha.6/Nexa-Release.json");
            Console.WriteLine("Fresh test key; fixture payloads are not executable. Ctrl+C stops the server.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

    private enum UpdateHttpMode { Ready, PrefixedTag, TruncatedPackage, BadPackage, BadSignature, OfflineRecovery, Offline }

    // Only this test handler redirects the production source; the helper exposes no override.
    private sealed class LoopbackUpdateTransport(Uri address) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri target = request.RequestUri!;
            if (target.Scheme != "https" || target.Host != "github.com"
                || !target.AbsolutePath.StartsWith("/PCL-N-Edition/PCL-N/releases/download/", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected production release request");
            request.RequestUri = new Uri(address, target.PathAndQuery);
            return base.SendAsync(request, cancellationToken);
        }
    }

    // Small GET-only in-memory server. No arbitrary disk access, upload or non-loopback binding.
    private sealed class UpdateFixtureServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly AutomaticUpdateFixtureSource _fixture;
        private readonly byte[] _wrongSignature;
        private readonly Task _worker;
        private volatile UpdateHttpMode _mode;
        private int _requests, _packages;

        internal UpdateFixtureServer(AutomaticUpdateFixtureSource fixture, byte[] wrongSignature)
        {
            _fixture = fixture; _wrongSignature = wrongSignature;
            _listener.Start(8);
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _worker = RunAsync();
        }
        internal Uri Address { get; }
        internal UpdateHttpMode Mode { get => _mode; set => _mode = value; }
        internal int Requests => Volatile.Read(ref _requests);
        internal int PackageRequests => Volatile.Read(ref _packages);

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using TcpClient socket = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    budget.CancelAfter(TimeSpan.FromSeconds(10));
                    try { await RespondAsync(socket.GetStream(), budget.Token); }
                    catch (IOException) { } // A client can disconnect after a rejected/truncated response.
                    catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
        }

        private async Task RespondAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] header = new byte[8192]; int used = 0;
            while (used < header.Length)
            {
                if (await stream.ReadAsync(header.AsMemory(used, 1), token) == 0) return;
                used++;
                if (used >= 4 && header[used - 4] == '\r' && header[used - 3] == '\n' && header[used - 2] == '\r' && header[used - 1] == '\n') break;
            }
            if (used == header.Length) return;
            string[] request = Encoding.ASCII.GetString(header, 0, used).Split("\r\n", StringSplitOptions.None)[0].Split(' ');
            Interlocked.Increment(ref _requests);
            UpdateHttpMode mode = _mode;
            string route = request.Length == 3 && request[0] == "GET" ? request[1] : "";
            const string prefix = "/PCL-N-Edition/PCL-N/releases/download/";
            string release = prefix + (mode == UpdateHttpMode.PrefixedTag ? "v" : "") + "2.0.0.alpha.6/";
            byte[]? body = null;
            bool package = false;
            if (route == "/test-key.asc") body = Encoding.ASCII.GetBytes(_fixture.PublicKey);
            else if (route == release + "Nexa-Release.json") body = _fixture.Manifest;
            else if (route == release + "Nexa-Release.json.asc") body = mode == UpdateHttpMode.BadSignature ? _wrongSignature : _fixture.Signature;
            else if (route == release + "Nexa-2.0.0.alpha.6-win-x64.portable.zip")
            {
                package = true; Interlocked.Increment(ref _packages);
                body = _fixture.Package;
                if (mode == UpdateHttpMode.BadPackage) { body = body.ToArray(); body[^1] ^= 1; }
            }
            string status = mode == UpdateHttpMode.Offline ? "503 Service Unavailable" : body is null ? "404 Not Found" : "200 OK";
            if (status != "200 OK") body = [];
            body ??= [];
            byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, token);
            int length = package && mode == UpdateHttpMode.TruncatedPackage ? body.Length / 2 : body.Length;
            await stream.WriteAsync(body.AsMemory(0, length), token);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); _listener.Stop();
            try { await _worker; }
            finally { _stop.Dispose(); }
        }
    }
}
