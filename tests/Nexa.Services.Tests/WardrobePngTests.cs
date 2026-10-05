using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Nexa.Services.Accounts;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static void WardrobePngAcceptsCompleteStaticSkins()
    {
        byte[] steve = SkinFixture();
        var image = WardrobeSkinValidator.Validate(steve, isSlim: false);
        AssertEqual(64, image.Width); AssertEqual(64, image.Height);
        AssertTrue(image.Bytes.Span.SequenceEqual(steve));
        AssertEqual(image.Key, WardrobeSkinValidator.Validate(steve, isSlim: true).Key);
        byte[] original = steve.ToArray(); steve[0] = 0;
        AssertTrue(image.Bytes.Span.SequenceEqual(original));

        foreach (int color in new[] { 2, 6 })
        {
            AssertEqual(64, WardrobeSkinValidator.Validate(WardrobeCreatePng(colorType: color), true).Height);
            AssertEqual(32, WardrobeSkinValidator.Validate(WardrobeCreatePng(height: 32, colorType: color), false).Height);
        }
        foreach (int depth in new[] { 1, 2, 4, 8 })
        {
            byte[] indexed = WardrobeCreatePng(colorType: 3, bitDepth: depth, transparency: [0, 255]);
            AssertEqual(64, WardrobeSkinValidator.Validate(indexed, false).Width);
        }

        // Known filter vectors: every reconstructed indexed row contains only palette index 1.
        byte[] rows = new byte[64 * 65];
        for (int row = 0; row < 64; row++)
        {
            int filter = row % 5; rows[row * 65] = (byte)filter;
            if (filter == 0) rows.AsSpan(row * 65 + 1, 64).Fill(1);
            else if (filter is 1 or 3) rows[row * 65 + 1] = 1;
        }
        WardrobeSkinValidator.Validate(WardrobeCreatePng(colorType: 3, rows: rows), true);

        // PNG permits a single compressed stream to span multiple consecutive IDAT chunks.
        byte[] png = WardrobeCreatePng();
        var chunks = WardrobeReadChunks(png).ToList();
        int dataIndex = chunks.FindIndex(chunk => chunk.Kind == "IDAT");
        byte[] data = chunks[dataIndex].Payload;
        chunks.RemoveAt(dataIndex);
        chunks.InsertRange(dataIndex, [("IDAT", data[..2]), ("IDAT", []), ("IDAT", data[2..])]);
        WardrobeSkinValidator.Validate(WardrobeWriteChunks(chunks), false);

        const int maximumBytes = 1_048_576;
        byte[] maximum = WardrobeInsertBeforeData(png, "neXa", new byte[maximumBytes - png.Length - 12]);
        AssertEqual(maximumBytes, maximum.Length);
        WardrobeSkinValidator.Validate(maximum, false);
    }

    private static void WardrobePngRejectsMalformedStructureAndUnsupportedFormats()
    {
        byte[] fixture = SkinFixture();
        WardrobeAssertInvalid([1, 2, 3]);
        WardrobeAssertInvalid(new byte[1_048_577]);
        for (int length = 0; length < fixture.Length; length++) WardrobeAssertInvalid(fixture[..length]);
        WardrobeAssertInvalid([.. fixture, 0]);
        WardrobeAssertInvalid([.. fixture, .. fixture]);
        byte[] badSignature = fixture.ToArray(); badSignature[0] ^= 1; WardrobeAssertInvalid(badSignature);
        byte[] badHeaderCrc = fixture.ToArray(); badHeaderCrc[32] ^= 1; WardrobeAssertInvalid(badHeaderCrc);
        byte[] badDataCrc = fixture.ToArray(); badDataCrc[^13] ^= 1; WardrobeAssertInvalid(badDataCrc);
        byte[] badLength = fixture.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(8, 4), uint.MaxValue); WardrobeAssertInvalid(badLength);

        WardrobeAssertInvalid(WardrobeCreatePng(width: 63));
        WardrobeAssertInvalid(WardrobeCreatePng(height: 63));
        WardrobeAssertInvalid(WardrobeCreatePng(width: 128, height: 128));
        WardrobeAssertInvalid(WardrobeCreatePng(height: 32), isSlim: true);
        foreach ((int index, byte value) in new (int, byte)[] { (8, 16), (9, 0), (9, 4), (10, 1), (11, 1), (12, 1) })
            WardrobeAssertInvalid(WardrobeRewriteChunk(WardrobeCreatePng(), "IHDR", header => { header[index] = value; return header; }));
        foreach (string animationChunk in new[] { "acTL", "fcTL", "fdAT" })
            WardrobeAssertInvalid(WardrobeInsertBeforeData(fixture, animationChunk, new byte[8]));
        WardrobeAssertInvalid(WardrobeInsertBeforeData(fixture, "ABCD", []));
        WardrobeAssertInvalid(WardrobeInsertBeforeData(fixture, "abca", []));
        WardrobeAssertInvalid(WardrobeInsertBeforeData(fixture, "ab1a", []));

        var chunks = WardrobeReadChunks(fixture).ToList();
        WardrobeAssertInvalid(WardrobeWriteChunks(chunks.Skip(1))); // Missing header.
        WardrobeAssertInvalid(WardrobeWriteChunks(chunks.Take(chunks.Count - 1))); // Missing end.
        WardrobeAssertInvalid(WardrobeWriteChunks([chunks[0], .. chunks])); // Duplicate header.
        WardrobeAssertInvalid(WardrobeRewriteChunk(fixture, "IEND", _ => [0]));
        WardrobeAssertInvalid(WardrobeWriteChunks(chunks.Where(chunk => chunk.Kind != "IDAT")));

        byte[] rgba = WardrobeCreatePng();
        var split = WardrobeReadChunks(rgba).ToList();
        byte[] data = split[1].Payload;
        WardrobeAssertInvalid(WardrobeWriteChunks([split[0], ("IDAT", data[..2]), ("neXa", []), ("IDAT", data[2..]), split[^1]]));
        WardrobeAssertInvalid(WardrobeWriteChunks([split[0], ("IDAT", data), ("PLTE", new byte[3]), split[^1]]));

        byte[] indexed = WardrobeCreatePng(colorType: 3);
        WardrobeAssertInvalid(WardrobeWriteChunks(WardrobeReadChunks(indexed).Where(chunk => chunk.Kind != "PLTE")));
        foreach (byte[] palette in new byte[][] { [], new byte[4], new byte[771] })
            WardrobeAssertInvalid(WardrobeRewriteChunk(indexed, "PLTE", _ => palette));
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, bitDepth: 1, palette: new byte[9]));
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, transparency: [1, 2, 3]));
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, transparency: []));
        WardrobeAssertInvalid(WardrobeCreatePng(transparency: [0]));
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 2, transparency: [0, 0]));
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 2, transparency: [1, 0, 0, 0, 0, 0]));
        WardrobeSkinValidator.Validate(WardrobeCreatePng(colorType: 2, transparency: [0, 1, 0, 2, 0, 3]), false);
        var paletteChunks = WardrobeReadChunks(indexed).ToList();
        WardrobeAssertInvalid(WardrobeWriteChunks([paletteChunks[0], paletteChunks[1], .. paletteChunks.Skip(1)]));
        WardrobeAssertInvalid(WardrobeWriteChunks([paletteChunks[0], ("tRNS", new byte[1]), .. paletteChunks.Skip(1)]));
        WardrobeAssertInvalid(WardrobeInsertBeforeData(WardrobeCreatePng(colorType: 3, transparency: [0]), "tRNS", [0]));
    }

    private static void WardrobePngRejectsInvalidAndUnboundedImageData()
    {
        byte[] valid = WardrobeCreatePng();
        byte[] compressed = WardrobeReadChunks(valid).Single(chunk => chunk.Kind == "IDAT").Payload;
        // Recompute PNG CRCs to isolate truncated zlib validation from container validation.
        for (int length = 0; length < compressed.Length; length++)
            WardrobeAssertInvalid(WardrobeCreatePng(compressed: compressed[..length]));
        byte[] badAdler = compressed.ToArray(); badAdler[^1] ^= 1;
        WardrobeAssertInvalid(WardrobeCreatePng(compressed: badAdler));
        WardrobeAssertInvalid(WardrobeCreatePng(compressed: [.. compressed, 0]));
        WardrobeAssertInvalid(WardrobeCreatePng(compressed: [.. compressed, .. compressed]));
        WardrobeAssertInvalid(WardrobeCreatePng(compressed: [0x78, 0x9c, 0xff, 0, 0, 0, 0]));

        const int expectedRows = 64 * (64 * 4 + 1);
        WardrobeAssertInvalid(WardrobeCreatePng(rows: new byte[expectedRows - 1]));
        WardrobeAssertInvalid(WardrobeCreatePng(rows: new byte[expectedRows + 1]));
        WardrobeAssertInvalid(WardrobeCreatePng(rows: new byte[4 * 1_048_576]));
        byte[] badFilter = new byte[expectedRows]; badFilter[32 * 257] = 5;
        WardrobeAssertInvalid(WardrobeCreatePng(rows: badFilter));

        byte[] indexedRows = new byte[64 * 65]; indexedRows[1] = 2;
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, rows: indexedRows));
        // Index validation must happen after unfiltering: Sub turns 1 + 1 into invalid index 2.
        indexedRows[0] = 1; indexedRows[1] = 1; indexedRows[2] = 1;
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, rows: indexedRows));
        byte[] packedRows = new byte[64 * 17]; packedRows[1] = 0xc0;
        WardrobeAssertInvalid(WardrobeCreatePng(colorType: 3, bitDepth: 2, rows: packedRows));
    }

    private static void WardrobeAssertInvalid(byte[] bytes, bool isSlim = false)
    {
        try { WardrobeSkinValidator.Validate(bytes, isSlim); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid skin PNG was accepted.");
    }

    private static byte[] WardrobeCreatePng(int width = 64, int height = 64, int bitDepth = 8, int colorType = 6,
        byte[]? rows = null, byte[]? compressed = null, byte[]? palette = null, byte[]? transparency = null)
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth; header[9] = (byte)colorType;
        List<(string Kind, byte[] Payload)> chunks = [("IHDR", header)];
        if (colorType == 3 || palette is not null) chunks.Add(("PLTE", palette ?? new byte[6]));
        if (transparency is not null) chunks.Add(("tRNS", transparency));
        if (compressed is null)
        {
            int channels = colorType switch { 2 => 3, 6 => 4, _ => 1 };
            rows ??= new byte[height * ((width * channels * bitDepth + 7) / 8 + 1)];
            using MemoryStream stream = new();
            using (ZLibStream encoder = new(stream, CompressionLevel.SmallestSize, leaveOpen: true)) encoder.Write(rows);
            compressed = stream.ToArray();
        }
        chunks.Add(("IDAT", compressed)); chunks.Add(("IEND", []));
        return WardrobeWriteChunks(chunks);
    }

    private static IEnumerable<(string Kind, byte[] Payload)> WardrobeReadChunks(byte[] png)
    {
        for (int offset = 8; offset < png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            yield return (Encoding.ASCII.GetString(png, offset + 4, 4), png.AsSpan(offset + 8, length).ToArray());
            offset += length + 12;
        }
    }

    private static byte[] WardrobeWriteChunks(IEnumerable<(string Kind, byte[] Payload)> chunks)
    {
        using MemoryStream stream = new(); stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] integer = new byte[4];
        foreach ((string kind, byte[] payload) in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)payload.Length); stream.Write(integer);
            byte[] type = Encoding.ASCII.GetBytes(kind); stream.Write(type); stream.Write(payload);
            uint crc = uint.MaxValue;
            foreach (byte value in type.Concat(payload))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc); stream.Write(integer);
        }
        return stream.ToArray();
    }

    private static byte[] WardrobeRewriteChunk(byte[] png, string kind, Func<byte[], byte[]> rewrite) =>
        WardrobeWriteChunks(WardrobeReadChunks(png).Select(chunk => chunk.Kind == kind ? (kind, rewrite(chunk.Payload)) : chunk));

    private static byte[] WardrobeInsertBeforeData(byte[] png, string kind, byte[] payload)
    {
        var chunks = WardrobeReadChunks(png).ToList();
        chunks.Insert(chunks.FindIndex(chunk => chunk.Kind == "IDAT"), (kind, payload));
        return WardrobeWriteChunks(chunks);
    }

}
