using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexa.Services.Minecraft.Management;

public static partial class InstanceContentUpdateTransaction
{
    internal sealed record IntegrityBaseline(InstanceContentBaselineState State, string? Sha256 = null, Guid? TransactionId = null);

    internal static async Task<IntegrityBaseline> ReadIntegrityBaselineAsync(InstanceManagementSnapshot snapshot, InstanceContentIntegrityQuery query, CancellationToken token)
    {
        try { return await ReadIntegrityBaselineCoreAsync(snapshot, query, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException)
        { return new(InstanceContentBaselineState.Incomplete); }
    }

    private static async Task<IntegrityBaseline> ReadIntegrityBaselineCoreAsync(InstanceManagementSnapshot snapshot, InstanceContentIntegrityQuery query, CancellationToken token)
    {
        string root = Path.Combine(snapshot.GameDirectory, Folder); RecoveryBlobStore.CheckLinks(root);
        if (!Directory.Exists(root)) return new(InstanceContentBaselineState.Missing);
        List<string> directories = []; long bytesRead = 0; bool incomplete = false;
        foreach (string directory in Directory.EnumerateDirectories(root))
        { token.ThrowIfCancellationRequested(); if (directories.Count == 1000) return new(InstanceContentBaselineState.Incomplete); directories.Add(directory); }
        List<(string Path, long Bytes, long Modified)> stamps = [];
        (long Stamp, string? Hash, Guid Id, bool Pending)? latest = null;
        foreach (string directory in directories)
        {
            token.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
            string path = Path.Combine(directory, "record.json");
            try
            {
                RecoveryBlobStore.CheckLinks(path); var info = new FileInfo(path);
                if (!info.Exists) { incomplete = true; continue; }
                if (info.Length is <= 0 or > 1024 * 1024 || (bytesRead = checked(bytesRead + info.Length)) > 8 * 1024 * 1024) return new(InstanceContentBaselineState.Incomplete);
                long size = info.Length, modified = info.LastWriteTimeUtc.Ticks; stamps.Add((path, size, modified));
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                byte[] bytes = new byte[(int)size]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                if (file.ReadByte() != -1) return new(InstanceContentBaselineState.Incomplete);
                var record = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject ?? throw new InvalidDataException("更新记录无效。");
                string? instance = record["instance"]?.GetValue<string>(), game = record["game"]?.GetValue<string>();
                if (instance is null || game is null || !Path.IsPathFullyQualified(instance) || !Path.IsPathFullyQualified(game)) throw new InvalidDataException("更新记录身份缺失。");
                if (instance != snapshot.InstanceDirectory || game != snapshot.GameDirectory) continue;
                ValidateRecord(record, snapshot);
                if (!DateTimeOffset.TryParse(record["created"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _)) throw new InvalidDataException("更新记录时间无效。");
                string phase = record["phase"]!.GetValue<string>(); bool pending = phase is "applying" or "rolling-back";
                foreach (var entry in (JsonArray)record["entries"]!)
                {
                    if (entry!["page"]!.GetValue<string>() != query.PageId) continue;
                    string name = entry[phase == "rolled-back" ? "original" : "target"]!.GetValue<string>();
                    if (!Nexa.Core.PathIdentity.Comparer.Equals(name, query.Name)
                        && !(pending && Nexa.Core.PathIdentity.Comparer.Equals(entry["original"]!.GetValue<string>(), query.Name))) continue;
                    string? hash = pending ? null : entry[phase == "rolled-back" ? "before" : "after"]!.GetValue<string>();
                    if (latest is not { } previous || modified > previous.Stamp) latest = (modified, hash, id, pending);
                    else if (modified == previous.Stamp && (previous.Hash != hash || previous.Id != id)) incomplete = true;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or FormatException)
            { incomplete = true; }
        }
        foreach (var (path, bytes, modified) in stamps)
        {
            token.ThrowIfCancellationRequested(); RecoveryBlobStore.CheckLinks(path); var info = new FileInfo(path);
            if (!info.Exists || info.Length != bytes || info.LastWriteTimeUtc.Ticks != modified) incomplete = true;
        }
        var current = Directory.EnumerateDirectories(root).Take(1001).ToArray();
        if (current.Length != directories.Count || !current.ToHashSet(Nexa.Core.PathIdentity.Comparer).SetEquals(directories)) incomplete = true;
        if (incomplete || latest is { Pending: true }) return new(InstanceContentBaselineState.Incomplete);
        return latest is { } fact ? new(InstanceContentBaselineState.ManagedUpdate, fact.Hash, fact.Id) : new(InstanceContentBaselineState.Missing);
    }
}
