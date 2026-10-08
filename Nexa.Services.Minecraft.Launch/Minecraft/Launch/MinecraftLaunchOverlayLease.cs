using System.Text.Json;
using System.Text.Json.Nodes;
using Nexa.Platform;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Owns four launch directories and a recoverable, same-volume rename journal.</summary>
public sealed class MinecraftLaunchOverlayLease : IAsyncDisposable
{
    private static readonly string[] Folders = ["mods", "resourcepacks", "shaderpacks", "config"];
    private readonly string _game, _workspace, _journal;
    private readonly FileStream _lease;
    private readonly IPlatformProcessIdentity _processes;
    private JsonObject? _state;
    private int _disposed;

    private MinecraftLaunchOverlayLease(string game, FileStream lease, IPlatformProcessIdentity processes)
    {
        _game = game; _lease = lease; _workspace = Path.Combine(game, ".nexacl-launch-overlay");
        _journal = Path.Combine(_workspace, "journal.json");
        _processes = processes;
    }

    public static ValueTask<MinecraftLaunchOverlayLease> AcquireAsync(string gameDirectory,
        MinecraftLaunchOverlay overlay, CancellationToken cancellationToken = default)
        => AcquireAsync(gameDirectory, overlay, null, cancellationToken);

    public static async ValueTask<MinecraftLaunchOverlayLease> AcquireAsync(string gameDirectory,
        MinecraftLaunchOverlay overlay, IPlatformProcessIdentity? processIdentity, CancellationToken cancellationToken = default)
    {
        string game = Path.GetFullPath(gameDirectory); RejectLinks(game);
        Directory.CreateDirectory(game);
        var processes = processIdentity ?? PlatformProcessIdentityFactory.Create();
        var lease = new MinecraftLaunchOverlayLease(game,
            await MinecraftGameDirectoryUseLease.AcquireExclusiveAsync(game, processes, cancellationToken).ConfigureAwait(false), processes);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await lease.RecoverAsync().ConfigureAwait(false);
            if (!overlay.IsRequired) return lease;
            string?[] sources = overlay.SafeLaunch ? [null, null, null, null]
                : [overlay.ModsSource, overlay.ResourcePacksSource, overlay.ShaderPacksSource, overlay.ConfigSource];
            foreach (string? source in sources)
            {
                if (source is null) continue;
                string full = Path.GetFullPath(source); RejectLinks(full);
                if (!Directory.Exists(full) || IsWithin(game, full) || IsWithin(full, game))
                    throw new InvalidDataException("An overlay source must exist outside the game directory and cannot contain it.");
            }
            Directory.CreateDirectory(lease._workspace);
            lease._state = new JsonObject { ["version"] = 1, ["game"] = game, ["directories"] = new JsonObject() };
            lease.WriteJournal();
            for (int index = 0; index < Folders.Length; index++)
            {
                if (!overlay.SafeLaunch && sources[index] is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                string name = Folders[index], target = Path.Combine(game, name), original = Path.Combine(lease._workspace, "original", name);
                RejectLinks(target);
                if (File.Exists(target)) throw new InvalidDataException("An overlay directory is occupied by a file: " + name);
                bool exists = Directory.Exists(target);
                ((JsonObject)lease._state["directories"]!)[name] = new JsonObject { ["hadOriginal"] = exists };
                lease.WriteJournal();
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (exists) Directory.Move(target, original);
                Directory.CreateDirectory(target);
                if (sources[index] is { } source)
                    await CopyAsync(Path.GetFullPath(source), target, cancellationToken).ConfigureAwait(false);
            }
            return lease;
        }
        catch
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void BindProcess(System.Diagnostics.Process process)
    {
        if (_state is null) return;
        var observation = _processes.Observe(process.Id);
        if (observation.State == PlatformProcessIdentityState.Exited) return;
        if (observation.State != PlatformProcessIdentityState.Running || observation.StartTimeUtcTicks is not > 0)
            throw new IOException("The started game process identity cannot be confirmed safely.");
        _state["processId"] = process.Id;
        _state["processStartTicks"] = observation.StartTimeUtcTicks.Value;
        WriteJournal();
    }

    private void WriteJournal()
    {
        string temporary = _journal + ".new";
        RejectLinks(temporary); RejectLinks(_journal);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(_state!.ToJsonString());
            stream.Write(bytes); stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _journal, overwrite: true);
    }

