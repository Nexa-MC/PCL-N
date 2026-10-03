using System.IO.Compression;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Minecraft.Process;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask InstanceServersPreserveUnknownTagsAndRejectStaleWrites()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), path = Path.Combine(instance, "servers.dat");
            var nbt = new ServerNbt(10, "", []); nbt.Children.Add(new(3, "third-party", [0, 0, 0, 7]));
            var servers = new ServerNbt(9, "servers", []) { ListType = 10 }; nbt.Children.Add(servers);
            var server = new ServerNbt(10, "", []); server.SetString("name", "原服务器🌐"); server.SetString("ip", "localhost:25565");
            server.SetString("icon", "cached-icon"); server.Children.Add(new(1, "acceptTextures", [1])); server.Children.Add(new(11, "unknown-array", [0, 0, 0, 1, 0, 0, 0, 9])); servers.Children.Add(server);
            var unicode = new ServerNbt(10, "", []); unicode.SetString("test", "a\0🌐");
            AssertEqual("a\0🌐", ServerNbt.Parse(unicode.Serialize()).String("test"));
            AssertTrue(unicode.Serialize().AsSpan().IndexOf(new byte[] { 0xC0, 0x80, 0xED, 0xA0, 0xBC, 0xED, 0xBC, 0x90 }) >= 0);
            byte[] original = nbt.Serialize();
            await using (var output = File.Create(path)) await using (var gzip = new GZipStream(output, CompressionMode.Compress)) await gzip.WriteAsync(original);
            var first = await InstanceServerListService.ReadAsync(new(instance));
            AssertEqual("原服务器🌐", first.Entries[0].Name); AssertEqual("cached-icon", first.Entries[0].Icon); AssertEqual<bool?>(true, first.Entries[0].AcceptTextures);
            var builder = new XsrStateStoreBuilder(); MinecraftProcessStateComposition.DeclareState(builder); var store = builder.Build();
            var change = new InstanceServerListSaveCommand(instance, first.Revision, [new(-1, "新服务器", "example.org"), first.Entries[0] with { Name = "已修改" }]);
            AssertTrue((await InstanceServerListService.SaveAsync(change, store)).IsSuccess);
            AssertTrue(File.Exists(path + "_old"));
            var after = await InstanceServerListService.ReadAsync(new(instance)); AssertEqual("新服务器", after.Entries[0].Name); AssertEqual("已修改", after.Entries[1].Name);
            var saved = ServerNbt.Parse(await File.ReadAllBytesAsync(path));
            AssertTrue(saved.Children.Single(t => t.Name == "third-party").Payload.SequenceEqual(new byte[] { 0, 0, 0, 7 }));
            var old = saved.Children.Single(t => t.Name == "servers").Children[1]; AssertTrue(old.Children.Any(t => t.Name == "unknown-array"));
            AssertEqual("cached-icon", old.String("icon"));
            AssertFalse((await InstanceServerListService.SaveAsync(change, store)).IsSuccess); // Old revision cannot overwrite.
            AssertFalse((await InstanceServerListService.SaveAsync(new(instance, after.Revision, [after.Entries[0], after.Entries[0]]), store)).IsSuccess);
            var running = new MinecraftProcessSnapshot(Guid.NewGuid(), "fixture", 1, MinecraftProcessState.Running, null, DateTimeOffset.UtcNow, null) { GameDirectory = instance };
            store.PublishDelta(store.Resolve(MinecraftProcessStateComposition.SessionsKey), new XsrCollectionDelta<MinecraftProcessSnapshot, Guid>(0, [running], []));
            AssertFalse((await InstanceServerListService.SaveAsync(new(instance, after.Revision, []), store)).IsSuccess);
            AssertEqual(2, (await InstanceServerListService.ReadAsync(new(instance))).Entries.Count);
            AssertFalse(Directory.EnumerateFiles(instance, ".nexa-servers-*").Any());
        }
        finally { Directory.Delete(root, true); }
    }
    private static async ValueTask InstanceServersRejectMalformedAndExpandedNbtWithoutModification()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root), path = Path.Combine(instance, "servers.dat");
            foreach (bool compressed in new[] { false, true })
            {
                if (compressed)
                {
                    await using var output = File.Create(path); await using var gzip = new GZipStream(output, CompressionMode.Compress);
                    await gzip.WriteAsync(new byte[8 * 1024 * 1024 + 1]);
                }
                else await File.WriteAllBytesAsync(path, [10, 0, 0, 9, 0, 7, 115, 101, 114, 118, 101, 114, 115, 10, 127, 255, 255, 255]);
                byte[] before = await File.ReadAllBytesAsync(path); bool rejected = false;
                try { await InstanceServerListService.ReadAsync(new(instance)); } catch (InvalidDataException) { rejected = true; }
                AssertTrue(rejected); AssertTrue((await File.ReadAllBytesAsync(path)).SequenceEqual(before));
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
