using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Management;
using Nexa.Xsr.State;

namespace Nexa.Services.Resources;

public sealed class ResourceContentUpdateService(ResourceContentOnlineService content, ResourceDownloadService downloads, XsrStateStore store)
{
    public async Task UpdateAsync(ResourceContentUpdateCommand command, CancellationToken token)
    {
        var query = command.File;
        if (query.PageId is not ("resourcepacks" or "shaderpacks")) throw new InvalidDataException("此更新入口只支持资源包和光影包。");
        var online = await content.ReadAsync(query, token).ConfigureAwait(false);
        if (online.Project is not { } project || !project.Sources.Contains(command.Source)
            || !online.Versions.Any(v => v.Id == command.VersionId && v.Provider == command.Source.Provider && v.ProjectId == command.Source.ProjectId && v.File is not null))
            throw new InvalidDataException("目标版本不属于当前文件或不兼容当前 Minecraft。");
        var snapshot = await InstanceManagementService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        string stage = Path.Combine(snapshot.GameDirectory, ".nexa-resource-" + Guid.NewGuid().ToString("N"));
        for (string? path = snapshot.GameDirectory; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能更新链接目录中的资源。");
        Directory.CreateDirectory(stage);
        try
        {
            await downloads.DownloadAsync(new(command.Source.Provider, command.Source.ProjectId, command.VersionId, stage, query.MirrorFirst), token).ConfigureAwait(false);
            string downloaded = Directory.EnumerateFiles(stage).Single();
            // Download metadata is re-fetched by DownloadService. It chooses the published filename;
            // the UI cannot supply an arbitrary local target or replace a running game's directory.
            var result = await InstanceContentTrash.ReplacePackAsync(new(query.InstanceDirectory, query.PageId, query.Name, false,
                query.ExpectedSize, query.ExpectedModifiedUtcTicks), downloaded, Path.GetFileName(downloaded), store, token).ConfigureAwait(false);
            if (!result.IsSuccess) throw new IOException(result.Error?.Message ?? "资源更新未完成。");
        }
        finally
        {
            if (Directory.Exists(stage) && (File.GetAttributes(stage) & FileAttributes.ReparsePoint) == 0)
            {
                foreach (string path in Directory.EnumerateFiles(stage)) File.Delete(path);
                Directory.Delete(stage);
            }
        }
    }
}