    private async ValueTask RecoverAsync()
    {
        if (!Directory.Exists(_workspace)) return;
        RejectLinks(_workspace); RejectLinks(_journal);
        if (!File.Exists(_journal) || new FileInfo(_journal).Length > 8192)
            throw new InvalidDataException("The overlay recovery workspace is unowned or its journal is invalid; inspect it before launching.");
        _state = await ReadJournalAsync().ConfigureAwait(false);
        if (_state is null || _state["version"]?.GetValue<int>() != 1 || _state["game"]?.GetValue<string>() != _game
            || _state["directories"] is not JsonObject directories || directories.Count > Folders.Length
            || directories.Any(pair => !Folders.Contains(pair.Key, StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid overlay recovery journal; no files were changed.");
        if (_state["processId"] is { } processId)
        {
            var observation = _processes.Observe(processId.GetValue<int>());
            long expected = _state["processStartTicks"]?.GetValue<long>() ?? 0;
            if (observation.State == PlatformProcessIdentityState.Unknown
                || observation.State == PlatformProcessIdentityState.Running
                && (observation.StartTimeUtcTicks is not > 0 || expected == 0 || observation.StartTimeUtcTicks.Value == expected))
                throw new IOException("The game still owns its temporary launch directories or its identity is unknown; wait for it to exit.");
        }
        foreach (var pair in directories.ToArray())
        {
            string target = Path.Combine(_game, pair.Key), original = Path.Combine(_workspace, "original", pair.Key);
            RejectLinks(original);
            bool hadOriginal = pair.Value?["hadOriginal"]?.GetValue<bool>()
                ?? throw new InvalidDataException("Invalid overlay directory journal.");
            if (Directory.Exists(original))
            {
                RemoveOwnedDirectory(target);
                Directory.Move(original, target);
            }
            else if (!hadOriginal) RemoveOwnedDirectory(target);
            // With an original and no backup the rename never happened, or restoration already
            // completed before the journal write. In either case retain that original directory.
            directories.Remove(pair.Key); WriteJournal();
        }
        RemoveOwnedDirectory(_workspace); _state = null;
    }

    private async Task<JsonObject> ReadJournalAsync()
    {
        await using var input = new FileStream(_journal, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        byte[] bytes = new byte[8193]; int length = 0, read;
        while (length < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(length)).ConfigureAwait(false)) > 0) length += read;
        if (length == bytes.Length) throw new InvalidDataException("The overlay journal exceeds its actual 8 KiB read budget; no files were changed.");
        try
        {
            return JsonNode.Parse(bytes.AsSpan(0, length), documentOptions: new JsonDocumentOptions { MaxDepth = 16 }) as JsonObject
                ?? throw new InvalidDataException("The overlay journal is not an object; no files were changed.");
        }
        catch (JsonException error) { throw new InvalidDataException("The overlay journal is invalid; no files were changed.", error); }
    }

    private static bool IsWithin(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Launch overlays do not follow symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RemoveOwnedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) throw new IOException("Overlay restoration encountered a conflicting file: " + path);
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path); return; }
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) RemoveOwnedDirectory(entry);
            else File.Delete(entry);
        }
        Directory.Delete(path);
    }

    private static async Task CopyAsync(string source, string target, CancellationToken token)
    {
        Stack<(string Source, string Target)> pending = new(); pending.Push((source, target));
        int entries = 0; long bytes = 0;
        byte[] buffer = new byte[65536];
        while (pending.TryPop(out var directory))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Source))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 4096) throw new InvalidDataException("An overlay exceeds the 4096-entry budget.");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Overlay sources cannot contain symbolic links.");
                string destination = Path.Combine(directory.Target, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                { Directory.CreateDirectory(destination); pending.Push((entry, destination)); }
                else
                {
                    await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        bytes = checked(bytes + read);
                        if (bytes > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("An overlay exceeds the 2 GiB copy budget.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await RecoverAsync().ConfigureAwait(false); }
        finally { _lease.Dispose(); }
    }
}
