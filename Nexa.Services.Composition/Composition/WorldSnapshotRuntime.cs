using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Management;
using Nexa.Services.Setup;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Services.Composition;

public static class WorldSnapshotRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries, XsrStateStore store,
        ContentBackupService backups, Func<string, IDisposable>? acquireWorldSessionLock = null)
    {
        var service = new InstanceWorldSnapshotService(new WorldSnapshotCasStore(backups), store, acquireWorldSessionLock);
        commands.Register<InstanceWorldSnapshotCaptureCommand>(InstanceWorldSnapshotContract.Capture,
            async (command, token) => await Task.Run(() => service.CaptureAsync(command, token), token).ConfigureAwait(false));
        commands.Register<InstanceWorldSnapshotRestoreCommand>(InstanceWorldSnapshotContract.Restore,
            async (command, token) => await Task.Run(() => service.RestoreAsync(command, token), token).ConfigureAwait(false));
        queries.Register<InstanceWorldSnapshotListQuery, IReadOnlyList<InstanceWorldSnapshot>>(InstanceWorldSnapshotContract.List,
            (query, token) => QueryAsync(() => service.ListAsync(query, token), token));
        queries.Register<InstanceWorldSnapshotVerifyQuery, InstanceWorldSnapshotVerification>(InstanceWorldSnapshotContract.Verify,
            (query, token) => QueryAsync(() => service.VerifyAsync(query, token), token));
    }

    private static async ValueTask<XsrResult<T>> QueryAsync<T>(Func<Task<T>> action, CancellationToken token) where T : notnull
    {
        try { return XsrResult.Success(await Task.Run(action, token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure<T>(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return XsrResult.Failure<T>(MinecraftErrors.InvalidRequest("无法读取此世界的快照，请检查世界目录和权限。")); }
    }
}

internal sealed class WorldSnapshotCasStore(ContentBackupService backups) : IInstanceWorldSnapshotStore
{
    private const string Namespace = "world-snapshot-v1:";
    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.Ordinal) { "session.lock", ".nexa-world-readonly" };

    public async Task<IReadOnlyList<InstanceWorldSnapshot>> ListAsync(string scope, CancellationToken token)
    {
        string prefix = Prefix(scope);
        return Array.AsReadOnly((await backups.ListAsync(token).ConfigureAwait(false)).Where(x => x.Label.StartsWith(prefix, StringComparison.Ordinal))
            .Select(Summary).ToArray());
    }

    public async Task<InstanceWorldSnapshot> CaptureAsync(string scope, string worldDirectory, string displayName, CancellationToken token)
    {
        string prefix = Prefix(scope); string safeName = new(displayName.Where(c => !char.IsControl(c)).Take(128 - prefix.Length).ToArray());
        return Summary(await backups.CaptureAsync(worldDirectory, prefix + safeName, ExcludedFiles, token).ConfigureAwait(false));
    }

    public async Task<InstanceWorldSnapshotVerification> VerifyAsync(string scope, string identity, CancellationToken token)
    {
        await RequireScopeAsync(scope, identity, token).ConfigureAwait(false);
        var verified = await backups.VerifyOfflineAsync(identity, token).ConfigureAwait(false);
        return new(verified.BackupIdentity, verified.Files, verified.VerifiedFiles, verified.MissingOrCorruptPaths);
    }

    public async Task RestoreAsync(string scope, string identity, string destination, CancellationToken token)
    {
        await RequireScopeAsync(scope, identity, token).ConfigureAwait(false);
        await backups.RestoreAsync(identity, destination, token).ConfigureAwait(false);
    }

    private async Task RequireScopeAsync(string scope, string identity, CancellationToken token)
    {
        string prefix = Prefix(scope);
        var manifest = (await backups.ListAsync(token).ConfigureAwait(false)).SingleOrDefault(x => x.Identity == identity && x.Label.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new IOException("快照不属于当前世界。");
        if (!manifest.Files.Any(x => x.RelativePath == "level.dat")) throw new IOException("世界快照缺少 level.dat。");
        if (manifest.Files.Any(x => x.RelativePath is "session.lock" or ".nexa-world-readonly")) throw new IOException("世界快照不能包含会话控制文件。");
    }

    private static string Prefix(string scope)
    {
        if (scope.Length != 64 || scope.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("快照范围无效。");
        return Namespace + scope + ':';
    }

    private static InstanceWorldSnapshot Summary(ContentBackupManifest manifest) => new(manifest.Identity, manifest.CreatedAt, manifest.Files.Count, manifest.Files.Sum(x => x.Bytes));
}
