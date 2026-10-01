using Nexa.Services.Minecraft.Install;
using Nexa.Services.Tasks;
namespace Nexa.Services.Resources;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Mutex remains alive for outstanding operations and has no wait handle.")]
public sealed class ResourceModInstallService(IResourceCatalogSource catalog, ResourceInstanceService instances,
    ResourceDownloadService downloads, MinecraftLocalJarService jars, TaskCenterService tasks)
{
    private readonly SemaphoreSlim _gate = new(1);
    public async Task InstallAsync(ResourceModInstallCommand command, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { await InstallCoreAsync(command, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task InstallCoreAsync(ResourceModInstallCommand command, CancellationToken token)
    {
        string? staging = null;
        int imported = 0;
        using var task = tasks.Begin(new("resource-install." + Guid.NewGuid().ToString("N"), "安装模组与依赖", ["解析依赖", "下载", "安装"]));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, task.CancellationToken);
        token = linked.Token;
        try
        {
            var context = await instances.ReadAsync(command.Instance, token).ConfigureAwait(false);
            var plan = await new ResourceDependencyPlanner(catalog).PlanAsync(command, context, token).ConfigureAwait(false);
            if (plan.Count == 0) { task.Complete("模组及必需依赖已安装。"); return; }
            staging = Path.Combine(Path.GetFullPath(command.Instance.Root), ".nexa-resource-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var artifacts = new List<LocalJarArtifact>();
            for (int i = 0; i < plan.Count; i++)
            {
                var version = plan[i];
                string directory = Path.Combine(staging, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(directory);
                task.Report("下载", version.Name, (double)i / plan.Count, i, plan.Count, 0);
                await downloads.DownloadAsync(new(version.Provider, version.ProjectId, version.Id, directory, command.Instance.MirrorFirst), token).ConfigureAwait(false);
                artifacts.Add(await MinecraftLocalJarService.InspectAsync(Path.Combine(directory, version.File!.Name), token).ConfigureAwait(false));
            }
            string validation = Path.Combine(staging, "validation");
            Directory.CreateDirectory(Path.Combine(validation, "mods"));
            try
            {
                for (int i = 0; i < artifacts.Count; i++) File.Copy(artifacts[i].Path, Path.Combine(validation, "mods", i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jar"));
                var added = await Nexa.Services.Minecraft.Process.LaunchModInventoryReader.ReadAsync(validation, token).ConfigureAwait(false);
                var existing = await Nexa.Services.Minecraft.Process.LaunchModInventoryReader.ReadAsync(context.GameDirectory, token).ConfigureAwait(false);
                ResourceDependencyVerifier.Verify(added, existing);
            }
            finally
            {
                foreach (string path in Directory.EnumerateFiles(Path.Combine(validation, "mods"))) File.Delete(path);
                Directory.Delete(Path.Combine(validation, "mods")); Directory.Delete(validation);
            }
            foreach (var artifact in artifacts)
            {
                task.Report("安装", Path.GetFileName(artifact.Path), (double)imported / plan.Count, imported, plan.Count, 0);
                var result = await jars.ImportAsync(new(artifact, command.Instance.Root, command.Instance.InstanceId, LocalJarAction.Mod), token).ConfigureAwait(false);
                if (!result.IsSuccess) throw new IOException(result.Error?.Message ?? "无法添加模组。");
                imported++;
            }
            task.Complete($"已安装 {imported} 个模组（含必需依赖）。");
        }
        catch (OperationCanceledException) { task.Canceled(); throw; }
        catch (Exception e) when (e is not OutOfMemoryException and not AccessViolationException)
        {
            string message = imported == 0 ? e.Message : $"已添加 {imported} 个依赖，主模组安装未完成。{e.Message}";
            task.Fail(message); throw new IOException(message, e);
        }
        finally
        {
            if (staging is not null)
            {
                // Delete only files created by this operation; never traverse links recursively.
                try
                {
                    if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) == 0)
                    {
                        foreach (string directory in Directory.EnumerateDirectories(staging))
                        {
                            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                            foreach (string path in Directory.EnumerateFiles(directory)) File.Delete(path);
                            Directory.Delete(directory);
                        }
                        Directory.Delete(staging);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
