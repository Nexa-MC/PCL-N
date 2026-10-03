using System.Buffers.Binary;
using System.Text;

namespace Nexa.Services.Minecraft.Management;

/// <summary>Bounded NBT preservation codec, including tags this launcher does not interpret.</summary>
internal sealed class ServerNbt(byte type, string name, byte[] payload)
{
    internal byte Type { get; } = type;
    internal string Name { get; } = name;
    internal byte[] Payload { get; } = payload;
    internal List<ServerNbt> Children { get; } = [];
    internal byte ListType { get; set; }
    internal static ServerNbt Parse(byte[] bytes)
    {
        int offset = 0, count = 0;
        var root = Read(bytes, ref offset, true, 0, ref count);
        if (root.Type != 10 || offset != bytes.Length) throw new InvalidDataException("服务器 NBT 根节点无效。");
        return root;
    }
    private static ServerNbt Read(byte[] bytes, ref int offset, bool named, int depth, ref int count, byte listType = 0)
    {
        if (depth > 32 || ++count > 100000) throw new InvalidDataException("服务器 NBT 过于复杂。");
        byte type = named ? Take(bytes, ref offset, 1)[0] : listType;
        if (type is 0 or > 12) throw new InvalidDataException("服务器 NBT 类型无效。");
        string name = named ? ReadString(bytes, ref offset) : "";
        var tag = new ServerNbt(type, name, []); int start = offset;
        if (type == 10)
        {
            while (offset < bytes.Length && bytes[offset] != 0) tag.Children.Add(Read(bytes, ref offset, true, depth + 1, ref count));
            if (Take(bytes, ref offset, 1)[0] != 0) throw new InvalidDataException("服务器 NBT 未结束。");
        }
        else if (type == 9)
        {
            tag.ListType = Take(bytes, ref offset, 1)[0];
            int length = Length(bytes, ref offset);
            if (length > 100000 || tag.ListType > 12 || tag.ListType == 0 && length > 0) throw new InvalidDataException("服务器 NBT 列表无效。");
            for (int i = 0; i < length; i++) tag.Children.Add(Read(bytes, ref offset, false, depth + 1, ref count, tag.ListType));
        }
        else
        {
            int length = type switch
            {
                1 => 1,
                2 => 2,
                3 or 5 => 4,
                4 or 6 => 8,
                7 => Length(bytes, ref offset),
                8 => BinaryPrimitives.ReadUInt16BigEndian(Take(bytes, ref offset, 2)),
                11 => checked(Length(bytes, ref offset) * 4),
                12 => checked(Length(bytes, ref offset) * 8),
                _ => 0
            };
            Take(bytes, ref offset, length);
            tag = new(type, name, bytes.AsSpan(start, offset - start).ToArray());
        }
        return tag;
    }
    private static int Length(byte[] bytes, ref int offset)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(Take(bytes, ref offset, 4));
        return length is >= 0 and <= 8 * 1024 * 1024 ? length : throw new InvalidDataException("服务器 NBT 大小无效。");
    }
    private static ReadOnlySpan<byte> Take(byte[] bytes, ref int offset, int length)
    {
        if (length < 0 || length > bytes.Length - offset) throw new InvalidDataException("服务器 NBT 已截断。");
        var result = bytes.AsSpan(offset, length); offset += length; return result;
    }
    // Java DataInput/DataOutput use modified UTF-8: NUL has two bytes and UTF-16
    // surrogate code units have three bytes each (not a four-byte Unicode scalar).
    private static string ReadString(byte[] bytes, ref int offset)
    {
        var data = Take(bytes, ref offset, BinaryPrimitives.ReadUInt16BigEndian(Take(bytes, ref offset, 2)));
        var text = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length;)
        {
            int first = data[i++];
            if (first < 128) { text.Append((char)first); continue; }
            int extra = (first & 0xE0) == 0xC0 ? 1 : (first & 0xF0) == 0xE0 ? 2 : -1;
            if (extra < 0 || i + extra > data.Length) throw new InvalidDataException("服务器文字编码无效。");
            int value = first & (extra == 1 ? 31 : 15);
            for (int j = 0; j < extra; j++)
            {
                int next = data[i++];
                if ((next & 0xC0) != 0x80) throw new InvalidDataException("服务器文字编码无效。");
                value = (value << 6) | (next & 63);
            }
            text.Append((char)value);
        }
        return text.ToString();
    }
    internal string? String(string key)
    {
        var tag = Children.FirstOrDefault(t => t.Name == key && t.Type == 8);
        if (tag is null) return null;
        int offset = 0; return ReadString(tag.Payload, ref offset);
    }
    internal void SetString(string key, string value)
    {
        using var stream = new MemoryStream(); WriteString(stream, value);
        var tag = new ServerNbt(8, key, stream.ToArray());
        int index = Children.FindIndex(t => t.Name == key);
        if (index < 0) Children.Add(tag); else Children[index] = tag;
    }
    internal byte[] Serialize()
    {
        using var stream = new MemoryStream(); Write(stream, true);
        if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("服务器列表超过限制。");
        return stream.ToArray();
    }
    private void Write(Stream stream, bool named)
    {
        if (named) { stream.WriteByte(Type); WriteString(stream, Name); }
        if (Type == 10) { foreach (var tag in Children) tag.Write(stream, true); stream.WriteByte(0); }
        else if (Type == 9)
        {
            stream.WriteByte(ListType); Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, Children.Count); stream.Write(size);
            foreach (var tag in Children) tag.Write(stream, false);
        }
        else stream.Write(Payload);
    }
    private static void WriteString(Stream stream, string value)
    {
        int length = 0;
        foreach (char c in value) length += c is > '\0' and <= '\u007f' ? 1 : c <= '\u07ff' ? 2 : 3;
        if (length > ushort.MaxValue) throw new InvalidDataException("服务器文字过长。");
        Span<byte> size = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)length); stream.Write(size);
        foreach (char c in value)
        {
            if (c is > '\0' and <= '\u007f') stream.WriteByte((byte)c);
            else
            {
                if (c > '\u07ff') { stream.WriteByte((byte)(0xE0 | (c >> 12))); stream.WriteByte((byte)(0x80 | ((c >> 6) & 63))); }
                else stream.WriteByte((byte)(0xC0 | (c >> 6)));
                stream.WriteByte((byte)(0x80 | (c & 63)));
            }
        }
    }
}
