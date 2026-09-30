using Nexa.Services.Minecraft;
using Nexa.Services.Minecraft.Install;

namespace Nexa.Services.Resources;

public sealed class ResourceContentOnlineService(ResourceInstanceService instances, IResourceCatalogSource catalog, ResourceTranslationService? translations = null)
{
    public Task<ResourceContentOnline> ReadAsync(ResourceContentOnlineQuery query, CancellationToken token) => Task.Run(async () =>
    {
        ResourceKind kind = query.PageId switch
        {
            "mods" => ResourceKind.Mod,
            "resourcepacks" => ResourceKind.ResourcePack,
            "shaderpacks" => ResourceKind.Shader,
            _ => throw new InvalidDataException("此内容不支持在线识别。")
        };
        if (!Path.IsPathFullyQualified(query.InstanceDirectory) || !MinecraftVersionPaths.IsSafeReference(query.Name))
            throw new InvalidDataException("请选择有效的资源文件。");
        string instance = Path.TrimEndingDirectorySeparator(Path.GetFullPath(query.InstanceDirectory));
        var versions = Directory.GetParent(instance);
        if (versions?.Name != "versions" || versions.Parent is null || !MinecraftVersionPaths.IsSafeReference(Path.GetFileName(instance)))
            throw new InvalidDataException("请选择有效的版本目录。");
        CheckLinks(instance);
        var metadata = await new MinecraftInstanceMetadataStore().LoadAsync(instance, token).ConfigureAwait(false);
        string game = metadata.InstanceIsolation ? instance : versions.Parent.FullName;
        var file = new FileInfo(Path.Combine(game, query.PageId, query.Name));
        CheckLinks(file.FullName);
        CheckIdentity(file, query);
        if (file.Length > 512L * 1024 * 1024) return new ResourceContentOnline(null, null, [], "文件较大，暂不进行在线识别。");
        var fingerprint = await instances.ReadFingerprintAsync(file, token).ConfigureAwait(false);
        if (fingerprint is null) throw new IOException("文件已变化，请刷新后重试。");
        var identified = await instances.IdentifyAsync(fingerprint, query.MirrorFirst, token).ConfigureAwait(false);
        if (identified.Files.Length == 0) return new ResourceContentOnline(null, null, [], identified.Complete ? "模组站尚未收录此文件，已保留本地信息。" : "暂时无法连接模组站，已保留本地信息。");
        var edit = await MinecraftInstallEditService.ReadAsync(new(versions.Parent.FullName, Path.GetFileName(instance)), token).ConfigureAwait(false);
        string loader = kind == ResourceKind.Mod ? edit.Selection.Select(selection => selection.Loader switch
        { InstallLoader.Fabric or InstallLoader.LegacyFabric => "fabric", InstallLoader.Quilt => "quilt", InstallLoader.Forge or InstallLoader.Cleanroom => "forge", InstallLoader.NeoForge => "neoforge", _ => "" }).FirstOrDefault(value => value.Length > 0) ?? "" : "";
        var sources = identified.Files.Select(match => match.Source).Distinct().ToArray();
        var detail = await catalog.DetailAsync(new(sources[0].ProjectId, edit.GameVersion, loader) { Sources = sources, MirrorFirst = query.MirrorFirst }, token).ConfigureAwait(false);
        var project = detail.Project with { Kind = kind };
        if (translations is not null && string.IsNullOrWhiteSpace(project.ChineseDescription))
        {
            var translated = await translations.ReadAsync(new(sources[0], project.Description), token).ConfigureAwait(false);
            project = project with { ChineseDescription = translated.Description };
        }
        var installed = detail.Versions.FirstOrDefault(version => identified.Files.Any(match => match.VersionId == version.Id && match.Source.Provider == version.Provider));
        string installedName = installed?.Number ?? "已识别，版本信息暂不可用";
        if (installed is null && catalog is IResourceFileSource files)
        {
            var match = identified.Files[0];
            installedName = (await files.ReadVersionAsync(new(match.Source.Provider, match.Source.ProjectId, match.VersionId, "", query.MirrorFirst), token).ConfigureAwait(false))?.Number ?? "已识别，版本信息暂不可用";
        }
        file.Refresh(); CheckIdentity(file, query); CheckLinks(file.FullName);
        return new ResourceContentOnline(project, installedName, detail.Versions, detail.Notice ?? (identified.Complete ? null : "部分来源暂时无法连接。"));
    }, token);

    private static void CheckIdentity(FileInfo file, ResourceContentOnlineQuery query)
    {
        if (!file.Exists || file.Length != query.ExpectedSize || file.LastWriteTimeUtc.Ticks != query.ExpectedModifiedUtcTicks)
            throw new IOException("文件已变化，请刷新后重试。");
    }
    private static void CheckLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能识别链接目录中的资源。");
    }
}
