using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Nexa.Platform;

namespace Nexa.Services.Minecraft.Launch;

/// <summary>Tracks ordinary directory readers across launcher exit without blocking launcher shutdown.</summary>
internal sealed partial class MinecraftGameDirectoryUseLease : IAsyncDisposable
{
    private readonly string _game, _receipt;
    private readonly FileStream _lease;
    private readonly IPlatformProcessIdentity _processes;
    private int _processId;
    private long _processStarted;
    private int _disposed;

    private MinecraftGameDirectoryUseLease(string game, string receipt, FileStream lease, IPlatformProcessIdentity processes)
    { _game = game; _receipt = receipt; _lease = lease; _processes = processes; }

    internal static async ValueTask<MinecraftGameDirectoryUseLease> AcquireAsync(string gameDirectory,
        IPlatformProcessIdentity? processIdentity = null, CancellationToken cancellationToken = default)
    {
        var processes = processIdentity ?? PlatformProcessIdentityFactory.Create();
        string game = PrepareDirectory(gameDirectory);
        using var gate = await AcquireGateAsync(game, cancellationToken).ConfigureAwait(false);
        FileStream lease = OpenLease(Path.Combine(game, ".nexacl-launch-overlay.lock"), exclusive: false);
        string receipt = Path.Combine(game, ".nexacl-game-uses", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await InspectReceiptsAsync(game, rejectLive: false, processes, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
            var owner = processes.Observe(Environment.ProcessId);
            if (owner.State != PlatformProcessIdentityState.Running || owner.StartTimeUtcTicks is not > 0)
                throw new IOException("The launcher process identity cannot be confirmed safely.");
            WriteReceipt(receipt, game, "preparing", Environment.ProcessId, owner.StartTimeUtcTicks.Value);
            return new(game, receipt, lease, processes);
        }
        catch { lease.Dispose(); throw; }
    }

    internal static async ValueTask<FileStream> AcquireExclusiveAsync(string gameDirectory,
        IPlatformProcessIdentity? processIdentity = null, CancellationToken cancellationToken = default)
    {
        string game = PrepareDirectory(gameDirectory);
        using var gate = await AcquireGateAsync(game, cancellationToken).ConfigureAwait(false);
        FileStream lease = OpenLease(Path.Combine(game, ".nexacl-launch-overlay.lock"), exclusive: true);
        try
        {
            await InspectReceiptsAsync(game, rejectLive: true, processIdentity ?? PlatformProcessIdentityFactory.Create(), cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal async ValueTask BindProcessAsync(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        using var gate = await AcquireGateAsync(_game, CancellationToken.None).ConfigureAwait(false);
        int processId = process.Id;
        var observation = _processes.Observe(processId);
        if (observation.State == PlatformProcessIdentityState.Exited) { File.Delete(_receipt); return; }
        if (observation.State != PlatformProcessIdentityState.Running || observation.StartTimeUtcTicks is not > 0)
            throw new IOException("The started game process identity cannot be confirmed safely.");
        long started = observation.StartTimeUtcTicks.Value;
        WriteReceipt(_receipt, _game, "running", processId, started);
        _processId = processId; _processStarted = started;
    }

    private static string PrepareDirectory(string gameDirectory)
    {
        string game = Path.GetFullPath(gameDirectory);
        RejectLinks(game); Directory.CreateDirectory(game);
        return game;
    }

    private static async ValueTask<FileStream> AcquireGateAsync(string game, CancellationToken token)
    {
        string path = Path.Combine(game, ".nexacl-game-use-gate.lock");
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return OpenLease(path, exclusive: true); }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            { await Task.Delay(10, token).ConfigureAwait(false); }
        }
    }

    private static FileStream OpenLease(string path, bool exclusive)
    {
        RejectLinks(path);
        // The explicit Unix advisory lock avoids relying on FileShare emulation for shared readers.
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            OperatingSystem.IsWindows() && exclusive ? FileShare.None : FileShare.ReadWrite);
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                    throw new PlatformNotSupportedException("Game-directory leases require Windows, Linux or macOS.");
                int descriptor = checked((int)stream.SafeFileHandle.DangerousGetHandle());
                if (Flock(descriptor, (exclusive ? 2 : 1) | 4) != 0)
                    throw new IOException("The game directory is already in use; wait for its game to exit.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static async Task InspectReceiptsAsync(string game, bool rejectLive, IPlatformProcessIdentity processes, CancellationToken token)
    {
        string directory = Path.Combine(game, ".nexacl-game-uses"); RejectLinks(directory);
        if (!Directory.Exists(directory)) return;
        int count = 0, active = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested(); RejectLinks(path);
            if (++count > 64 || !Path.GetFileName(path).EndsWith(".json", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
                || Directory.Exists(path))
                throw new InvalidDataException("Game-directory use receipts are incomplete or exceed the 64-record budget; inspect .nexacl-game-uses.");
            var receipt = await ReadReceiptAsync(path, token).ConfigureAwait(false);
            if (receipt["version"]?.GetValue<int>() != 1 || receipt["game"]?.GetValue<string>() != game
                || receipt["processId"]?.GetValue<int>() is not > 0
                || receipt["processStartTicks"]?.GetValue<long>() is not > 0
                || receipt["phase"]?.GetValue<string>() is not ("preparing" or "running"))
                throw new InvalidDataException("A game-directory use receipt is invalid; no launch directories were changed.");
            bool preparing = receipt["phase"]!.GetValue<string>() == "preparing";
            bool live = IsLive(processes, receipt["processId"]!.GetValue<int>(), receipt["processStartTicks"]!.GetValue<long>());
            if (!preparing && !live) { File.Delete(path); continue; }
            active++;
            if (rejectLive)
                throw new IOException(preparing && !live
                    ? "An interrupted game startup has an unbound process receipt; inspect .nexacl-game-uses before applying an overlay."
                    : "A running or starting game still owns this directory; wait before applying an overlay.");
        }
        if (!rejectLive && active == 64)
            throw new InvalidDataException("The game directory already has 64 active or uncertain use receipts.");
    }

    private static async Task<JsonObject> ReadReceiptAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        byte[] bytes = new byte[4097]; int length = 0, read;
        while (length < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false)) > 0)
            length += read;
        if (length == bytes.Length) throw new InvalidDataException("A game-directory receipt exceeds its 4 KiB budget.");
        return JsonNode.Parse(bytes.AsSpan(0, length)) as JsonObject
            ?? throw new InvalidDataException("A game-directory receipt is not an object.");
    }

