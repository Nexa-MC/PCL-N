using System.Security.Cryptography;
using System.Text.Json;

namespace Nexa.Services.Updates;

/// <summary>One package selected from authenticated release bytes; never replacement authority.</summary>
public sealed class VerifiedReleasePackage
{
    private static readonly string[] RootProperties = ["schemaVersion", "product", "version", "channel", "runtimeVariant", "configuration", "assets"];
    private static readonly string[] AssetProperties = ["name", "rid", "format", "size", "sha256"];
    private static readonly string[] RuntimeIds = ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    private const int MaximumEnvelopeBytes = 1024 * 1024;

    private VerifiedReleasePackage(string version, string channel, string rid, string format,
        string name, long size, string sha256)
    {
        Version = version; Channel = channel; RuntimeId = rid; Format = format;
        Name = name; Size = size; Sha256 = sha256;
    }

    public string Version { get; }
    public string Channel { get; }
    public string RuntimeId { get; }
    public string Format { get; }
    public string Name { get; }
    public long Size { get; }
    public string Sha256 { get; }

    /// <summary>The installed identity and high-water version must come from the trusted helper.</summary>
    public static Task<VerifiedReleasePackage> VerifyAsync(ReadOnlyMemory<byte> manifest,
        ReadOnlyMemory<byte> signature, UpdateBuildIdentity installedIdentity, string selectedChannel,
        string highestAcceptedVersion, string format, IUpdateSignatureVerifier verifier,
        CancellationToken cancellationToken = default)
        => VerifyCoreAsync(manifest, signature, installedIdentity, selectedChannel, highestAcceptedVersion, format, verifier, null, cancellationToken);

    internal static Task<VerifiedReleasePackage> VerifyResumeAsync(ReadOnlyMemory<byte> manifest,
        ReadOnlyMemory<byte> signature, UpdateBuildIdentity installed, string channel, string highest,
        string format, IUpdateSignatureVerifier verifier, string protectedDigest, CancellationToken token)
        => VerifyCoreAsync(manifest, signature, installed, channel, highest, format, verifier, protectedDigest, token);

