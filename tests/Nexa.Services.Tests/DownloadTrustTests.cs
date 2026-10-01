using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Downloads;
using Nexa.Services.Minecraft.Downloads;
using Nexa.Services.Minecraft.Install;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Tasks;
using Nexa.Xsr.State;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private sealed class TrustHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsoluteUri);
            var response = respond(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage TrustResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    private static string TrustManifest(string url, string digest) => new JsonObject
    {
        ["versions"] = new JsonArray(new JsonObject
        { ["id"] = "1.20.1", ["url"] = url, ["sha1"] = digest })
    }.ToJsonString();

    private static async Task TrustRejectsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Untrusted download was accepted.");
    }

    private static async ValueTask MinecraftAuthorityRejectsMirrorMetadataAndRedirects()
    {
        const string versionUrl = "https://piston-meta.mojang.com/v1/packages/version.json";
        const string raw = "{\n  \"id\": \"1.20.1\", \"comment\": \"原始内容\"\n}";
        using var handler = new TrustHttpHandler(request => TrustResponse(request.RequestUri!.AbsoluteUri == versionUrl
            ? raw : TrustManifest(versionUrl, Sha1Hex(raw))));
        using var http = new HttpClient(handler);
        var source = new MinecraftInstallService.HttpMinecraftInstallMetadataSource(http);
        AssertEqual("1.20.1", (await source.FetchVanillaVersionJsonAsync("1.20.1", default))["id"]!.ToString());
        AssertEqual(2, handler.Requests.Count);

        foreach (string url in new[] { versionUrl, "https://evil.invalid/version.json", "https://piston-meta.mojang.com.evil.invalid/version.json" })
        {
            using var bad = new TrustHttpHandler(request => TrustResponse(request.RequestUri!.AbsoluteUri == versionUrl
                ? raw : TrustManifest(url, new('A', 40))));
            using var client = new HttpClient(bad);
            await TrustRejectsAsync<InvalidDataException>(() => new MinecraftInstallService.HttpMinecraftInstallMetadataSource(client)
                .FetchVanillaVersionJsonAsync("1.20.1", default));
            AssertEqual(url == versionUrl ? 2 : 1, bad.Requests.Count);
        }
        using var unavailable = new TrustHttpHandler(_ => new(HttpStatusCode.ServiceUnavailable));
        using var offline = new HttpClient(unavailable);
        await TrustRejectsAsync<InvalidOperationException>(() => new MinecraftInstallService.HttpMinecraftInstallMetadataSource(offline)
            .FetchVanillaVersionJsonAsync("1.20.1", default));
        AssertEqual(1, unavailable.Requests.Count);
        AssertFalse(unavailable.Requests.Any(url => url.Contains("bmclapi", StringComparison.Ordinal)));

        using var redirect = new TrustHttpHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("{}"),
            RequestMessage = new(HttpMethod.Get, "https://bmclapi2.bangbang93.com/index.json"),
        });
        using var redirected = new HttpClient(redirect);
        await TrustRejectsAsync<InvalidDataException>(() => new MinecraftInstallService.HttpMinecraftInstallMetadataSource(redirected)
            .FetchVerifiedAssetIndexAsync(versionUrl, null, -1, default));
    }

    private static async ValueTask MinecraftIndexUsesVerifiedRawBytesWithoutSecondTransfer()
    {
        string root = CreateTempDirectory();
        const string indexUrl = "https://piston-meta.mojang.com/v1/packages/index/5.json";
        const string versionUrl = "https://piston-meta.mojang.com/v1/packages/version.json";
        const string index = "{\n  \"objects\": {}\n}";
        string version = new JsonObject
        {
            ["id"] = "1.20.1",
            ["assetIndex"] = new JsonObject { ["id"] = "5", ["url"] = indexUrl, ["sha1"] = Sha1Hex(index), ["size"] = Encoding.UTF8.GetByteCount(index) },
            ["downloads"] = new JsonObject { ["client"] = new JsonObject { ["url"] = "https://piston-data.mojang.com/client/1.20.1.jar", ["sha1"] = Sha1Hex("JARCONTENT"), ["size"] = 10 } },
        }.ToJsonString();
        try
        {
            using var handler = new TrustHttpHandler(request => TrustResponse(request.RequestUri!.AbsoluteUri switch
            {
                versionUrl => version,
                indexUrl => index,
                _ => TrustManifest(versionUrl, Sha1Hex(version)),
            }));
            using var http = new HttpClient(handler);
            XsrStateStoreBuilder builder = new(); TaskCenterStateContract.DeclareState(builder); DownloadService.DeclareState(builder);
            var store = builder.Build();
            List<string> transfers = [];
            using var service = new MinecraftInstallService(new TaskCenterService(store), new DownloadService(store), http: http,
                connectionFactory: url => { transfers.Add(url); return new ServingConnection("JARCONTENT"u8.ToArray()); });
            var result = await service.InstallAsync(new(root, "1.20.1", InstanceName: "test"));
            AssertTrue(result.IsSuccess);
            AssertEqual(index, await File.ReadAllTextAsync(Path.Combine(root, "assets", "indexes", "5.json")));
            AssertEqual(1, handler.Requests.Count(url => url == indexUrl));
            AssertFalse(transfers.Any(url => url.EndsWith("5.json", StringComparison.Ordinal)));
            var source = new MinecraftInstallService.HttpMinecraftInstallMetadataSource(http);
            await TrustRejectsAsync<InvalidDataException>(() => source.FetchVerifiedAssetIndexAsync(indexUrl, new('A', 40), Encoding.UTF8.GetByteCount(index), default));
            await TrustRejectsAsync<InvalidDataException>(() => source.FetchVerifiedAssetIndexAsync(indexUrl, Sha1Hex(index), 1, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await TrustRejectsAsync<OperationCanceledException>(() => source.FetchVerifiedAssetIndexAsync(indexUrl, Sha1Hex(index), -1, cancelled.Token));
            using var overrun = new TrustHttpHandler(_ => { var response = TrustResponse("oversized"); response.Content.Headers.ContentLength = 1; return response; });
            using var bounded = new HttpClient(overrun);
            await TrustRejectsAsync<InvalidDataException>(() => AuthoritativeMetadata.ReadAsync(bounded, indexUrl, 4, default));

            // The public parsed-metadata port cannot preserve formatting of original network bytes.
            string customRoot = Path.Combine(root, "custom-provider");
            using var custom = new InstallFixture(new FakeMetadata
            { VanillaJson = JsonNode.Parse(version)!.AsObject(), AssetIndexJson = JsonNode.Parse(index)!.AsObject() });
            AssertTrue((await custom.Install.InstallAsync(new(customRoot, "1.20.1", InstanceName: "test"))).IsSuccess);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async ValueTask ForgeAuthorityRejectsRedirectBeforeInstallerDownload()
    {
        foreach (var loader in new[] { InstallLoader.Forge, InstallLoader.NeoForge })
        {
            string root = CreateTempDirectory();
            try
            {
                using var handler = new TrustHttpHandler(_ => new(HttpStatusCode.OK)
                { Content = new StringContent(new string('A', 40)), RequestMessage = new(HttpMethod.Get, "https://evil.invalid/installer.jar.sha1") });
                using var http = new HttpClient(handler);
                XsrStateStoreBuilder builder = new(); DownloadService.DeclareState(builder);
                var downloads = new DownloadService(builder.Build());
                bool transferred = false, executed = false;
                var service = new ForgeInstallService(downloads, http,
                    _ => { transferred = true; return new ServingConnection([]); }, (_, _) => Task.FromResult("java"),
                    (_, _) => { executed = true; return Task.CompletedTask; });
                await TrustRejectsAsync<InvalidDataException>(() => service.InstallAsync(new(root, "1.20.1", "test", loader, "47.2.0", new()), null, default));
                AssertFalse(transferred); AssertFalse(executed);

                byte[] genuine = InstallerFixtureArchive(new JsonObject { ["id"] = "loader", ["mainClass"] = "bootstrap.Main" });
                using var canonicalHandler = new TrustHttpHandler(_ => TrustResponse(Convert.ToHexString(SHA1.HashData(genuine))));
                using var canonicalHttp = new HttpClient(canonicalHandler);
                var mismatched = new ForgeInstallService(downloads, canonicalHttp,
                    source => { AssertEqual(ForgeInstallService.InstallerUrl(loader, "1.20.1", "47.2.0"), source); return new ServingConnection("tampered"u8.ToArray()); },
                    (_, _) => Task.FromResult("java"), (_, _) => { executed = true; return Task.CompletedTask; });
                await TrustRejectsAsync<IOException>(() => mismatched.InstallAsync(new(root, "1.20.1", "test", loader, "47.2.0", new()), null, default));
                AssertFalse(executed);

                // A receipt from the earlier acceptance policy cannot bypass a new canonical check.
                string oldKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(loader + "\n1.20.1\n47.2.0\n")));
                string oldDirectory = Path.Combine(root, ".task", "loader", oldKey); Directory.CreateDirectory(oldDirectory);
                await File.WriteAllBytesAsync(Path.Combine(oldDirectory, "installer.jar"), genuine);
                byte[] receipt = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(genuine)));
                string receiptPath = Path.Combine(oldDirectory, "sha256");
                await RecoveryRecordAuthority.AuthorizeAsync(receiptPath, receipt, default); await File.WriteAllBytesAsync(receiptPath, receipt);
                using var cache = new LoaderInstallerCache(root, new(root, "1.20.1", "test", loader, "47.2.0", new()));
                AssertFalse(await cache.RestoreAsync(Path.Combine(root, "restored.jar"), default));
                AssertFalse(File.Exists(Path.Combine(root, "restored.jar")));
            }
            finally { Directory.Delete(root, true); }
        }
        const string canonical = "https://maven.minecraftforge.net/net/minecraftforge/forge.jar";
        AssertTrue(MinecraftDownloadSourcePlanner.GetLibrarySources(canonical, true, null).SequenceEqual([canonical]));
        AssertTrue(MinecraftDownloadSourcePlanner.GetLauncherOrMetaSources("https://piston-data.mojang.com/client.jar", true, null)
            .SequenceEqual(["https://piston-data.mojang.com/client.jar"]));
    }

    private static async Task RewriteAuthorizedTrustRecord(string path, JsonObject record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, RecoveryJsonContext.Default.JsonObject);
        await RecoveryRecordAuthority.AuthorizeAsync(path, bytes, default);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static async ValueTask OldInstallTrustPreservesRollbackAndRejectsPublication()
    {
        string root = CreateTempDirectory();
        try
        {
            string stage = await MetadataTaskStage(root);
            string relative = "versions/test/test.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(stage, relative))!);
            await File.WriteAllTextAsync(Path.Combine(root, relative), "original");
            await File.WriteAllTextAsync(Path.Combine(stage, relative), "replacement");
            await InstallPublicationJournal.PrepareAsync(root, stage, "test", [relative], new Dictionary<string, string>(), default);
            string taskPath = Path.Combine(stage, ".task", "plan.json");
            var task = JsonNode.Parse(await File.ReadAllTextAsync(taskPath))!.AsObject();
            task["Schema"] = 1;
            await RewriteAuthorizedTrustRecord(taskPath, task);
            string publicationPath = Path.Combine(stage, ".publication", "plan.json");
            var publication = JsonNode.Parse(await File.ReadAllTextAsync(publicationPath))!.AsObject();
            publication["version"] = 1;
            await RewriteAuthorizedTrustRecord(publicationPath, publication);
            string progressPath = Path.Combine(stage, ".publication", "progress.json");
            var progress = JsonNode.Parse(await File.ReadAllTextAsync(progressPath))!.AsObject();
            progress["plan"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(publicationPath)));
            await RewriteAuthorizedTrustRecord(progressPath, progress);
            using var fixture = new InstallFixture(new FakeMetadata());
            Guid id = Guid.ParseExact(Path.GetFileName(stage), "N");
            await TrustRejectsAsync<InvalidDataException>(() => fixture.Install.ResumeInstallationAsync(root, id));
            var old = await InstallPublicationJournal.OpenAsync(root, stage, default);
            await TrustRejectsAsync<InvalidDataException>(() => old.ApplyAsync(default));
            AssertEqual("original", await File.ReadAllTextAsync(Path.Combine(root, relative)));
            AssertTrue(File.Exists(publicationPath));
            await fixture.Install.RollbackInstallationAsync(root, id);
            AssertEqual("original", await File.ReadAllTextAsync(Path.Combine(root, relative)));

            var pack = await ModpackInstallJournal.CreateAsync(root, new(new("source.pack", new('A', 64), "test", "1", "modrinth", "1.20.1", null, null, "pack", 0, 0), root), default);
            string intentPath = Path.Combine(pack.Stage, "intent.json");
            var intent = JsonNode.Parse(await File.ReadAllTextAsync(intentPath))!.AsObject(); intent["Schema"] = 1;
            await RewriteAuthorizedTrustRecord(intentPath, intent);
            var oldPack = await ModpackInstallJournal.OpenAsync(root, pack.Stage, default);
            await TrustRejectsAsync<InvalidDataException>(() => oldPack.PublishAsync(default));
            AssertFalse(Directory.Exists(oldPack.Destination));
            await oldPack.CancelAsync();
            AssertTrue(oldPack.Canceled);

            foreach (bool rollback in new[] { false, true })
                foreach (bool corrupt in new[] { false, true })
                {
                    // Simulate an old build dying after publication but before its terminal status write.
                    string committedStage = await MetadataTaskStage(root);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(committedStage, relative))!);
                    await File.WriteAllTextAsync(Path.Combine(root, relative), "original");
                    await File.WriteAllTextAsync(Path.Combine(committedStage, relative), "replacement");
                    await InstallPublicationJournal.PrepareAsync(root, committedStage, "test", [relative], new Dictionary<string, string>(), default);
                    string committedTask = Path.Combine(committedStage, ".task", "plan.json");
                    var oldTask = JsonNode.Parse(await File.ReadAllTextAsync(committedTask))!.AsObject(); oldTask["Schema"] = 1;
                    await RewriteAuthorizedTrustRecord(committedTask, oldTask);
                    string committedPlan = Path.Combine(committedStage, ".publication", "plan.json");
                    var oldPlan = JsonNode.Parse(await File.ReadAllTextAsync(committedPlan))!.AsObject(); oldPlan["version"] = 1;
                    await RewriteAuthorizedTrustRecord(committedPlan, oldPlan);
                    string committedProgress = Path.Combine(committedStage, ".publication", "progress.json");
                    var completed = JsonNode.Parse(await File.ReadAllTextAsync(committedProgress))!.AsObject();
                    completed["plan"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(committedPlan)));
                    completed["phase"] = "committed"; completed["attempted"] = 1;
                    await RewriteAuthorizedTrustRecord(committedProgress, completed);
                    await File.WriteAllTextAsync(Path.Combine(root, relative), corrupt ? "user-change" : "replacement");
                    Guid committedId = Guid.ParseExact(Path.GetFileName(committedStage), "N");
                    Func<Task> repair = rollback ? () => fixture.Install.RollbackInstallationAsync(root, committedId)
                        : async () => { await fixture.Install.ResumeInstallationAsync(root, committedId); };
                    if (corrupt) await TrustRejectsAsync<IOException>(repair);
                    else
                    {
                        if (rollback) await TrustRejectsAsync<InvalidOperationException>(repair);
                        else await repair();
                        var saved = await InstallTaskJournal.ReadAsync(root, committedStage, default);
                        AssertEqual(InstallTaskStatus.Completed, await InstallTaskJournal.ReadStatusAsync(committedStage, saved, default));
                    }
                    AssertEqual(corrupt ? "user-change" : "replacement", await File.ReadAllTextAsync(Path.Combine(root, relative)));
                }
        }
        finally { Directory.Delete(root, true); }
    }
}
