using System.Buffers.Binary;
using System.IO.Compression;
using Nexa.Core.Media;

namespace Nexa.Services.Accounts;

/// <summary>Validates complete, bounded PNG skin uploads before they leave the account service.</summary>
public static class WardrobeSkinValidator
{
    private const int MaximumBytes = 1_048_576;
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Accepts static 64×64 skins and classic 64×32 skins with complete image data.</summary>
    /// <exception cref="InvalidDataException">The image is malformed or unsupported for the selected model.</exception>
    public static PngImage Validate(byte[] bytes, bool isSlim)
        => ValidateCore(bytes, AccountWardrobeTextureKind.Skin, isSlim, upload: true);

    /// <summary>Validates bounded static skin/cape previews, including 64–512 pixel HD texture atlases.</summary>
    public static PngImage ValidateTexture(byte[] bytes, AccountWardrobeTextureKind kind, bool isSlim = false)
        => ValidateCore(bytes, kind, isSlim, upload: false);

    private static PngImage ValidateCore(byte[] bytes, AccountWardrobeTextureKind textureKind, bool isSlim, bool upload)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (textureKind is not (AccountWardrobeTextureKind.Skin or AccountWardrobeTextureKind.Cape))
            throw new ArgumentOutOfRangeException(nameof(textureKind));
        if (bytes.Length > MaximumBytes) throw Invalid("皮肤文件不能超过 1 MiB。");
        // Freeze caller-owned bytes so the returned carrier describes the same image we validated.
        byte[] encoded = bytes.ToArray();
        if (encoded.Length < 45 || !encoded.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw Invalid("皮肤文件不是完整的 PNG。");

        int width = 0, height = 0, colorType = 0, bitDepth = 0, paletteEntries = 0;
        bool headerSeen = false, paletteSeen = false, transparencySeen = false, dataSeen = false, dataEnded = false, endSeen = false;
        using MemoryStream compressed = new();
        for (int offset = 8; offset < encoded.Length;)
        {
            if (encoded.Length - offset < 12) throw Invalid("PNG 数据块被截断。");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset, 4));
            if (length > encoded.Length - offset - 12) throw Invalid("PNG 数据块长度无效。");
            ReadOnlySpan<byte> kind = encoded.AsSpan(offset + 4, 4);
            ReadOnlySpan<byte> payload = encoded.AsSpan(offset + 8, (int)length);
            for (int index = 0; index < kind.Length; index++)
                if (kind[index] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw Invalid("PNG 数据块类型无效。");
            if ((kind[2] & 32) != 0) throw Invalid("PNG 数据块保留位无效。");
            if (CalculateCrc(encoded.AsSpan(offset + 4, (int)length + 4))
                != BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(offset + 8 + (int)length, 4)))
                throw Invalid("PNG 数据块校验失败。");
            if (!headerSeen && !kind.SequenceEqual("IHDR"u8)) throw Invalid("PNG 缺少首个 IHDR 数据块。");
            if (dataSeen && !kind.SequenceEqual("IDAT"u8)) dataEnded = true;

