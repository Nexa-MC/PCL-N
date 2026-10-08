using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceClientProtocolReadsVerifiedSelectedJarAndRejectsUncertainFacts()
    {
        string root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProtocolFixtureAsync(root);
            AssertEqual<int?>(763, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            byte[] original = await File.ReadAllBytesAsync(fixture.Jar);
            string local = Path.Combine(fixture.Instance, "base.jar");
            await File.WriteAllBytesAsync(local, ProtocolJarBytes("{\"protocol_version\":764}"));
            AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await WriteProtocolManifestAsync(fixture.Manifest, local);
            AssertEqual<int?>(764, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            File.Delete(local); await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
            await File.WriteAllTextAsync(Path.Combine(fixture.Instance, "child.json"), "{\"id\":\"child\",\"jar\":\"base\"}");
            AssertEqual<int?>(763, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await File.WriteAllTextAsync(Path.Combine(fixture.Instance, "child.json"), "{\"id\":\"child\",\"inheritsFrom\":\" \",\"jar\":\"base\"}");
            AssertEqual<int?>(763, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            foreach (string json in new[] { "{\"protocol_version\":-1}", "{\"protocol_version\":\"763\"}", "{\"protocol_version\":763,\"protocol_version\":764}", "{}", "broken", "{\"protocol_version\":763,\"padding\":\"" + new string('x', 65536) + "\"}" })
            {
                await File.WriteAllBytesAsync(fixture.Jar, ProtocolJarBytes(json)); await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
                AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            }
            await File.WriteAllBytesAsync(fixture.Jar, ProtocolJarBytes("{\"protocol_version\":763}", duplicate: true)); await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
            AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await File.WriteAllBytesAsync(fixture.Jar, ProtocolJarBytes("{\"protocol_version\":763}", entries: 16385)); await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
            AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await File.WriteAllBytesAsync(fixture.Jar, original); await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
            await File.WriteAllTextAsync(fixture.Manifest, "{\"id\":\"base\"}");
            AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await WriteProtocolManifestAsync(fixture.Manifest, fixture.Jar);
            await File.WriteAllTextAsync(Path.Combine(fixture.Instance, "child.json"), "{\"id\":\"child\",\"inheritsFrom\":\"child\"}");
            AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            await File.WriteAllTextAsync(Path.Combine(fixture.Instance, "child.json"), "{\"id\":\"child\",\"inheritsFrom\":\"base\"}");
            if (!OperatingSystem.IsWindows())
            {
                string target = Path.Combine(root, "original.jar"); File.Move(fixture.Jar, target); File.CreateSymbolicLink(fixture.Jar, target);
                AssertEqual<int?>(null, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
                File.Delete(fixture.Jar); File.Move(target, fixture.Jar);
            }
            byte[] before = await File.ReadAllBytesAsync(fixture.Jar);
            AssertEqual<int?>(763, await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, default));
            byte[] after = await File.ReadAllBytesAsync(fixture.Jar); AssertTrue(before.SequenceEqual(after));
            using var stop = new CancellationTokenSource(); stop.Cancel(); bool cancelled = false;
            try { await InstanceOfflineReadinessService.ReadClientProtocolAsync(fixture.Instance, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
            AssertTrue(cancelled);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask InstanceServerProtocolComparesActualNetworkAndClientFacts()
    {
        string root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProtocolFixtureAsync(root); var store = new XsrStateStoreBuilder().Build();
            foreach (var (protocol, knownClient) in new (int? Protocol, bool KnownClient)[] { (763, true), (764, true), (null, true), (763, false) })
            {
                if (!knownClient) await File.WriteAllTextAsync(fixture.Manifest, "{\"id\":\"base\"}");
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    var list = await InstanceServerListService.ReadAsync(new(fixture.Instance));
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    AssertTrue((await InstanceServerListService.SaveAsync(new(fixture.Instance, list.Revision, [new(-1, "Fixture", "127.0.0.1:" + port)]), store)).IsSuccess);
                    list = await InstanceServerListService.ReadAsync(new(fixture.Instance));
                    var serve = Task.Run(async () =>
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token); await using var stream = client.GetStream();
                        int length = await InstanceServerStatusService.ReadVarAsync(stream, stop.Token); byte[] handshake = new byte[length]; await stream.ReadExactlyAsync(handshake, stop.Token);
                        byte[] request = new byte[2]; await stream.ReadExactlyAsync(request, stop.Token); AssertTrue(request.SequenceEqual(new byte[] { 1, 0 }));
                        string version = protocol is { } value ? "{\"name\":\"Untrusted name\",\"protocol\":" + value + "}" : "{\"name\":\"Same name proves nothing\"}";
                        byte[] json = Encoding.UTF8.GetBytes("{\"description\":\"Fixture\",\"version\":" + version + "}");
                        using var body = new MemoryStream(); InstanceServerStatusService.WriteVar(body, 0); InstanceServerStatusService.WriteVar(body, json.Length); body.Write(json);
                        using var response = new MemoryStream(); InstanceServerStatusService.WriteVar(response, (int)body.Length); body.WriteTo(response);
                        await stream.WriteAsync(response.ToArray(), stop.Token);
                    }, stop.Token);
                    var status = await InstanceServerStatusService.ReadAsync(new(fixture.Instance, list.Revision, 0), stop.Token); await serve;
                    AssertTrue(status.Reachable); AssertEqual(protocol, status.ServerProtocol); AssertEqual<int?>(knownClient ? 763 : null, status.ClientProtocol);
                }
                finally { listener.Stop(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(string Instance, string Jar, string Manifest)> CreateProtocolFixtureAsync(string root)
    {
        string instance = Path.Combine(root, "versions", "child"), vanilla = Path.Combine(root, "versions", "base"); Directory.CreateDirectory(instance); Directory.CreateDirectory(vanilla);
        string jar = Path.Combine(vanilla, "base.jar"), manifest = Path.Combine(vanilla, "base.json");
        await File.WriteAllTextAsync(Path.Combine(instance, "child.json"), "{\"id\":\"child\",\"inheritsFrom\":\"base\"}");
        await File.WriteAllBytesAsync(jar, ProtocolJarBytes("{\"protocol_version\":763}")); await WriteProtocolManifestAsync(manifest, jar);
        return (instance, jar, manifest);
    }

    private static async Task WriteProtocolManifestAsync(string manifest, string jar)
    {
        byte[] bytes = await File.ReadAllBytesAsync(jar);
        var json = new JsonObject { ["id"] = "base", ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["sha1"] = Convert.ToHexString(SHA1.HashData(bytes)), ["size"] = bytes.Length } } };
        await File.WriteAllTextAsync(manifest, json.ToJsonString());
    }

    private static byte[] ProtocolJarBytes(string metadata, bool duplicate = false, int entries = 1)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var file = archive.CreateEntry("version.json").Open()) file.Write(Encoding.UTF8.GetBytes(metadata));
            if (duplicate) { using var file = archive.CreateEntry("version.json").Open(); file.Write(Encoding.UTF8.GetBytes(metadata)); }
            for (int index = 1; index < entries; index++) archive.CreateEntry("fixture/" + index);
        }
        return output.ToArray();
    }
}
