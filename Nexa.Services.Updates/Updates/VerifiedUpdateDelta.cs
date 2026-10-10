using System.Security.Cryptography;
using System.Text.Json;

namespace Nexa.Services.Updates;

internal interface IUpdateDeltaSource
{
    Task<(byte[] Index, byte[] Signature)> ReadDeltaIndexAsync(string version, CancellationToken token);
}

internal sealed record VerifiedUpdateDelta(string Name, long Size, string Sha256, string FromVersion)
{
    internal static async Task<VerifiedUpdateDelta?> SelectAsync(byte[] index, byte[] signature,
        VerifiedReleasePackage release, string installed, IUpdateSignatureVerifier verifier, CancellationToken token)
    {
        if (index.Length is 0 or > 1024 * 1024 || signature.Length is 0 or > 1024 * 1024)
            throw new InvalidDataException("差分索引大小无效。");
        byte[] owned = index.ToArray();
        using var input = new MemoryStream(owned, writable: false);
        using var detached = new MemoryStream(signature.ToArray(), writable: false);
        await verifier.VerifyAsync(input, detached, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            RequireProperties(root, "schemaVersion", "product", "version", "runtimeVariant", "configuration", "patches");
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || Text(root, "product") != "nexacl"
                || Text(root, "version") != release.Version || Text(root, "runtimeVariant") != "nativeaot-self-contained"
                || Text(root, "configuration") != "Release") throw new InvalidDataException("差分索引身份不匹配。");
            JsonElement patches = root.GetProperty("patches");
            // Five historical versions across the six supported runtime identifiers.
            if (patches.ValueKind != JsonValueKind.Array || patches.GetArrayLength() > 30)
                throw new InvalidDataException("差分索引条目超限。");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            VerifiedUpdateDelta? selected = null;
            foreach (JsonElement patch in patches.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                RequireProperties(patch, "fromVersion", "rid", "name", "size", "sha256", "targetSha256");
                string from = Text(patch, "fromVersion"), rid = Text(patch, "rid"), name = Text(patch, "name");
                string sha = Text(patch, "sha256"), targetSha = Text(patch, "targetSha256");
                long size = patch.GetProperty("size").GetInt64();
                UpdateVersion baseVersion = UpdateHighWaterJournal.ParseVersion(from);
                if (baseVersion >= UpdateHighWaterJournal.ParseVersion(release.Version)
                    || rid is not ("win-x64" or "win-arm64" or "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64")
                    || name != $"Nexa-{release.Version}-{rid}.from-{from}.delta.zip" || !identities.Add(rid + "/" + from)
                    || size is <= 0 or > 2L * 1024 * 1024 * 1024 || !IsDigest(sha) || !IsDigest(targetSha))
                    throw new InvalidDataException("差分包身份、长度或摘要无效。");
                if (rid == release.RuntimeId && from == installed && targetSha == release.Sha256 && size < release.Size)
                    selected = new(name, size, sha, from);
            }
            return selected;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new InvalidDataException("差分索引格式无效。", error); }
    }

    internal async Task ReceiveAsync(Stream input, Stream output, CancellationToken token)
    {
        output.SetLength(0); output.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long actual = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (count > Size - actual) throw new InvalidDataException("差分包实际长度超限。");
            actual += count; hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        if (actual != Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != Sha256)
            throw new InvalidDataException("差分包长度或摘要不匹配。");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Position = 0;
    }

    internal static bool IsDigest(string value) => value.Length == 64 && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    internal static string Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String
        ? value.GetProperty(name).GetString()! : throw new InvalidDataException("差分字段类型无效。");
    internal static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("差分对象无效。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("差分字段重复或未知。");
        if (seen.Count != names.Length) throw new InvalidDataException("差分字段缺失。");
    }
}