            if (kind.SequenceEqual("IHDR"u8))
            {
                if (headerSeen || length != 13) throw Invalid("PNG IHDR 数据块无效。");
                uint imageWidth = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
                uint imageHeight = BinaryPrimitives.ReadUInt32BigEndian(payload[4..8]);
                if (upload && (imageWidth != 64 || imageHeight is not (32 or 64)))
                    throw Invalid("皮肤尺寸必须为 64×64 或 64×32。");
                if (!upload && (imageWidth is < 64 or > 512 || imageWidth % 64 != 0
                    || (textureKind == AccountWardrobeTextureKind.Skin
                        ? imageHeight != imageWidth && imageHeight * 2 != imageWidth
                        : imageHeight * 2 != imageWidth)))
                    throw Invalid("皮肤或披风预览尺寸无效。");
                if (textureKind == AccountWardrobeTextureKind.Skin && isSlim && imageHeight * 2 == imageWidth)
                    throw Invalid("纤细模型不支持旧式 64×32 皮肤。");
                width = (int)imageWidth; height = (int)imageHeight; bitDepth = payload[8]; colorType = payload[9];
                if (!((colorType is 2 or 6 && bitDepth == 8) || (colorType == 3 && bitDepth is 1 or 2 or 4 or 8))
                    || payload[10] != 0 || payload[11] != 0 || payload[12] != 0)
                    throw Invalid("皮肤 PNG 必须使用非交错的 RGB、RGBA 或调色板格式。");
                headerSeen = true;
            }
            else if (kind.SequenceEqual("PLTE"u8))
            {
                if (paletteSeen || transparencySeen || dataSeen || length is 0 or > 768 || length % 3 != 0)
                    throw Invalid("PNG 调色板无效。");
                paletteEntries = (int)length / 3;
                if (colorType == 3 && paletteEntries > 1 << bitDepth) throw Invalid("PNG 调色板超出位深范围。");
                paletteSeen = true;
            }
            else if (kind.SequenceEqual("tRNS"u8))
            {
                if (transparencySeen || dataSeen || colorType == 6
                    || (colorType == 3 && (!paletteSeen || length == 0 || length > paletteEntries))
                    || (colorType == 2 && (length != 6 || payload[0] != 0 || payload[2] != 0 || payload[4] != 0)))
                    throw Invalid("PNG 透明度数据无效。");
                transparencySeen = true;
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                if (dataEnded || (colorType == 3 && !paletteSeen)) throw Invalid("PNG 图像数据顺序或调色板无效。");
                compressed.Write(payload); dataSeen = true;
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !dataSeen || offset + 12 != encoded.Length) throw Invalid("PNG 结束数据块无效或含有额外数据。");
                endSeen = true;
            }
            else if (kind.SequenceEqual("acTL"u8) || kind.SequenceEqual("fcTL"u8) || kind.SequenceEqual("fdAT"u8))
                throw Invalid("皮肤不支持 APNG 动画。");
            else if ((kind[0] & 32) == 0)
                throw Invalid("PNG 包含不支持的关键数据块。");
            offset += (int)length + 12;
        }
        if (!endSeen || compressed.Length == 0) throw Invalid("PNG 缺少完整图像数据或结束数据块。");
        ValidateImageData(compressed.ToArray(), width, height, colorType, bitDepth, paletteEntries);
        return PngImage.TryCreate(encoded) ?? throw Invalid("PNG 图像载体创建失败。");
    }

    private static void ValidateImageData(byte[] compressed, int width, int height, int colorType, int bitDepth, int paletteEntries)
    {
        int channels = colorType switch { 2 => 3, 6 => 4, _ => 1 };
        int rowBytes = (width * channels * bitDepth + 7) / 8;
        int bytesPerPixel = Math.Max(1, channels * bitDepth / 8);
        byte[] scanline = new byte[rowBytes + 1], previous = new byte[rowBytes];
        using ExactCompressedInput input = new(compressed);
        using ZLibStream inflater = new(input, CompressionMode.Decompress, leaveOpen: true);
        uint adlerLow = 1, adlerHigh = 0;
        try
        {
            for (int row = 0; row < height; row++)
            {
                inflater.ReadExactly(scanline);
                foreach (byte value in scanline)
                {
                    adlerLow = (adlerLow + value) % 65521;
                    adlerHigh = (adlerHigh + adlerLow) % 65521;
                }
                int filter = scanline[0];
                if (filter > 4) throw Invalid("PNG 行滤镜无效。");
                Span<byte> pixels = scanline.AsSpan(1);
                for (int index = 0; index < pixels.Length; index++)
                {
                    int left = index >= bytesPerPixel ? pixels[index - bytesPerPixel] : 0;
                    int up = previous[index];
                    int upperLeft = index >= bytesPerPixel ? previous[index - bytesPerPixel] : 0;
                    int predicted = filter switch
                    {
                        1 => left,
                        2 => up,
                        3 => (left + up) / 2,
                        4 => Paeth(left, up, upperLeft),
                        _ => 0,
                    };
                    pixels[index] = unchecked((byte)(pixels[index] + predicted));
                }
                if (colorType == 3)
                    for (int pixel = 0; pixel < width; pixel++)
                    {
                        int bit = pixel * bitDepth;
                        int paletteIndex = (pixels[bit / 8] >> (8 - bitDepth - bit % 8)) & ((1 << bitDepth) - 1);
                        if (paletteIndex >= paletteEntries) throw Invalid("PNG 像素引用了不存在的调色板颜色。");
                    }
                pixels.CopyTo(previous);
            }
            if (inflater.ReadByte() != -1) throw Invalid("PNG 解压后的图像数据超出尺寸范围。");
            if (input.Position != compressed.Length) throw Invalid("PNG 压缩流结束后含有额外数据。");
            if (compressed.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(compressed.Length - 4)) != (adlerHigh << 16 | adlerLow))
                throw Invalid("PNG 压缩数据校验失败。");
        }
        catch (EndOfStreamException failure)
        {
            throw new InvalidDataException("PNG 解压后的图像数据被截断。", failure);
        }
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        int prediction = left + up - upperLeft;
        int leftDistance = Math.Abs(prediction - left), upDistance = Math.Abs(prediction - up), cornerDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= upDistance && leftDistance <= cornerDistance ? left : upDistance <= cornerDistance ? up : upperLeft;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private static uint CalculateCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint crc = index;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            table[index] = crc;
        }
        return table;
    }

    /// <summary>One-byte reads expose the exact zlib end; exhaustion before that end is an error.</summary>
    private sealed class ExactCompressedInput(byte[] bytes) : Stream
    {
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (_offset == bytes.Length) throw Invalid("PNG 压缩流被截断，未到达结束标记。");
            buffer[0] = bytes[_offset++];
            return 1;
        }
        public override int ReadByte()
        {
            if (_offset == bytes.Length) throw Invalid("PNG 压缩流被截断，未到达结束标记。");
            return bytes[_offset++];
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