    private static bool IsLive(IPlatformProcessIdentity processes, int processId, long started)
    {
        var observation = processes.Observe(processId);
        if (observation.State == PlatformProcessIdentityState.Exited) return false;
        if (observation.State != PlatformProcessIdentityState.Running || observation.StartTimeUtcTicks is not > 0)
            throw new IOException("The recorded game process could not be inspected safely.");
        return observation.StartTimeUtcTicks.Value == started;
    }

    private static void WriteReceipt(string path, string game, string phase, int processId, long started)
    {
        string temporary = path + ".new"; RejectLinks(path); RejectLinks(temporary);
        var receipt = new JsonObject
        {
            ["version"] = 1,
            ["game"] = game,
            ["phase"] = phase,
            ["processId"] = processId,
            ["processStartTicks"] = started
        };
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(receipt.ToJsonString());
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        File.Move(temporary, path, overwrite: true);
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Game-directory leases do not follow symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!Directory.Exists(_game)) return;
            using var gate = await AcquireGateAsync(_game, CancellationToken.None).ConfigureAwait(false);
            // Disposal can also happen while the launcher is stopping or its process observer
            // has been disposed. Keep the durable receipt until the actual game is confirmed dead.
            if (_processId != 0 && IsLive(_processes, _processId, _processStarted)) return;
            RejectLinks(_receipt); File.Delete(_receipt);
            RejectLinks(_receipt + ".new"); File.Delete(_receipt + ".new");
        }
        finally { _lease.Dispose(); }
    }

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Flock(int descriptor, int operation);
}
