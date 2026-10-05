using System.Buffers.Binary;
using System.Text;

namespace Nexa.Sidecar.Protocol;

/// <summary>An owned, validated value. Its bytes never alias caller-owned storage.</summary>
public sealed class SidecarBinaryValue
{
    private readonly byte[] _bytes;

    public SidecarBinaryValue(uint codecId, ReadOnlySpan<byte> bytes)
    {
        SidecarWireCodecs.Validate(codecId, bytes);
        CodecId = codecId;
        _bytes = bytes.ToArray();
    }

    public uint CodecId { get; }
    public int Length => _bytes.Length;
    public ReadOnlySpan<byte> Span => _bytes;
    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Explicit contract adapter; implementations encode fields without reflection.</summary>
public interface ISidecarBinaryCodec<T>
{
    uint CodecId { get; }
    T Decode(ReadOnlySpan<byte> bytes);
    byte[] Encode(T value);
}

/// <summary>Frozen primitive codecs shared by independent peers and Host state mirrors.</summary>
public static class SidecarWireCodecs
{
    public const uint Utf8String = 0;
    public const uint Bool = 1;
    public const uint I32 = 2;
    public const uint I64 = 3;
    public const uint F64 = 4;
    public const uint Bytes = 5;
    public const uint GeneratedDto = 6;
    public const int MaximumValueLength = ushort.MaxValue;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool IsSupported(uint codecId) => codecId <= GeneratedDto;

    public static void Validate(uint codecId, ReadOnlySpan<byte> bytes)
    {
        if (!IsSupported(codecId)) throw new SidecarProtocolException("Unknown Sidecar value codec.");
        if (bytes.Length > MaximumValueLength) throw new SidecarProtocolException("Sidecar value exceeds its byte budget.");
        int expected = codecId switch { Bool => 1, I32 => 4, I64 or F64 => 8, _ => -1 };
        if (expected >= 0 && bytes.Length != expected) throw new SidecarProtocolException("Sidecar value has an invalid primitive shape.");
        if (codecId == Bool && bytes[0] > 1) throw new SidecarProtocolException("Sidecar Boolean must be zero or one.");
        if (codecId == Utf8String)
        {
            try { _ = Utf8.GetCharCount(bytes); }
            catch (DecoderFallbackException) { throw new SidecarProtocolException("Sidecar string is not valid UTF-8."); }
        }
    }

    public static object Decode(uint codecId, ReadOnlySpan<byte> bytes)
    {
        Validate(codecId, bytes);
        return codecId switch
        {
            Utf8String => Utf8.GetString(bytes),
            Bool => bytes[0] == 1,
            I32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            I64 => BinaryPrimitives.ReadInt64LittleEndian(bytes),
            F64 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            _ => bytes.ToArray(),
        };
    }

    public static byte[] Encode(uint codecId, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is string textValue && textValue.Length > MaximumValueLength
            || value is byte[] blobValue && blobValue.Length > MaximumValueLength)
            throw new SidecarProtocolException("Sidecar value exceeds its byte budget.");
        byte[] bytes;
        try
        {
            switch (codecId, value)
            {
                case (Utf8String, string text):
                    if (Utf8.GetByteCount(text) > MaximumValueLength) throw new SidecarProtocolException("Sidecar value exceeds its byte budget.");
                    bytes = Utf8.GetBytes(text); break;
                case (Bool, bool boolean): bytes = [boolean ? (byte)1 : (byte)0]; break;
                case (I32, int integer): bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, integer); break;
                case (I64, long integer): bytes = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(bytes, integer); break;
                case (F64, double number): bytes = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(bytes, number); break;
                case (Bytes or GeneratedDto, byte[] blob): bytes = blob.ToArray(); break;
                default: throw new SidecarProtocolException("Value does not match its Sidecar codec.");
            }
        }
        catch (EncoderFallbackException) { throw new SidecarProtocolException("Sidecar string contains invalid UTF-16."); }
        Validate(codecId, bytes);
        return bytes;
    }

    public static SidecarBinaryValue Encode<T>(ISidecarBinaryCodec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new SidecarBinaryValue(codec.CodecId, codec.Encode(value));
    }

    public static T Decode<T>(ISidecarBinaryCodec<T> codec, SidecarBinaryValue value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(value);
        if (codec.CodecId != value.CodecId) throw new SidecarProtocolException("Typed Sidecar codec does not match the value.");
        return codec.Decode(value.Span);
    }
}
