using System.Text.Json.Nodes;
using Nexa.Services.Files;
using Nexa.Services.Tasks;
using Nexa.Xsr;

namespace Nexa.Services.Setup;

public sealed partial class StoragePreferencesService
{
    private string CleanupRecord => _locator + ".cleanup.json";
    private sealed record CleanupFacts(IReadOnlyList<FileFact> Files, IReadOnlyList<TaskCenterEntry> Tasks, string Revision);

    public async Task<XsrResult<StorageCleanupPreview>> PreviewCleanupAsync(StorageCleanupQuery query, CancellationToken token = default)
    {
        try
        {
            CleanupFacts facts = await ReadCleanupAsync(query.Kind, token).ConfigureAwait(false);
            var entries = query.Kind == StorageCleanupKind.TemporaryFiles
                ? facts.Files.Select(file => new StorageCleanupEntry(file.RelativePath, file.Bytes)).ToArray()
                : facts.Tasks.Select(entry => new StorageCleanupEntry(entry.Title, 0)).ToArray();
            return XsrResult.Success(new StorageCleanupPreview(query.Kind, facts.Revision, entries));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure<StorageCleanupPreview>(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure<StorageCleanupPreview>(Failure(error)); }
    }

    public async Task<XsrResult> CleanupAsync(StorageCleanupCommand command, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureIdle();
            await RecoverCleanupAsync(_root, _locator, token).ConfigureAwait(false);
            CleanupFacts facts = await ReadCleanupAsync(command.Kind, token).ConfigureAwait(false);
            if (facts.Revision != command.ExpectedRevision) throw new IOException("清理范围已变化，请重新预览。");
            token.ThrowIfCancellationRequested(); EnsureIdle();
            if (command.Kind == StorageCleanupKind.FinishedTasks)
            {
                if (tasks?.DismissFinished(facts.Tasks) != true) throw new IOException("任务列表已变化，请重新预览。");
                return XsrResult.Success();
            }
            if (facts.Files.Count == 0) return XsrResult.Success();
            string transaction = Guid.NewGuid().ToString("N"), stage = CleanupStage(_root, transaction);
            var record = new JsonObject
            {
                ["version"] = 1,
                ["transaction"] = transaction,
                ["source"] = _root,
                ["phase"] = "prepared",
                ["files"] = new JsonArray(facts.Files.Select(file => (JsonNode)new JsonObject
                { ["path"] = file.RelativePath, ["bytes"] = file.Bytes, ["sha256"] = file.Hash }).ToArray())
            };
            await WriteRecordAsync(CleanupRecord, record, token, overwrite: false, maximumBytes: 32 * 1024 * 1024).ConfigureAwait(false);
            bool committed = false;
            try
            {
                CreateRestrictedDirectory(stage); CheckLinks(stage);
                WriteOwner(stage, transaction);
                foreach (FileFact file in facts.Files)
                {
                    token.ThrowIfCancellationRequested(); EnsureIdle();
                    string source = Path.Combine(_root, file.RelativePath), target = Path.Combine(stage, file.RelativePath);
                    if (!EligibleTemporary(_root, source, DateTime.UtcNow) || await FactAsync(_root, source, token).ConfigureAwait(false) != file)
                        throw new IOException("临时文件在预览后发生变化，清理已取消。");
                    // Refuse a file still leased by its writer. The stream closes immediately before its rename.
                    using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    CreateRestrictedDirectory(Path.GetDirectoryName(target)!); CheckLinks(target); File.Move(source, target, overwrite: false);
                }
                token.ThrowIfCancellationRequested(); EnsureIdle();
                record["phase"] = "committed";
                await WriteRecordAsync(CleanupRecord, record, token, maximumBytes: 32 * 1024 * 1024).ConfigureAwait(false); committed = true;
                try { DeleteOwned(stage, transaction); File.Delete(CleanupRecord); }
                catch (Exception error) when (IsExpected(error)) { /* Committed cleanup deallocation retries at startup. */ }
                return XsrResult.Success();
            }
            finally
            {
                if (!committed) await RecoverCleanupAsync(_root, _locator, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (IsExpected(error)) { return XsrResult.Failure(Failure(error)); }
        finally { _gate.Release(); }
    }

    private async Task<CleanupFacts> ReadCleanupAsync(StorageCleanupKind kind, CancellationToken token)
    {
        if (kind == StorageCleanupKind.FinishedTasks)
        {
            if (tasks is null) throw new IOException("任务历史不可用。");
            var entries = tasks.ReadEntries().Where(entry => entry.State == TaskCenterEntryState.Finished)
                .OrderBy(entry => entry.TaskId, StringComparer.Ordinal).ToArray();
            return new([], entries, Hash(string.Join('\n', entries.Select(entry => entry.TaskId + "\0" + entry.Title + "\0" + entry.Stage + "\0" + entry.Detail))));
        }
        if (kind != StorageCleanupKind.TemporaryFiles) throw new InvalidDataException("清理类型无效。");
        CheckLinks(_root);
        var files = new List<FileFact>(); DateTime now = DateTime.UtcNow;
        foreach (string name in new[] { FolderNames.Settings, FolderNames.Profiles, FolderNames.Cache })
        {
            string folder = Path.Combine(_root, name); CheckLinks(folder);
            if (!Directory.Exists(folder)) continue;
            var pending = new Stack<string>(); pending.Push(folder);
            int inspected = 0;
            while (pending.TryPop(out string? directory))
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); CheckLinks(entry);
                    if (++inspected > MaxFiles) throw new IOException("清理目录超过条目限制。");
                    if (Directory.Exists(entry))
                    {
                        // Recovery, task and durable cache directories are not cleanup traversal roots.
                        if (name == FolderNames.Cache && Path.GetFileName(entry) == "scratch") pending.Push(entry);
                        continue;
                    }
                    if (EligibleTemporary(_root, entry, now))
                    {
                        if (files.Count >= MaxFiles) throw new IOException("清理候选超过条目限制。");
                        files.Add(await FactAsync(_root, entry, token).ConfigureAwait(false));
                    }
                }
        }
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        return new(files, [], Hash(string.Join('\n', files.Select(file => file.RelativePath + "\0" + file.Bytes + "\0" + file.Hash))));
    }

    private static bool EligibleTemporary(string root, string path, DateTime now)
    {
        CheckLinks(path);
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return File.Exists(path) && File.GetLastWriteTimeUtc(path) <= now - TimeSpan.FromHours(24) && IsTemporaryRelativePath(relative);
    }

    private static bool IsTemporaryRelativePath(string relative)
    {
        string[] parts = relative.Split('/');
        bool allowedFolder = parts.Length == 2 && parts[0] is FolderNames.Settings or FolderNames.Profiles or FolderNames.Cache
            || parts.Length == 3 && parts[0] == FolderNames.Cache && parts[1] == "scratch";
        if (!allowedFolder) return false;
        string name = parts[^1];
        if (parts[0] == FolderNames.Settings && name.StartsWith(".settings.json.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal))
            return Guid.TryParseExact(name[15..^4], "N", out _);
        int marker = name.LastIndexOf(".tmp-", StringComparison.Ordinal);
        return marker > 0 && Guid.TryParseExact(name[(marker + 5)..], "N", out _);
    }

    private static string CleanupStage(string root, string transaction) => Path.Combine(root, ".nexa-storage-cleanup-" + transaction);
    private static async Task RecoverCleanupAsync(string root, string locator, CancellationToken token)
    {
        string path = locator + ".cleanup.json"; CheckLinks(path);
        if (!File.Exists(path)) return;
        JsonObject record = await ReadRecordAsync(path, token, 32 * 1024 * 1024).ConfigureAwait(false);
        string transaction = ReadTransaction(record), stage = CleanupStage(root, transaction);
        if (!Same(root, Normalize(record["source"]!.GetValue<string>())) || record["files"] is not JsonArray files || files.Count > MaxFiles)
            throw new InvalidDataException("清理恢复记录不属于当前数据位置。");
        string phase = record["phase"]!.GetValue<string>();
        if (phase is not ("prepared" or "committed")) throw new InvalidDataException("清理恢复阶段无效。");
        CheckLinks(stage);
        if (!Directory.Exists(stage)) { File.Delete(path); return; }
        if (!IsOwned(stage, transaction))
        {
            if (TryRemoveUnmarkedEmptyStage(stage)) { File.Delete(path); return; }
            throw new IOException("清理暂存目录身份无效，已保留所有文件。");
        }
        if (phase == "prepared")
        {
            HashSet<string> seen = new(Nexa.Core.PathIdentity.Comparer);
            foreach (JsonNode? entry in files)
            {
                token.ThrowIfCancellationRequested();
                string relative = entry!["path"]!.GetValue<string>();
                string source = Nexa.Core.PathIdentity.Contained(stage, relative), target = Nexa.Core.PathIdentity.Contained(root, relative);
                CheckLinks(source); CheckLinks(target);
                // Validate the same narrow naming scope independently of a user-editable journal.
                if (!seen.Add(target) || !IsTemporaryRelativePath(relative.Replace(Path.DirectorySeparatorChar, '/')))
                    throw new InvalidDataException("清理恢复范围无效。");
                if (!File.Exists(source)) continue;
                FileFact fact = await FactAsync(stage, source, token).ConfigureAwait(false);
                if (fact.Bytes != entry["bytes"]!.GetValue<long>() || fact.Hash != entry["sha256"]!.GetValue<string>()) throw new IOException("清理暂存文件已变化，已保留记录。");
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("清理回滚目标已被替换，已保留原始临时文件。");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(source, target, overwrite: false);
            }
        }
        DeleteOwned(stage, transaction); File.Delete(path);
    }
}
