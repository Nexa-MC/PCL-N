using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nexa.Services.Minecraft.Management;

public static class InstanceServerStatusService
{
    public static async Task<InstanceServerStatus> ReadAsync(InstanceServerStatusQuery query, CancellationToken token = default)
    {
        var servers = await InstanceServerListService.ReadAsync(new(query.InstanceDirectory), token).ConfigureAwait(false);
        if (query.ExpectedRevision != servers.Revision || query.SourceIndex < 0 || query.SourceIndex >= servers.Entries.Count)
            throw new IOException("服务器列表已变化，请刷新后重试。");
        string address = servers.Entries[query.SourceIndex].Address;
        if (address.Length > 512 || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl) || address.Contains("://", StringComparison.Ordinal) || !Uri.TryCreate("tcp://" + address, UriKind.Absolute, out var endpoint)
            || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Port > 65535)
            throw new InvalidDataException("服务器地址无效。");
        int port = endpoint.Port < 0 ? 25565 : endpoint.Port;
        var clock = Stopwatch.StartNew(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); stop.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient(); await client.ConnectAsync(endpoint.DnsSafeHost, port, stop.Token).ConfigureAwait(false);
            await using var stream = client.GetStream(); using var packet = new MemoryStream();
            WriteVar(packet, 0); WriteVar(packet, -1); byte[] host = Encoding.UTF8.GetBytes(endpoint.DnsSafeHost); WriteVar(packet, host.Length); packet.Write(host);
            byte[] portBytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port); packet.Write(portBytes); WriteVar(packet, 1);
            using var request = new MemoryStream(); WriteVar(request, (int)packet.Length); packet.WriteTo(request); request.Write([1, 0]);
            await stream.WriteAsync(request.ToArray(), stop.Token).ConfigureAwait(false);
            int length = await ReadVarAsync(stream, stop.Token).ConfigureAwait(false);
            if (length is <= 0 or > 262144) throw new InvalidDataException("服务器状态数据超过限制。");
            byte[] response = new byte[length]; await stream.ReadExactlyAsync(response, stop.Token).ConfigureAwait(false);
            using var data = new MemoryStream(response, false);
            if (await ReadVarAsync(data, stop.Token).ConfigureAwait(false) != 0) throw new InvalidDataException("服务器状态包无效。");
            int jsonLength = await ReadVarAsync(data, stop.Token).ConfigureAwait(false);
            if (jsonLength <= 0 || jsonLength != data.Length - data.Position) throw new InvalidDataException("服务器状态长度无效。");
            using var document = JsonDocument.Parse(response.AsMemory((int)data.Position, jsonLength), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("服务器状态不是对象。");
            string description = root.TryGetProperty("description", out var text) ? Description(text, 0) : "";
            string version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString()![..Math.Min(128, name.GetString()!.Length)] : "";
            int? online = null, max = null;
            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                if (players.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out int value) && value >= 0) online = value;
                if (players.TryGetProperty("max", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out value) && value >= 0) max = value;
            }
            return new(true, description, version, online, max, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or JsonException or InvalidDataException)
        { return new(false, "无法连接或未返回有效状态", "", null, null, clock.ElapsedMilliseconds); }
    }
    private static string Description(JsonElement value, int depth)
    {
        if (depth > 8) return "";
        string text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("text", out var plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString()! : "";
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            foreach (var child in extra.EnumerateArray().Take(32)) { text += Description(child, depth + 1); if (text.Length >= 512) break; }
        return text[..Math.Min(512, text.Length)];
    }
    internal static void WriteVar(Stream stream, int number)
    {
        uint value = unchecked((uint)number);
        do { byte part = (byte)(value & 127); value >>= 7; stream.WriteByte(value == 0 ? part : (byte)(part | 128)); } while (value != 0);
    }
    internal static async Task<int> ReadVarAsync(Stream stream, CancellationToken token)
    {
        byte[] one = new byte[1]; int value = 0;
        for (int i = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false);
            if (i == 4 && (one[0] & 240) != 0) throw new InvalidDataException("服务器可变整数无效。");
            value |= (one[0] & 127) << (7 * i); if ((one[0] & 128) == 0) return value;
        }
        throw new InvalidDataException("服务器可变整数过长。");
    }
}
