using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

/// <summary>
/// One state value codec: converts between the wire bytes of a declared contract and the typed
/// value its mirror cell holds. Codecs are pure, allocation-bounded, and reflection-free; the
/// generated DTO codec (id 6) treats the DTO blob as schema-contracted opaque bytes whose
/// field-level encode/decode is emitted by the generator.
/// </summary>
public interface ISidecarValueCodec
{
    uint Id { get; }

    /// <summary>
    /// Gets the mirror cell type this codec feeds.
    /// </summary>
    Type ValueType { get; }

    /// <summary>
    /// Validates the wire bytes for this codec, throwing on malformed input.
    /// </summary>
    void Validate(ReadOnlySpan<byte> raw);

    /// <summary>
    /// Decodes the wire bytes into the typed value.
    /// </summary>
    object Decode(ReadOnlySpan<byte> raw);

    /// <summary>
    /// Encodes the typed value into wire bytes.
    /// </summary>
    byte[] Encode(object value);
}

/// <summary>
/// The codec registry. IDs are frozen for the protocol draft: 0 = UTF-8 string, 1 = Bool, 2 =
/// Int32, 3 = Int64, 4 = Float64, 5 = Bytes, 6 = generated DTO blob. Unknown IDs are rejected at
/// registration, so later codec additions are protocol-draft revisions, not silent extensions.
/// </summary>
public static class SidecarValueCodecs
{
    public const uint Utf8String = 0;
    public const uint Bool = 1;
    public const uint I32 = 2;
    public const uint I64 = 3;
    public const uint F64 = 4;
    public const uint Bytes = 5;
    public const uint GeneratedDto = 6;

    private static readonly Dictionary<uint, ISidecarValueCodec> Codecs = new()
    {
        [Utf8String] = new PortableCodec(Utf8String, typeof(string)),
        [Bool] = new PortableCodec(Bool, typeof(bool)),
        [I32] = new PortableCodec(I32, typeof(int)),
        [I64] = new PortableCodec(I64, typeof(long)),
        [F64] = new PortableCodec(F64, typeof(double)),
        [Bytes] = new PortableCodec(Bytes, typeof(byte[])),
        [GeneratedDto] = new PortableCodec(GeneratedDto, typeof(byte[])),
    };

    public static ISidecarValueCodec Get(uint id) =>
        Codecs.TryGetValue(id, out ISidecarValueCodec? codec)
            ? codec
            : throw new SidecarProtocolException($"The state codec {id} is unknown to this protocol draft.");

    /// <summary>
    /// Validates wire bytes for a codec without decoding.
    /// </summary>
    public static void Validate(uint id, ReadOnlySpan<byte> raw) => Get(id).Validate(raw);

    /// <summary>
    /// Decodes wire bytes into the codec's typed value.
    /// </summary>
    public static object Decode(uint id, ReadOnlySpan<byte> raw) => Get(id).Decode(raw);

    public static byte[] Encode(uint id, object value) => Get(id).Encode(value);

    private sealed class PortableCodec(uint id, Type valueType) : ISidecarValueCodec
    {
        public uint Id { get; } = id;

        public Type ValueType { get; } = valueType;

        public void Validate(ReadOnlySpan<byte> raw) => SidecarWireCodecs.Validate(Id, raw);

        public object Decode(ReadOnlySpan<byte> raw) => SidecarWireCodecs.Decode(Id, raw);

        public byte[] Encode(object value) => SidecarWireCodecs.Encode(Id, value);
    }
}
