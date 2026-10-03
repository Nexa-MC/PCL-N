using System.IO.Compression;
using System.Security.Cryptography;
using Nexa.Xsr;
using Nexa.Xsr.State;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceServerListService
{
    private const int Limit = 8 * 1024 * 1024;
    public static Task<InstanceServerList> ReadAsync(InstanceServerListQuery query, CancellationToken token = default) => Task.Run(async () =>
    {
        var instance = await InstanceManagementService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        var (revision, root) = await ReadFileAsync(Path.Combine(instance.GameDirectory, "servers.dat"), token).ConfigureAwait(false);
        var list = List(root);
        if (list.Children.Count > 512 || list.ListType != 10) throw new InvalidDataException("服务器列表超过限制或类型无效。");
        return new InstanceServerList(revision, Array.AsReadOnly(list.Children.Select((tag, i) => new InstanceServerEntry(i,
            tag.String("name") ?? "", tag.String("ip") ?? "", tag.String("icon"),
            tag.Children.FirstOrDefault(t => t.Name == "acceptTextures" && t.Type == 1) is { } preference ? preference.Payload[0] != 0 : null)).ToArray()));
    }, token);

    public static async Task<XsrResult> SaveAsync(InstanceServerListSaveCommand command, XsrStateStore store, CancellationToken token = default)
    {
        string? stage = null;
        try
        {
            if (command.Entries.Count > 512 || command.Entries.Any(e => string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 256
                || string.IsNullOrWhiteSpace(e.Address) || e.Address.Length > 512 || e.Address.Any(char.IsControl)
                || e.Name.Any(char.IsControl) || e.SourceIndex < -1)) throw new InvalidDataException("服务器名称或地址无效。");
            var instance = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            using var lease = await InstanceRecoveryOperationGate.EnterRestoreAsync(Directory.GetParent(instance.InstanceDirectory)!.Parent!.FullName, token).ConfigureAwait(false);
            instance = await InstanceManagementService.ReadAsync(new(command.InstanceDirectory), token).ConfigureAwait(false);
            InstanceContentTrash.RejectRunning(instance, store, token);
            string path = Path.Combine(instance.GameDirectory, "servers.dat"), backup = path + "_old";
            var (revision, root) = await ReadFileAsync(path, token).ConfigureAwait(false);
            if (revision != command.ExpectedRevision) throw new IOException("服务器列表已变化，请刷新后重试。");
            var list = List(root); var original = list.Children.ToArray();
            if (list.ListType != 10 || original.Length > 512 || command.Entries.Any(e => e.SourceIndex >= original.Length)
                || command.Entries.Where(e => e.SourceIndex >= 0).Select(e => e.SourceIndex).Distinct().Count() != command.Entries.Count(e => e.SourceIndex >= 0))
                throw new InvalidDataException("服务器编辑身份无效。");
            list.Children.Clear();
            foreach (var entry in command.Entries)
            {
                var tag = entry.SourceIndex >= 0 ? original[entry.SourceIndex] : new ServerNbt(10, "", []);
                tag.SetString("name", entry.Name.Trim()); tag.SetString("ip", entry.Address.Trim()); list.Children.Add(tag);
            }
            byte[] bytes = root.Serialize();
            stage = Path.Combine(instance.GameDirectory, ".nexa-servers-" + Guid.NewGuid().ToString("N"));
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, token).ConfigureAwait(false); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            RecoveryBlobStore.CheckLinks(path); RecoveryBlobStore.CheckLinks(backup);
            if ((await ReadFileAsync(path, token).ConfigureAwait(false)).Revision != revision) throw new IOException("服务器列表已变化，未覆盖其他修改。");
            if (revision == "missing") File.Move(stage, path, false);
            else File.Replace(stage, path, backup, true);
            return XsrResult.Success();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return XsrResult.Failure(XsrRuntimeErrors.Cancelled()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        { return XsrResult.Failure(MinecraftErrors.InvalidRequest(error.Message)); }
        finally { if (stage is not null) try { File.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static ServerNbt List(ServerNbt root)
    {
        var list = root.Children.FirstOrDefault(t => t.Name == "servers");
        if (list is not null)
        {
            if (list.Type != 9) throw new InvalidDataException("服务器列表不是 NBT 列表。");
            if (list.Children.Count == 0 && list.ListType == 0) list.ListType = 10;
            return list;
        }
        list = new(9, "servers", []) { ListType = 10 }; root.Children.Add(list); return list;
    }
    private static async Task<(string Revision, ServerNbt Root)> ReadFileAsync(string path, CancellationToken token)
    {
        RecoveryBlobStore.CheckLinks(path);
        if (!File.Exists(path)) return ("missing", new(10, "", []));
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length > Limit) throw new InvalidDataException("服务器列表超过 8 MiB。");
        using var raw = new MemoryStream(); await CopyBounded(file, raw, token).ConfigureAwait(false);
        byte[] bytes = raw.ToArray(); string revision = Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes.Length > 2 && bytes[0] == 31 && bytes[1] == 139)
        {
            raw.Position = 0; using var gzip = new GZipStream(raw, CompressionMode.Decompress); using var unpacked = new MemoryStream();
            await CopyBounded(gzip, unpacked, token).ConfigureAwait(false); bytes = unpacked.ToArray();
        }
        else if (bytes.Length > 2 && bytes[0] == 0x78)
        {
            raw.Position = 0; using var zlib = new ZLibStream(raw, CompressionMode.Decompress); using var unpacked = new MemoryStream();
            await CopyBounded(zlib, unpacked, token).ConfigureAwait(false); bytes = unpacked.ToArray();
        }
        return (revision, ServerNbt.Parse(bytes));
    }
    private static async Task CopyBounded(Stream input, Stream output, CancellationToken token)
    {
        byte[] buffer = new byte[81920]; int read; long total = 0;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Limit - total + 1)), token).ConfigureAwait(false)) > 0)
        { total += read; if (total > Limit) throw new InvalidDataException("服务器 NBT 实际大小超过限制。"); await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); }
    }
}
