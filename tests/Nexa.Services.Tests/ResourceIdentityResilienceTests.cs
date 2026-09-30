using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Nexa.Services.Resources;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask ResourceIdentityMalformedProviderPreservesHealthySource()
    {
        string root = CreateTempDirectory();
        try
        {
            string instance = ResourceInstanceFixture(root);
            string directory = Path.Combine(instance, "mods"); Directory.CreateDirectory(directory);
            byte[] bytes = "a b\r\nc"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(directory, "fixture.jar"), bytes);
            string sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant();
            var modrinth = new JsonObject { [sha512] = new JsonObject { ["project_id"] = "ValidProject", ["id"] = "ValidVersion" } };
            var curseForgeMatch = new JsonObject
            {
                ["file"] = new JsonObject
                {
                    ["modId"] = 123,
                    ["id"] = 456,
                    ["fileFingerprint"] = 1621425345,
                    ["hashes"] = new JsonArray((JsonNode)new JsonObject { ["algo"] = 1, ["value"] = Convert.ToHexString(SHA1.HashData(bytes)) })
                }
            };
            string healthyModrinth = modrinth.ToJsonString();
            string healthyCurseForge = new JsonObject { ["data"] = new JsonObject { ["exactMatches"] = new JsonArray((JsonNode)curseForgeMatch) } }.ToJsonString();
            string invalidModrinthId = new JsonObject { [sha512] = new JsonObject { ["project_id"] = "../other", ["id"] = "ValidVersion" } }.ToJsonString();
            string nullModrinthMatch = new JsonObject { [sha512] = null }.ToJsonString();
            var scenarios = new (ResourceProvider Malformed, string Body)[]
            {
                (ResourceProvider.Modrinth, "null"), (ResourceProvider.Modrinth, "[]"),
                (ResourceProvider.Modrinth, nullModrinthMatch), (ResourceProvider.Modrinth, invalidModrinthId),
                (ResourceProvider.CurseForge, "null"), (ResourceProvider.CurseForge, "[]"),
                (ResourceProvider.CurseForge, "{}"), (ResourceProvider.CurseForge, "{\"data\":null}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":null}}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":{}}}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":[null]}}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":[{\"file\":null}]}}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":[{\"file\":{\"hashes\":{}}}]}}"),
                (ResourceProvider.CurseForge, "{\"data\":{\"exactMatches\":[{\"file\":{\"id\":456,\"modId\":123,\"hashes\":[null]}}]}}")
            };
            foreach (var scenario in scenarios)
            {
                int calls = 0;
                using var http = new HttpClient(new ResourceHttp(request =>
                {
                    calls++;
                    var provider = request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal) ? ResourceProvider.Modrinth : ResourceProvider.CurseForge;
                    string body = provider == scenario.Malformed ? scenario.Body : provider == ResourceProvider.Modrinth ? healthyModrinth : healthyCurseForge;
                    return new(HttpStatusCode.OK) { Content = new StringContent(body) };
                }));
                var context = await new ResourceInstanceService(new(http, "")).ReadAsync(new(root, "fixture"), default);
                AssertEqual(2, calls); AssertEqual(1, context.Installed.Count);
                AssertEqual(scenario.Malformed == ResourceProvider.Modrinth ? ResourceProvider.CurseForge : ResourceProvider.Modrinth, context.Installed[0].Source.Provider);
                AssertTrue(context.Notice is not null);
                AssertFalse(context.Notice!.Contains(scenario.Body, StringComparison.Ordinal));
            }
            // A malformed row must not discard other verified rows from the same provider either.
            string partiallyMalformed = new JsonObject { ["data"] = new JsonObject { ["exactMatches"] = new JsonArray((JsonNode)curseForgeMatch.DeepClone(), null) } }.ToJsonString();
            using (var http = new HttpClient(new ResourceHttp(request => new(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal) ? healthyModrinth : partiallyMalformed) })))
            {
                var context = await new ResourceInstanceService(new(http, "")).ReadAsync(new(root, "fixture"), default);
                AssertEqual(2, context.Installed.Count); AssertTrue(context.Notice is not null);
            }
            using var cancelled = new CancellationTokenSource();
            using var cancellationHttp = new HttpClient(new ResourceHttp(request =>
            {
                bool isModrinth = request.RequestUri!.AbsolutePath.EndsWith("version_files", StringComparison.Ordinal);
                if (!isModrinth) cancelled.Cancel();
                return new(HttpStatusCode.OK) { Content = new StringContent(isModrinth ? healthyModrinth : healthyCurseForge) };
            }));
            bool propagated = false;
            try { await new ResourceInstanceService(new(cancellationHttp, "")).ReadAsync(new(root, "fixture"), cancelled.Token); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { propagated = true; }
            AssertTrue(propagated);
        }
        finally { Directory.Delete(root, true); }
    }
}