    private static async Task<VerifiedReleasePackage> VerifyCoreAsync(ReadOnlyMemory<byte> manifest,
        ReadOnlyMemory<byte> signature, UpdateBuildIdentity installedIdentity, string selectedChannel,
        string highestAcceptedVersion, string format, IUpdateSignatureVerifier verifier, string? resumeDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installedIdentity);
        ArgumentNullException.ThrowIfNull(verifier);
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.Length is 0 or > MaximumEnvelopeBytes || signature.Length is 0 or > MaximumEnvelopeBytes)
            throw new InvalidDataException("发布清单或签名大小不合法。");
        // Ownership is shared with neither the caller nor an exposed writable stream.
        byte[] owned = manifest.ToArray();
        using var content = new MemoryStream(owned, writable: false);
        using var detached = new MemoryStream(signature.ToArray(), writable: false);
        await verifier.VerifyAsync(content, detached, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(owned, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            RequireProperties(root, RootProperties);
            if (!root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1
                || Text(root, "product") != "nexacl"
                || Text(root, "runtimeVariant") != "nativeaot-self-contained"
                || Text(root, "configuration") != "Release"
                || installedIdentity.RuntimeVariant != Text(root, "runtimeVariant")
                || installedIdentity.Configuration != Text(root, "configuration"))
                throw new InvalidDataException("发布清单产品或构建配置不匹配。");
            string version = Text(root, "version"), channel = Text(root, "channel");
            UpdateVersion candidate = CanonicalVersion(version);
            UpdateVersion installed = CanonicalVersion(installedIdentity.Version);
            UpdateVersion highest = CanonicalVersion(highestAcceptedVersion);
            string derivedChannel = candidate.Stage switch
            {
                UpdateVersionStage.Alpha => "alpha",
                UpdateVersionStage.Beta => "beta",
                UpdateVersionStage.Stable => "stable",
                _ => "ci",
            };
            if (candidate.Stage == UpdateVersionStage.Ci || channel != derivedChannel || channel != selectedChannel
                || candidate <= installed || (candidate <= highest && !(candidate == highest && resumeDigest is not null
                    && Convert.ToHexStringLower(SHA256.HashData(owned)) == resumeDigest)))
                throw new InvalidDataException("发布版本、通道或防回退策略不匹配。");
            var expected = new HashSet<string>(StringComparer.Ordinal);
            foreach (string rid in RuntimeIds)
                foreach (string kind in Formats(rid)) expected.Add($"Nexa-{version}-{rid}.{kind}");
            JsonElement assets = root.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 18)
                throw new InvalidDataException("发布清单包集合不完整。");
            VerifiedReleasePackage? selected = null;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireProperties(asset, AssetProperties);
                string rid = Text(asset, "rid"), kind = Text(asset, "format"), name = Text(asset, "name");
                string digest = Text(asset, "sha256");
                if (name != $"Nexa-{version}-{rid}.{kind}" || !expected.Remove(name)
                    || !asset.GetProperty("size").TryGetInt64(out long size) || size <= 0
                    || digest.Length != 64 || !digest.All(static c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
                    throw new InvalidDataException("发布包身份、长度或摘要不合法。");
                if (rid == installedIdentity.RuntimeId && kind == format)
                    selected = new(version, channel, rid, kind, name, size, digest);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return expected.Count == 0 && selected is not null ? selected
                : throw new InvalidDataException("发布包与当前平台或格式不匹配。");
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("发布清单格式无效。", failure);
        }
    }

    /// <summary>Checks actual bytes only; the helper must retain object ownership through later use.</summary>
    public async Task VerifyPackageAsync(Stream package, CancellationToken cancellationToken = default)
        => await ReceivePackageAsync(package, destination: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Hashes the same bytes copied into an already-open, initially empty helper-owned stream.
    /// The helper must establish protected object ownership before calling and discard failed output.
    /// Neither stream is closed or reopened; successful output is flushed and rewound.
    /// </summary>
    public async Task CopyVerifiedPackageAsync(Stream package, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(package, destination) || !destination.CanWrite || !destination.CanSeek
            || destination.Position != 0 || destination.Length != 0)
            throw new ArgumentException("更新接收目标必须是独立、可写、可定位且为空的受保护对象。", nameof(destination));
        // An already-cancelled call does not inspect/read the source or mutate the destination.
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await ReceivePackageAsync(package, destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            destination.Position = 0;
        }
        catch (Exception failure)
        {
            try
            {
                destination.SetLength(0);
                destination.Position = 0;
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("更新接收失败且无法清除暂存内容，必须丢弃该对象。", failure, cleanupFailure);
            }
            throw;
        }
    }

    private async Task ReceivePackageAsync(Stream package, Stream? destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = await package.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (count > Size - total) throw new InvalidDataException("发布包实际长度超过已签名长度。");
            total += count;
            digest.AppendData(buffer, 0, count);
            if (destination is not null)
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (total != Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布包长度或摘要不匹配。");
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string Text(JsonElement value, string property) => value.GetProperty(property).ValueKind == JsonValueKind.String
        ? value.GetProperty(property).GetString()! : throw new InvalidDataException("发布清单字段类型不合法。");

    private static void RequireProperties(JsonElement value, string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("发布清单对象不合法。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidDataException("发布清单字段重复或未知。");
        if (seen.Count != expected.Length) throw new InvalidDataException("发布清单字段缺失。");
    }

    private static UpdateVersion CanonicalVersion(string value)
    {
        if (value is not { Length: > 0 and <= 128 } || !UpdateVersion.TryParse(value, out var parsed)
            || parsed.ToString() != value || ((parsed.Stage is UpdateVersionStage.Alpha or UpdateVersionStage.Beta) && parsed.Sequence <= 0)
            || (parsed.Stage == UpdateVersionStage.Ci && parsed.Commit?.Length != 6))
            throw new InvalidDataException("发布版本不符合规范。");
        return parsed;
    }

    private static string[] Formats(string rid) => rid.StartsWith("win-", StringComparison.Ordinal)
        ? ["setup.exe", "msi", "portable.zip"] : rid.StartsWith("linux-", StringComparison.Ordinal)
            ? ["deb", "rpm", "AppImage", "portable.tar.gz"] : ["dmg", "portable.tar.gz"];
}
