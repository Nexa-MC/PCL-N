using System.Text;
using System.Text.Json.Nodes;
using Nexa.Services.Updates;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static byte[] ReleaseManifestFixture()
    {
        using Stream input = typeof(Program).Assembly.GetManifestResourceStream("Fixtures.ReleaseManifest.json")!;
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    private static readonly UpdateBuildIdentity ReleaseInstalled = new("2.0.0.alpha.5", "linux-x64", "nativeaot-self-contained", "Release");

    private static async ValueTask SignedReleaseAuthenticatesOwnedIdentityAndActualPackage()
    {
        byte[] raw = ReleaseManifestFixture();
        var key = GenerateSigningKey();
        var verifier = new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint);
        byte[] signature = Encoding.ASCII.GetBytes(key.Sign(raw));
        // The caller changes both original buffers after verification and before parsing.
        var result = await VerifiedReleasePackage.VerifyAsync(raw, signature, ReleaseInstalled, "alpha",
            ReleaseInstalled.Version, "portable.tar.gz", new ReleaseMutationVerifier(verifier, () =>
            {
                Array.Fill(raw, (byte)0); Array.Fill(signature, (byte)0);
            }));
        AssertEqual("2.0.0.alpha.6", result.Version);
        AssertEqual("Nexa-2.0.0.alpha.6-linux-x64.portable.tar.gz", result.Name);
        byte[] package = "publisher fixture package\n"u8.ToArray();
        using (var input = new MemoryStream(package)) await result.VerifyPackageAsync(input);
        foreach (byte[] wrong in new[] { package[..^1], package.Concat(new byte[] { 0 }).ToArray(), Encoding.UTF8.GetBytes("modified! fixture package\n") })
        {
            using var input = new MemoryStream(wrong);
            await ExpectReleaseRejection(() => result.VerifyPackageAsync(input));
        }
        raw = ReleaseManifestFixture(); signature = Encoding.ASCII.GetBytes(key.Sign(raw));
        raw[0] ^= 1;
        await ExpectReleaseRejection(() => VerifiedReleasePackage.VerifyAsync(raw, signature, ReleaseInstalled,
            "alpha", ReleaseInstalled.Version, "portable.tar.gz", verifier));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        using var canceledPackage = new MemoryStream(package);
        try { await result.VerifyPackageAsync(canceledPackage, stop.Token); throw new InvalidOperationException("Package cancellation ignored."); }
        catch (OperationCanceledException) { }
        AssertEqual(0L, canceledPackage.Position);
    }

    private static async ValueTask SignedReleaseRejectsRoutingReplayAndUnorderedCi()
    {
        byte[] raw = ReleaseManifestFixture(); var key = GenerateSigningKey();
        var verifier = new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint);
        Task<VerifiedReleasePackage> Verify(byte[] data, UpdateBuildIdentity identity, string channel, string highest, string kind = "portable.tar.gz") =>
            VerifiedReleasePackage.VerifyAsync(data, Encoding.ASCII.GetBytes(key.Sign(data)), identity, channel, highest, kind, verifier);
        await Verify(raw, ReleaseInstalled, "alpha", ReleaseInstalled.Version);
        foreach (var identity in new[]
        {
            ReleaseInstalled with { RuntimeId = "linux-X64" }, ReleaseInstalled with { RuntimeVariant = "SelfContained" },
            ReleaseInstalled with { Configuration = "CI" }, ReleaseInstalled with { Version = "2.0.0.alpha.6" },
            ReleaseInstalled with { Version = "2.0.0.alpha.7" }, ReleaseInstalled with { Version = "v2.0.0.alpha.5" },
        }) await ExpectReleaseRejection(() => Verify(raw, identity, "alpha", ReleaseInstalled.Version));
        await ExpectReleaseRejection(() => Verify(raw, ReleaseInstalled, "beta", ReleaseInstalled.Version));
        await ExpectReleaseRejection(() => Verify(raw, ReleaseInstalled, "alpha", "2.0.0.alpha.6"));
        await ExpectReleaseRejection(() => Verify(raw, ReleaseInstalled, "alpha", "2.0.0.alpha.7"));
        await ExpectReleaseRejection(() => Verify(raw, ReleaseInstalled, "alpha", ReleaseInstalled.Version, "portable.zip"));
        string json = Encoding.UTF8.GetString(raw);
        foreach (var (version, channel) in new[] { ("2.0.0.beta.1", "beta"), ("2.0.0", "stable") })
        {
            byte[] next = Encoding.UTF8.GetBytes(json.Replace("2.0.0.alpha.6", version, StringComparison.Ordinal)
                .Replace("\"channel\":\"alpha\"", "\"channel\":\"" + channel + "\"", StringComparison.Ordinal));
            await Verify(next, ReleaseInstalled, channel, ReleaseInstalled.Version);
        }
        byte[] ci = Encoding.UTF8.GetBytes(json.Replace("2.0.0.alpha.6", "2.0.0.ci.abcdef", StringComparison.Ordinal)
            .Replace("\"channel\":\"alpha\"", "\"channel\":\"ci\"", StringComparison.Ordinal));
        await ExpectReleaseRejection(() => Verify(ci, ReleaseInstalled with { Version = "2.0.0.ci.000001" }, "ci", "2.0.0.ci.000001"));
    }

    private static async ValueTask SignedReleaseRejectsSchemaAliasesAndBoundsInput()
    {
        byte[] raw = ReleaseManifestFixture(); var key = GenerateSigningKey();
        var verifier = new UpdateGpgVerifier(key.ArmoredKey, key.Fingerprint);
        Task<VerifiedReleasePackage> Verify(byte[] data) => VerifiedReleasePackage.VerifyAsync(data,
            Encoding.ASCII.GetBytes(key.Sign(data)), ReleaseInstalled, "alpha", ReleaseInstalled.Version, "portable.tar.gz", verifier);
        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = true, root => root["schemaVersion"] = JsonNode.Parse("1.0"),
            root => root["product"] = "other", root => root.Remove("channel"), root => root["Channel"] = "alpha",
            root => root["url"] = "file:///payload", root => root["version"] = "2.0.0.alpha.0",
            root => root["runtimeVariant"] = "NativeAOT", root => root["configuration"] = "release",
            root => root["assets"]!.AsArray().RemoveAt(0),
            root => root["assets"]![1] = root["assets"]![0]!.DeepClone(),
            root => root["assets"]![0]!["size"] = true, root => root["assets"]![0]!["size"] = 0,
            root => root["assets"]![0]!["size"] = JsonNode.Parse("1.5"),
            root => root["assets"]![0]!["sha256"] = "not-a-hash", root => root["assets"]![0]!["rid"] = "win-arm64",
            root => root["assets"]![0]!["format"] = "unknown", root => root["assets"]![0]!["name"] = "../payload",
            root => root["assets"]![0]!["url"] = "https://example.invalid",
        ];
        foreach (var mutate in mutations)
        {
            JsonObject root = JsonNode.Parse(raw)!.AsObject(); mutate(root);
            await ExpectReleaseRejection(() => Verify(Encoding.UTF8.GetBytes(root.ToJsonString())));
        }
        string duplicate = Encoding.UTF8.GetString(raw).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        await ExpectReleaseRejection(() => Verify(Encoding.UTF8.GetBytes(duplicate)));
        string duplicateAsset = Encoding.UTF8.GetString(raw).Replace("\"format\":\"AppImage\"", "\"format\":\"AppImage\",\"format\":\"AppImage\"", StringComparison.Ordinal);
        await ExpectReleaseRejection(() => Verify(Encoding.UTF8.GetBytes(duplicateAsset)));
        // A genuinely signed, otherwise valid JSON envelope must still fail its actual budget.
        byte[] large = Encoding.UTF8.GetBytes(new string(' ', 1024 * 1024) + Encoding.UTF8.GetString(raw));
        await ExpectReleaseRejection(() => Verify(large));
        var budgetVerifier = new ReleaseBudgetVerifier();
        await ExpectReleaseRejection(() => VerifiedReleasePackage.VerifyAsync(raw, new byte[1024 * 1024 + 1],
            ReleaseInstalled, "alpha", ReleaseInstalled.Version, "portable.tar.gz", budgetVerifier));
        AssertEqual(0, budgetVerifier.Calls);
    }

    private static async Task ExpectReleaseRejection(Func<Task> work)
    {
        try { await work(); throw new InvalidOperationException("Untrusted release was admitted."); }
        catch (InvalidDataException) { }
    }

    private sealed class ReleaseMutationVerifier(IUpdateSignatureVerifier inner, Action mutate) : IUpdateSignatureVerifier
    {
        public async Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken = default)
        {
            await inner.VerifyAsync(content, detachedSignature, cancellationToken); mutate();
        }
    }

    private sealed class ReleaseBudgetVerifier : IUpdateSignatureVerifier
    {
        public int Calls { get; private set; }
        public Task VerifyAsync(Stream content, Stream detachedSignature, CancellationToken cancellationToken = default)
        {
            Calls++; return Task.CompletedTask;
        }
    }
}
