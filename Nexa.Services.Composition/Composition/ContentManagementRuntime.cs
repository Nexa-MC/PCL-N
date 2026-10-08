using Nexa.Services.Minecraft.Management;
using Nexa.Xsr;
using Nexa.Xsr.Runtime;
using Nexa.Xsr.State;

namespace Nexa.Services.Composition;

public static class ContentManagementRuntime
{
    public static void Register(XsrCommandRouterBuilder commands, XsrQueryRouterBuilder queries, XsrStateStore store,
        Func<string, CancellationToken, Task>? captureWorld = null, Func<string, IDisposable>? acquireWorldSessionLock = null)
    {
        queries.Register<InstanceContentIntegrityQuery, InstanceContentIntegrity>(InstanceContentIntegrityContract.Read,
            async (query, token) => XsrResult.Success(await InstanceContentIntegrityService.ReadAsync(query, token).ConfigureAwait(false)));
        commands.Register<InstanceContentUpdateRollbackCommand>(InstanceManagementContract.RollbackContentUpdate,
            async (command, token) => await InstanceContentUpdateTransaction.RollbackAsync(command, store, token).ConfigureAwait(false));
        commands.Register<InstanceWorldCopyCommand>(InstanceWorldContract.Copy,
            async (command, token) => await InstanceWorldService.CopyAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        if (captureWorld is not null) commands.Register<InstanceWorldBackupCommand>(InstanceWorldContract.Backup,
            async (command, token) => await InstanceWorldService.BackupAsync(command, store, captureWorld, acquireWorldSessionLock, token).ConfigureAwait(false));
        commands.Register<InstanceWorldLockCommand>(InstanceWorldContract.SetLock,
            async (command, token) => await InstanceWorldService.SetLockAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        commands.Register<InstanceWorldDataPackCommand>(InstanceWorldContract.SetDataPackEnabled,
            async (command, token) => await InstanceWorldService.SetDataPackEnabledAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        commands.Register<InstanceWorldDataPackImportCommand>(InstanceWorldContract.ImportDataPack,
            async (command, token) => await InstanceWorldService.ImportDataPackAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        commands.Register<InstanceWorldDataPackRemoveCommand>(InstanceWorldContract.RemoveDataPack,
            async (command, token) => await InstanceWorldService.RemoveDataPackAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        commands.Register<InstanceWorldDataPackRestoreCommand>(InstanceWorldContract.RestoreDataPack,
            async (command, token) => await InstanceWorldService.RestoreDataPackAsync(command, store, acquireWorldSessionLock, token).ConfigureAwait(false));
        queries.Register<InstanceWorldMetadataQuery, InstanceWorldMetadata>(InstanceWorldContract.Read,
            async (query, token) => XsrResult.Success(await InstanceWorldService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceWorldHealthQuery, InstanceWorldHealth>(InstanceWorldHealthContract.Read,
            async (query, token) => XsrResult.Success(await Task.Run(() => InstanceWorldService.ReadHealthAsync(query, store, acquireWorldSessionLock, token), token).ConfigureAwait(false)));
        queries.Register<InstanceScreenshotQuery, InstanceScreenshot>(InstanceScreenshotContract.Read,
            async (query, token) => XsrResult.Success(await InstanceScreenshotService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceFileListQuery, InstanceFileListing>(InstanceFileWorkspaceContract.List,
            async (query, token) => XsrResult.Success(await InstanceFileWorkspaceService.ListAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceFileReadQuery, InstanceFileDocument>(InstanceFileWorkspaceContract.Read,
            async (query, token) => XsrResult.Success(await InstanceFileWorkspaceService.ReadAsync(query, token).ConfigureAwait(false)));
        queries.Register<InstanceFileSavePreviewQuery, InstanceFileSavePreview>(InstanceFileWorkspaceContract.Preview,
            async (query, token) => XsrResult.Success(await InstanceFileWorkspaceService.PreviewAsync(query, token).ConfigureAwait(false)));
        commands.Register<InstanceFileSaveCommand>(InstanceFileWorkspaceContract.Save,
            async (command, token) => await InstanceFileWorkspaceService.SaveAsync(command, store, token).ConfigureAwait(false));
    }
}
