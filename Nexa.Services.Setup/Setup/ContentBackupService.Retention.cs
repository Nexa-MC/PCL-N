using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Services.Logging;

namespace Nexa.Services.Setup;

public sealed partial class ContentBackupService
{
    public Func<int>? ReadConfiguredKeepCount { get; init; }

    public async Task<ContentBackupRetentionPolicy> ReadRetentionAsync(CancellationToken token = default)
    {
        await using var lease = await AcquireAsync(token).ConfigureAwait(false);
        string path = Path.Combine(_root, ".retention.json"); CheckLinks(path);
        if (!File.Exists(path)) return ApplyConfiguredRetention(new());
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (file.Length > 4096) throw new IOException("备份保留策略超过读取预算。");
        byte[] bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(bytes); var root = doc.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1) throw new IOException("备份保留策略版本不支持。");
        var policy = new ContentBackupRetentionPolicy(root.GetProperty("enabled").GetBoolean(), root.GetProperty("keepCount").GetInt32(), root.GetProperty("keepDays").GetInt32());
        ValidateRetention(policy.KeepCount, policy.KeepDays); return ApplyConfiguredRetention(policy);
    }

    public async Task SetRetentionAsync(ContentBackupRetentionPolicy policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(policy); ValidateRetention(policy.KeepCount, policy.KeepDays); EnsureConfiguredCount(policy.KeepCount);
        await using var lease = await AcquireAsync(token).ConfigureAwait(false); EnsureIdle();
        string path = Path.Combine(_root, ".retention.json"), stage = path + "." + Guid.NewGuid().ToString("N") + ".part"; CheckLinks(path);
        try
        {
            var json = new JsonObject { ["schema"] = 1, ["enabled"] = policy.Enabled, ["keepCount"] = policy.KeepCount, ["keepDays"] = policy.KeepDays };
            await using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(json, StorageJsonContext.Default.JsonObject), token).ConfigureAwait(false); await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true); }
            token.ThrowIfCancellationRequested(); EnsureIdle(); EnsureConfiguredCount(policy.KeepCount); CheckLinks(path); File.Move(stage, path, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    internal bool MaintenanceIdle => _isIdle();

    private ContentBackupRetentionPolicy ApplyConfiguredRetention(ContentBackupRetentionPolicy policy)
    {
        if (ReadConfiguredKeepCount is not { } read) return policy;
        int count = read(); ValidateRetention(count, policy.KeepDays); return policy with { KeepCount = count };
    }

    private void EnsureConfiguredCount(int count)
    {
        if (ReadConfiguredKeepCount is { } read && read() != count) throw new IOException("备份保留数量已变化，请重新预览。");
    }
}

/// <summary>Optional retention is admitted only while idle and never blocks launcher/UI startup.</summary>
public sealed class ContentBackupMaintenanceSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    public ContentBackupMaintenanceSession(ContentBackupService backups, LogService? log = null) => _run = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                if (!backups.MaintenanceIdle) continue;
                try
                {
                    var policy = await backups.ReadRetentionAsync(_stop.Token).ConfigureAwait(false);
                    if (policy.Enabled) await backups.PruneAutomaticallyAsync(policy.KeepCount, policy.KeepDays, _stop.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or FormatException)
                { log?.Warn("Storage", "自动备份清理未完成；请在存储页检查保留策略和清单。"); }
            } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    });
    public async ValueTask DisposeAsync() { await _stop.CancelAsync().ConfigureAwait(false); await _run.ConfigureAwait(false); _stop.Dispose(); }
}
