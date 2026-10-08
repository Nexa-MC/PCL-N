



using Nexa.Services.Tasks;


namespace Nexa.Services.Minecraft.Install;

public sealed partial class MinecraftInstallService
{
    private async Task<MinecraftInstallResult> ReinstallAsync(MinecraftInstallCommand command, ITaskCenterTask task, CancellationToken token, string? resumeStage = null, InstallExecution? execution = null)
    {
        string instance = command.InstanceName ?? throw new InvalidDataException("未指定要修改的版本。");
        var original = await MinecraftInstallEditService.ReadAsync(new(command.RootDirectory, instance), token).ConfigureAwait(false);
        if (original.GameVersion != command.GameVersion || original.Fingerprint != command.EditFingerprint)
            throw new InvalidDataException("原版本已更改，请重新打开修改页。Minecraft 本体版本不能更改。");
        var selected = new List<InstallBuildSelection>();
        if (command.Loader is { } primary) selected.Add(new(primary, command.LoaderBuild!));
        selected.AddRange((command.Addons ?? []).Select(addon => new InstallBuildSelection(addon.Kind, addon.Version)));
        var editPlan = MinecraftInstallEditPlanner.Evaluate(new(original, selected));
        if (command.ForceReinstall) editPlan = new(MinecraftInstallEditKind.Reinstall,
            selected.Select(item => item.Loader).ToArray(), "修复版本");
        bool renaming = command.NewInstanceName is not null && command.NewInstanceName != instance;
        if (editPlan.Kind == MinecraftInstallEditKind.Unchanged && !renaming)
        {
            if (resumeStage is null && (command.NewInstanceName is null || command.NewInstanceName == instance)) task.Complete("没有需要修改的选项");
            return new(instance, Path.Combine(original.RootDirectory, "versions", instance));
        }
        string stage = resumeStage ?? Nexa.Core.PathIdentity.Contained(original.RootDirectory, ".nexa-modify/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool safeToRemove = false, completed = false;
        FileStream? executionLease = null;
        try
        {
            if (resumeStage is null)
            {
                string taskDirectory = Path.Combine(stage, InstallTaskJournal.DirectoryName);
                Directory.CreateDirectory(taskDirectory);
                string executionPath = Path.Combine(taskDirectory, "execution.lock"); Management.RecoveryBlobStore.CheckLinks(executionPath);
                executionLease = new FileStream(executionPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _ = await InstallTaskJournal.CreateAsync(stage, command with { RootDirectory = original.RootDirectory }, token).ConfigureAwait(false);
            }
            ReadyExecution(execution, stage);
            if (resumeStage is not null) await InstallRecoveredScratch.ResetAsync(stage, token).ConfigureAwait(false);
            if (editPlan.Kind == MinecraftInstallEditKind.Unchanged)
                return new(instance, Path.Combine(original.RootDirectory, "versions", instance));
            if (editPlan.Kind == MinecraftInstallEditKind.ComponentsOnly)
                await PrepareComponentEditAsync(command, original, editPlan, stage, task, token).ConfigureAwait(false);
            else
                await RunAsync(command with
                {
                    RootDirectory = stage,
                    EditFingerprint = null,
                    ReuseRoot = original.RootDirectory,
                    PreparingEdit = true,
                    ModsRelativeDirectory = original.ModsRelativeDirectory
                }, task, token, new PersistentInstallMetadataSource(stage, _metadata)).ConfigureAwait(false);
            string relativeManifest = $"versions/{instance}/{instance}.json";
            string targetManifest = Nexa.Core.PathIdentity.Contained(original.RootDirectory, relativeManifest);
            var current = await MinecraftInstallEditService.ReadAsync(new(original.RootDirectory, instance), token).ConfigureAwait(false);
            if (current.Fingerprint != original.Fingerprint) throw new InvalidDataException("安装期间原版本已更改，请重试。");
            string[] generatedFiles = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/'))
                .Where(relative => !relative.StartsWith(InstallTaskJournal.DirectoryName + "/", StringComparison.Ordinal)
                    && !relative.StartsWith(".nexa-install/", StringComparison.Ordinal)
                    && !relative.Split('/').Any(segment => segment is ".nexa-java-jobs" or ".nexa-java.lock"))
                .Where(relative => relative == relativeManifest || !(relative.StartsWith("versions/", StringComparison.Ordinal)
                    && relative.EndsWith(".json", StringComparison.Ordinal) && File.Exists(Nexa.Core.PathIdentity.Contained(original.RootDirectory, relative))))
                .ToArray();
            var removals = original.ManagedMods.Where(mod => editPlan.Kind != MinecraftInstallEditKind.ComponentsOnly
                    || mod.Loader is { } kind && editPlan.ChangedLoaders.Contains(kind))
                .ToDictionary(mod => mod.Path.Replace('\\', '/'), mod => mod.Sha256, Nexa.Core.PathIdentity.Comparer);
            if (removals.Keys.Any(path => !path.StartsWith(original.ModsRelativeDirectory + "/", StringComparison.Ordinal)))
                throw new InvalidDataException("受管理 Mod 的路径无效。");
            var publication = await InstallPublicationJournal.PrepareAsync(original.RootDirectory, stage, instance, generatedFiles, removals, token).ConfigureAwait(false);
            try { await publication.ApplyAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
            {
                if (resumeStage is not null || _pauseForExit) throw; // Recovery keeps its progress for the next attempt or explicit rollback.
                // Cancellation compensates too. A failed compensation retains the durable stage.
                try { await publication.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception rollbackError) when (rollbackError is not OutOfMemoryException and not AccessViolationException)
                { throw new AggregateException("安装发布及回滚未完成，已保留事务备份。", error, rollbackError); }
                safeToRemove = true;
                throw;
            }
            // Keep committed evidence if persisting the terminal receipt fails.
            safeToRemove = false;
            if (resumeStage is null && (command.NewInstanceName is null || command.NewInstanceName == instance))
            {
                var saved = await InstallTaskJournal.ReadAsync(original.RootDirectory, stage, CancellationToken.None).ConfigureAwait(false);
                await InstallTaskJournal.WriteStatusAsync(stage, saved, InstallTaskStatus.Completed, CancellationToken.None).ConfigureAwait(false);
                completed = true;
                task.Complete($"已修改 {instance}");
                Installed?.Invoke(original.RootDirectory);
            }
            return new(instance, Path.GetDirectoryName(targetManifest)!);
        }
        finally
        {
            executionLease?.Dispose();
            if (completed) InstallTaskCleanup.TryPrune(stage, ".task/plan.json", ".task/status.json", ".task/execution.lock");
            // An interrupted/failed publication owns the only durable originals. Keep it until resolved.
            if (!completed && !renaming && resumeStage is null && (safeToRemove || !_pauseForExit && !File.Exists(Path.Combine(stage, ".publication", "progress.json"))))
            {
                Management.RecoveryBlobStore.CheckLinks(stage);
                try { Directory.Delete(stage, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
