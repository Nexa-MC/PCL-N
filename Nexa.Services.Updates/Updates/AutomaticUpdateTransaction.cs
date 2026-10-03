using System.Security.Cryptography;
using Nexa.Platform.Updates;

namespace Nexa.Services.Updates;

public interface IUpdateReleaseSource
{
    Task<(byte[] Manifest, byte[] Signature)> ReadReleaseAsync(string version, CancellationToken token);
    Task<Stream> OpenPackageAsync(string version, string name, CancellationToken token);
}

/// <summary>Runs inside the preinstalled helper, over a system-admitted installation.</summary>
public sealed class AutomaticUpdateTransaction(IUpdateDirectory installation, UpdateBuildIdentity baseline,
    IUpdateSignatureVerifier verifier, IUpdateReleaseSource source)
{
    public const string ActivationName = "active-update.journal", StatusName = "update-status.journal";
    private const string PendingName = "pending-update.journal", HighWaterName = "highest-accepted-version.journal";
    internal Action<string>? FaultBoundary { get; init; }

    public async Task<string> InstallAsync(string version, string channel, CancellationToken token = default)
    {
        UpdateHighWaterJournal.ParseVersion(version);
        if (channel is not ("alpha" or "beta" or "stable")) throw new InvalidDataException("更新通道无效。");
        using FileStream exclusive = installation.OpenState("update-transaction.lock");
        using FileStream pending = installation.OpenState(PendingName);
        using FileStream highWater = installation.OpenState(HighWaterName);
        using FileStream active = installation.OpenState(ActivationName, publicRead: true, exclusive: false);
        using FileStream status = installation.OpenState(StatusName, publicRead: true, exclusive: false);
        string[]? current = UpdateTransactionJournal.Read(active);
        string[]? previous = UpdateTransactionJournal.Read(pending);
        string installed = current is { Length: 4 } && UpdateVersion.TryParse(current[0], out var activeVersion)
            && UpdateVersion.TryParse(baseline.Version, out var baseVersion) && activeVersion > baseVersion ? current[0] : baseline.Version;
        if (current is not null && current.Length != 4) throw new InvalidDataException("生效版本记录无效。");
        if (installed == version && current is not null) { Publish("complete"); return current[1]; }
        string? highest = UpdateHighWaterJournal.Read(highWater).Version;
        string floor = highest ?? installed;
        string? resumeDigest = previous is { Length: 7 } && previous[0] == version && previous[1] == channel
            && previous[6] is "prepared" or "accepted" ? previous[3] : null;
        if (highest is not null && UpdateHighWaterJournal.ParseVersion(version) <= UpdateHighWaterJournal.ParseVersion(highest) && resumeDigest is null)
            throw new InvalidDataException("此版本已被接受，且不存在可恢复事务。");
        string previousSlot = current is { Length: 4 } && installed == current[0] ? current[1] : "";
        try
        {
            Publish("verifying");
            byte[] manifest, signature;
            IUpdateDirectory? cached = null;
            if (previous is { Length: 7 } && previous[0] == version && previous[1] == channel)
            {
                cached = installation.OpenDirectory(previous[2]);
                manifest = ReadBounded(cached, "manifest"); signature = ReadBounded(cached, "signature");
            }
            else (manifest, signature) = await source.ReadReleaseAsync(version, token).ConfigureAwait(false);
            using var cachedLease = cached;
            string digest = Convert.ToHexStringLower(SHA256.HashData(manifest));
            string format = baseline.RuntimeId.StartsWith("win-", StringComparison.Ordinal) ? "portable.zip" : "portable.tar.gz";
            var identity = baseline with { Version = installed };
            VerifiedReleasePackage release = resumeDigest is null
                ? await VerifiedReleasePackage.VerifyAsync(manifest, signature, identity, channel, floor, format, verifier, token).ConfigureAwait(false)
                : await VerifiedReleasePackage.VerifyResumeAsync(manifest, signature, identity, channel, floor, format, verifier, resumeDigest, token).ConfigureAwait(false);
            if (release.Version != version || release.Size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新目标或下载预算不匹配。");
            string txName = ".nexa-tx-" + Guid.NewGuid().ToString("N");
            using IUpdateDirectory tx = installation.CreateDirectory(txName, publicRead: false);
            using (FileStream file = tx.CreateFile("manifest")) { file.Write(manifest); file.Flush(true); }
            using (FileStream file = tx.CreateFile("signature")) { file.Write(signature); file.Flush(true); }
            using FileStream package = tx.CreateFile("package");
            UpdateTransactionJournal.Append(pending, version, channel, txName, digest, installed, previousSlot, highest == version ? "accepted" : "receiving");
            Publish("downloading");
            Stream? cachedPackage = cached?.OpenRead("package");
            if (cachedPackage is not null && cachedPackage.Length != release.Size) { cachedPackage.Dispose(); cachedPackage = null; }
            using (Stream input = cachedPackage ?? await source.OpenPackageAsync(version, release.Name, token).ConfigureAwait(false))
                await release.CopyVerifiedPackageAsync(input, package, token).ConfigureAwait(false);
            package.Flush(true); tx.Flush();
            FaultBoundary?.Invoke("received");
            Publish("preparing");
            string slotName = ".nexa-slot-" + Guid.NewGuid().ToString("N");
            using IUpdateDirectory slot = installation.CreateDirectory(slotName, publicRead: true);
            await ProtectedUpdateExtractor.ExtractAsync(package, baseline.RuntimeId, slot, token).ConfigureAwait(false);
            UpdateTransactionJournal.Append(pending, version, channel, txName, digest, installed, previousSlot, "prepared");
            installation.Flush();
            FaultBoundary?.Invoke("prepared");
            token.ThrowIfCancellationRequested();
            // The short durable activation commit is not interrupted by cancellation.
            if (highest != version) { UpdateHighWaterJournal.Append(highWater, version); highWater.Flush(true); }
            FaultBoundary?.Invoke("high-water");
            UpdateTransactionJournal.Append(pending, version, channel, txName, digest, installed, previousSlot, "accepted");
            FaultBoundary?.Invoke("accepted");
            Publish("activating");
            UpdateTransactionJournal.Append(active, version, slotName, installed, previousSlot);
            installation.Flush();
            FaultBoundary?.Invoke("activated");
            UpdateTransactionJournal.Append(pending, version, channel, txName, digest, installed, previousSlot, "complete");
            Publish("complete");
            return slotName;
        }
        catch (OperationCanceledException) { Publish("paused"); throw; }
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        { Publish("failed"); throw; }

        void Publish(string phase) => UpdateTransactionJournal.Append(status, version, channel, phase);
    }

    private static byte[] ReadBounded(IUpdateDirectory directory, string name)
    {
        using FileStream stream = directory.OpenRead(name);
        if (stream.Length is 0 or > 1024 * 1024) throw new InvalidDataException("缓存发布封套超限。");
        byte[] bytes = new byte[stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    public void Rollback()
    {
        using FileStream exclusive = installation.OpenState("update-transaction.lock");
        using FileStream active = installation.OpenState(ActivationName, publicRead: true, exclusive: false);
        string[]? current = UpdateTransactionJournal.Read(active);
        if (current is not { Length: 4 } || string.IsNullOrEmpty(current[2])) throw new InvalidOperationException("没有可回滚的更新。");
        if (current[3].Length != 0) { using var old = installation.OpenDirectory(current[3]); }
        using (FileStream pending = installation.OpenState(PendingName))
            UpdateTransactionJournal.Append(pending, current[0], "", "", "", "", "", "rolledback");
        UpdateTransactionJournal.Append(active, current[2], current[3], "", "");
        installation.Flush();
    }
}
