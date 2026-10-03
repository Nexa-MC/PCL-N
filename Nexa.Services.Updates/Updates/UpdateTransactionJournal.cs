using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nexa.Services.Updates;

/// <summary>Bounded corruption-detecting frames. Authentication is the protected namespace.</summary>
public static class UpdateTransactionJournal
{
    private const int MaximumBytes = 4 * 1024 * 1024, MaximumFrameBytes = 16 * 1024;
    public static string[]? Read(Stream stream) => Scan(stream).Fields;
    private static (string[]? Fields, long Length) Scan(Stream stream)
    {
        if (stream.Length > MaximumBytes) throw new InvalidDataException("更新日志已满。");
        stream.Position = 0;
        string[]? last = null;
        long committed = 0;
        byte[] header = new byte[8];
        while (stream.Position < stream.Length)
        {
            int count = (int)Math.Min(8, stream.Length - stream.Position);
            stream.ReadExactly(header.AsSpan(0, count));
            if (!header.AsSpan(0, Math.Min(count, 4)).SequenceEqual("NXT1"u8[..Math.Min(count, 4)]))
                throw new InvalidDataException("更新日志帧无效。");
            if (count < 8) return (last, committed);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 2 or > MaximumFrameBytes) throw new InvalidDataException("更新日志帧超限。");
            if (stream.Length - stream.Position < length + 36) return (last, committed);
            byte[] body = new byte[length];
            stream.ReadExactly(body);
            byte[] digest = new byte[32], suffix = new byte[4];
            stream.ReadExactly(digest); stream.ReadExactly(suffix);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(header); hash.AppendData(body);
            if (!CryptographicOperations.FixedTimeEquals(digest, hash.GetHashAndReset()) || !suffix.AsSpan().SequenceEqual("DONE"u8))
                throw new InvalidDataException("更新日志校验失败。");
            using var data = new MemoryStream(body, writable: false);
            using var reader = new BinaryReader(data, new UTF8Encoding(false, true));
            int fields = reader.ReadUInt16();
            if (fields is < 1 or > 16) throw new InvalidDataException("更新日志字段数量无效。");
            last = new string[fields];
            for (int i = 0; i < fields; i++) last[i] = reader.ReadString();
            if (data.Position != data.Length) throw new InvalidDataException("更新日志包含额外数据。");
            committed = stream.Position;
        }
        return (last, committed);
    }

    public static void Append(FileStream stream, params string[] fields)
    {
        byte[] frame = Encode(fields);
        var state = Scan(stream);
        if (state.Length + frame.Length > MaximumBytes) throw new InvalidDataException("更新日志已满，需要系统维护。");
        stream.SetLength(state.Length); stream.Position = state.Length;
        stream.Write(frame); stream.Flush(true);
    }

    internal static byte[] Encode(string[] fields)
    {
        if (fields.Length is < 1 or > 16 || fields.Any(x => x is null || Encoding.UTF8.GetByteCount(x) > 4096))
            throw new InvalidDataException("更新日志字段超限。");
        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        { writer.Write((ushort)fields.Length); foreach (string value in fields) writer.Write(value); }
        if (body.Length > MaximumFrameBytes) throw new InvalidDataException("更新日志帧超限。");
        byte[] frame = new byte[8 + body.Length + 36];
        "NXT1"u8.CopyTo(frame);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), (int)body.Length);
        body.ToArray().CopyTo(frame, 8);
        SHA256.HashData(frame.AsSpan(0, 8 + (int)body.Length), frame.AsSpan(8 + (int)body.Length, 32));
        "DONE"u8.CopyTo(frame.AsSpan(frame.Length - 4));
        return frame;
    }
}
