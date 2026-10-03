using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceServerStatusUsesBoundedProtocolAndAdmittedRevision()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root); var store = new XsrStateStoreBuilder().Build();
            foreach (bool oversized in new[] { false, true })
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var list = await InstanceServerListService.ReadAsync(new(instance));
                    AssertTrue((await InstanceServerListService.SaveAsync(new(instance, list.Revision, [new(-1, "Local", "127.0.0.1:" + port)]), store)).IsSuccess);
                    list = await InstanceServerListService.ReadAsync(new(instance));
                    var serve = Task.Run(async () =>
                    {
                        using var client = await listener.AcceptTcpClientAsync(timeout.Token); await using var stream = client.GetStream();
                        int length = await InstanceServerStatusService.ReadVarAsync(stream, timeout.Token); AssertTrue(length is > 0 and < 1024);
                        byte[] handshake = new byte[length]; await stream.ReadExactlyAsync(handshake, timeout.Token);
                        using var packet = new MemoryStream(handshake); AssertEqual(0, await InstanceServerStatusService.ReadVarAsync(packet, timeout.Token));
                        AssertEqual(-1, await InstanceServerStatusService.ReadVarAsync(packet, timeout.Token));
                        byte[] request = new byte[2]; await stream.ReadExactlyAsync(request, timeout.Token); AssertTrue(request.SequenceEqual(new byte[] { 1, 0 }));
                        using var response = new MemoryStream();
                        if (oversized) InstanceServerStatusService.WriteVar(response, 262145);
                        else
                        {
                            byte[] json = Encoding.UTF8.GetBytes("{\"description\":{\"text\":\"Hello\",\"extra\":[{\"text\":\" World\"}]},\"version\":{\"name\":\"Fixture\"},\"players\":{\"online\":3,\"max\":20}}");
                            using var body = new MemoryStream(); InstanceServerStatusService.WriteVar(body, 0); InstanceServerStatusService.WriteVar(body, json.Length); body.Write(json);
                            InstanceServerStatusService.WriteVar(response, (int)body.Length); body.WriteTo(response);
                        }
                        await stream.WriteAsync(response.ToArray(), timeout.Token);
                    }, timeout.Token);
                    var status = await InstanceServerStatusService.ReadAsync(new(instance, list.Revision, 0), timeout.Token);
                    await serve; AssertEqual(!oversized, status.Reachable);
                    if (!oversized) { AssertEqual("Hello World", status.Description); AssertEqual<int?>(3, status.OnlinePlayers); AssertEqual("Fixture", status.Version); }
                    bool stale = false;
                    try { await InstanceServerStatusService.ReadAsync(new(instance, "stale", 0), timeout.Token); } catch (IOException) { stale = true; }
                    AssertTrue(stale);
                }
                finally { listener.Stop(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
