using System.IO.Compression;
using Nexa.Services.Caching;
using Nexa.Services.Files;
using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ContentMetadataUsesLocalNamesAndBoundedMedia()
    {
        string root = CreateTempDirectory();
        try
        {
            byte[] png;
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream("Fixtures.Steve.png")!)
            { using var output = new MemoryStream(); stream.CopyTo(output); png = output.ToArray(); }
            void Archive(string name, params (string Path, byte[] Data)[] files)
            {
                using var zip = ZipFile.Open(Path.Combine(root, name), ZipArchiveMode.Create);
                foreach (var file in files) { using var output = zip.CreateEntry(file.Path).Open(); output.Write(file.Data); }
            }
            static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
            Archive("opaque.jar", ("fabric.mod.json", Utf8("""{"id":"iris","name":"Iris Shaders","version":"1.8.0","description":"Local shaders","icon":"assets/icon.png"}""")), ("assets/icon.png", png));
            Archive("forge.jar", ("META-INF/mods.toml", Utf8("[[mods]]\nmodId=\"test\"\ndisplayName=\"Forge Example\"\nversion=\"${file.jarVersion}\"\nlogoFile=\"icon.png\"")), ("META-INF/MANIFEST.MF", Utf8("Implementation-Version: 3.2.1\n")), ("icon.png", png));
            Archive("quilt.jar", ("quilt.mod.json", Utf8("""{"quilt_loader":{"id":"test","version":"2.1","metadata":{"name":"Quilt Example","icon":"icon.png"}}}""")), ("icon.png", png));
            var mods = await InstanceContentMetadata.EnrichAsync(InstanceManagementService.ReadContent(new("mods", "模组", root), default), root, new(8 * 1024 * 1024), default);
            AssertEqual("Iris Shaders", mods.Entries.Single(item => item.Name == "opaque.jar").DisplayName);
            AssertEqual("3.2.1", mods.Entries.Single(item => item.Name == "forge.jar").Version);
            AssertEqual("Quilt Example", mods.Entries.Single(item => item.Name == "quilt.jar").DisplayName);
            AssertTrue(mods.Entries.All(item => item.Icon is not null));
            Archive("pack.zip", ("pack.mcmeta", Utf8("""{"pack":{"description":"§aGreen §lPack","pack_format":34}}""")), ("pack.png", png));
            var packs = await InstanceContentMetadata.EnrichAsync(new("resourcepacks", [new("pack.zip", false, new FileInfo(Path.Combine(root, "pack.zip")).Length)], true, null), root, new(8 * 1024 * 1024), default);
            AssertEqual("§aGreen §lPack", packs.Entries[0].DisplayName);
            AssertEqual("34", packs.Entries[0].Version);
            AssertTrue(packs.Entries[0].Icon is not null);
            Archive("too-large.jar", ("fabric.mod.json", new byte[256 * 1024 + 1]));
            var bounded = await InstanceContentMetadata.EnrichAsync(new("mods", [new("too-large.jar", false, 100)], true, null), root, new(1024), default);
            AssertEqual("too-large.jar", bounded.Entries[0].DisplayName);
            AssertTrue(bounded.Entries[0].Icon is null);
            File.WriteAllBytes(Path.Combine(root, "screen.png"), png);
            var screenshots = await InstanceContentMetadata.EnrichAsync(new("screenshots", [new("screen.png", false, png.Length)
            { ModifiedUtcTicks = File.GetLastWriteTimeUtc(Path.Combine(root, "screen.png")).Ticks }], true, null), root, new(8 * 1024 * 1024), default);
            AssertTrue(screenshots.Entries[0].Icon is not null);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await InstanceContentMetadata.EnrichAsync(mods, root, new(1024), stop.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async ValueTask ArchiveDisplayCacheReusesParsingAndChargesEachReadBudget()
    {
        string root = CreateTempDirectory();
        try
        {
            string path = Path.Combine(root, "local.jar");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                writer.Write("{\"id\":\"local\",\"name\":\"Shared display\",\"version\":\"1\"}");
            using SharedStateCache cache = new();
            var source = new InstanceContentSnapshot("mods", [new("local.jar", false, new FileInfo(path).Length) { Enabled = true }], true, null);
            ArchiveReadBudget firstBudget = new(4 * 1024 * 1024);
            var first = await InstanceContentMetadata.EnrichAsync(source, root, firstBudget, cache, default);
            AssertEqual("Shared display", first.Entries[0].DisplayName); AssertTrue(firstBudget.Remaining < 4 * 1024 * 1024);
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ArchiveReadBudget secondBudget = new(4 * 1024 * 1024);
                var second = await InstanceContentMetadata.EnrichAsync(source with { Entries = [source.Entries[0] with { Enabled = false }] },
                    root, secondBudget, cache, default);
                AssertEqual("Shared display", second.Entries[0].DisplayName); AssertEqual<bool?>(false, second.Entries[0].Enabled);
                AssertEqual(firstBudget.Remaining, secondBudget.Remaining);
                var small = await InstanceContentMetadata.EnrichAsync(source, root, new(1), cache, default);
                AssertEqual("local.jar", small.Entries[0].DisplayName);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
