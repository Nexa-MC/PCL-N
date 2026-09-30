namespace Nexa.Services.Resources;

public sealed class ResourceDependencyPlanner(IResourceCatalogSource catalog)
{
    public async Task<IReadOnlyList<ResourceVersion>> PlanAsync(ResourceModInstallCommand command, ResourceInstanceContext context, CancellationToken token) =>
        (await PreviewAsync(command, context, token, resolveNames: false).ConfigureAwait(false)).Versions;
    public async Task<ResourceInstallPlan> PreviewAsync(ResourceModInstallCommand command, ResourceInstanceContext context, CancellationToken token, bool resolveNames = true)
    {
        if (string.IsNullOrEmpty(context.Game) || string.IsNullOrEmpty(context.Loader)) throw new InvalidDataException("请先选择已安装加载器的 Minecraft 版本。");
        var chosen = new Dictionary<ResourceReference, ResourceVersion>();
        var visiting = new HashSet<ResourceReference>();
        var incompatible = new List<(ResourceReference Source, string? Version)>();
        List<ResourceVersion> ordered = [];
        var optional = new Dictionary<ResourceReference, ResourceOptionalDependency>();
        bool hasCycles = false;
        async Task Visit(ResourceReference reference, string? pinned, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (chosen.TryGetValue(reference, out var already))
            {
                if (!string.IsNullOrEmpty(pinned) && already.Id != pinned) throw new InvalidDataException("多个模组要求不同的依赖版本。");
                if (visiting.Contains(reference)) hasCycles = true;
                return;
            }
            if (depth > 16 || chosen.Count >= 64) throw new InvalidDataException("模组依赖超出安装预算。");
            ResourceVersion? version;
            if (!string.IsNullOrEmpty(pinned))
            {
                version = catalog is IResourceFileSource files ? await files.ReadVersionAsync(new(reference.Provider, reference.ProjectId, pinned, "", command.Instance.MirrorFirst), token).ConfigureAwait(false) : null;
                if (version is null) throw new InvalidDataException("无法读取指定的依赖版本。");
                reference = new(version.Provider, version.ProjectId);
            }
            else
            {
                var detail = await catalog.DetailAsync(new(reference.ProjectId, context.Game, context.Loader) { Sources = [reference], MirrorFirst = command.Instance.MirrorFirst }, token).ConfigureAwait(false);
                version = detail.Versions.FirstOrDefault(v => v.Provider == reference.Provider && v.Games.Contains(context.Game) && v.Loaders.Contains(context.Loader, StringComparer.OrdinalIgnoreCase));
            }
            if (version is null || !version.Games.Contains(context.Game) || !version.Loaders.Contains(context.Loader, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("模组或其依赖没有适用于当前游戏版本和加载器的版本。");
            if (visiting.Contains(reference))
            { if (!string.IsNullOrEmpty(pinned) && chosen[reference].Id != pinned) throw new InvalidDataException("循环依赖要求不一致的版本。"); hasCycles = true; return; }
            if (chosen.TryGetValue(reference, out var previous))
            { if (!string.IsNullOrEmpty(pinned) && previous.Id != pinned) throw new InvalidDataException("多个模组要求不同的依赖版本。"); return; }
            var installed = context.Installed.Where(f => f.Source == reference).ToArray();
            bool present = false;
            if (installed.Length > 0)
            {
                present = installed.Any(f => f.Enabled && f.VersionId == version.Id);
                if (!present && string.IsNullOrEmpty(pinned) && catalog is IResourceFileSource fileSource)
                    foreach (var file in installed.Where(f => f.Enabled))
                    {
                        var existing = await fileSource.ReadVersionAsync(new(reference.Provider, reference.ProjectId, file.VersionId, "", command.Instance.MirrorFirst), token).ConfigureAwait(false);
                        if (existing is not null && existing.Games.Contains(context.Game) && existing.Loaders.Contains(context.Loader, StringComparer.OrdinalIgnoreCase)) { version = existing; present = true; break; }
                    }
                if (!present) throw new InvalidDataException("已有同一项目的其他版本或已禁用模组，请先在版本管理中处理。");
            }
            if (!present && (version.File is null || !version.File.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("模组依赖未开放直接下载。");
            if (!present && version.File!.Size is <= 0 or > 268435456) throw new InvalidDataException("模组文件大小超出安装预算。");
            if (version.Dependencies.Count > 256) throw new InvalidDataException("模组依赖数量过多。");
            chosen[reference] = version; visiting.Add(reference);
            foreach (var dependency in version.Dependencies)
            {
                var child = new ResourceReference(reference.Provider, dependency.ProjectId);
                if (dependency.Kind == "required")
                {
                    if (string.IsNullOrEmpty(child.ProjectId) && string.IsNullOrEmpty(dependency.VersionId)) throw new InvalidDataException("必需依赖没有可解析的项目或版本标识。");
                    await Visit(child, dependency.VersionId, depth + 1).ConfigureAwait(false);
                }
                else if (dependency.Kind == "optional")
                {
                    if (child.ProjectId.Length == 0 && !string.IsNullOrEmpty(dependency.VersionId) && catalog is IResourceFileSource optionalSource)
                    {
                        var childVersion = await optionalSource.ReadVersionAsync(new(reference.Provider, "", dependency.VersionId, "", command.Instance.MirrorFirst), token).ConfigureAwait(false);
                        if (childVersion is not null) child = new(childVersion.Provider, childVersion.ProjectId);
                    }
                    if (child.ProjectId.Length == 0) throw new InvalidDataException("可选依赖没有可解析的项目标识。");
                    if (optional.Count >= 64 && !optional.ContainsKey(child)) throw new InvalidDataException("可选依赖数量超出安装预算。");
                    optional.TryAdd(child, new(child, dependency.VersionId, child.ProjectId));
                    if (command.OptionalDependencies.Contains(child)) await Visit(child, dependency.VersionId, depth + 1).ConfigureAwait(false);
                }
                else if (dependency.Kind == "incompatible") incompatible.Add((child, dependency.VersionId));
            }
            visiting.Remove(reference); if (!present) ordered.Add(version);
        }
        await Visit(command.Source, command.VersionId, 0).ConfigureAwait(false);
        foreach (var conflict in incompatible)
            if (chosen.TryGetValue(conflict.Source, out var selected) && (string.IsNullOrEmpty(conflict.Version) || selected.Id == conflict.Version)
                || context.Installed.Any(f => f.Enabled && f.Source == conflict.Source && (string.IsNullOrEmpty(conflict.Version) || f.VersionId == conflict.Version)))
                throw new InvalidDataException("所选模组与已安装或即将安装的模组不兼容。");
        if (ordered.Sum(v => v.File!.Size) > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("模组依赖下载总量超出预算。");
        var options = optional.Values.ToArray();
        if (resolveNames)
        {
            using var slots = new SemaphoreSlim(4);
            options = await Task.WhenAll(options.Select(async option =>
            {
                await slots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var detail = await catalog.DetailAsync(new(option.Source.ProjectId, context.Game, context.Loader) { Sources = [option.Source], MirrorFirst = command.Instance.MirrorFirst }, token).ConfigureAwait(false);
                    return option with { Name = detail.Project.DisplayName };
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is IOException or InvalidDataException or HttpRequestException) { return option; }
                finally { slots.Release(); }
            })).ConfigureAwait(false);
        }
        return new(ordered, options, hasCycles);
    }
}
