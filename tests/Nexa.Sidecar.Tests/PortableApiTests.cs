using System.Buffers.Binary;
using Nexa.Sidecar.Protocol;

namespace Nexa.Sidecar.Tests;

internal static partial class Program
{
    private static void PortableApiPreservesLegacyAndNegotiatesExtensions()
    {
        byte[] legacyHello = SidecarHandshake.EncodeHello(1, "legacy");
        AssertEqual(SidecarFeatures.None, SidecarHandshake.DecodeHelloDetails(legacyHello).Features);
        AssertEqual("legacy", SidecarHandshake.DecodeHello(legacyHello).PeerName);
        SidecarFeatures future = SidecarFeatures.All | (SidecarFeatures)0x80000000;
        var hello = SidecarHandshake.DecodeHelloDetails(SidecarHandshake.EncodeHello(1, "peer", future));
        AssertEqual(future, hello.Features);
        Guid session = Guid.NewGuid();
        byte[] welcome = SidecarHandshake.EncodeWelcome(1, session, "notice", SidecarFeatures.All);
        AssertEqual(session, SidecarHandshake.DecodeWelcome(welcome).SessionId);
        AssertEqual("notice", SidecarHandshake.DecodeWelcomeDetails(welcome).Notice);
        AssertEqual(SidecarFeatures.All, SidecarHandshake.DecodeWelcomeDetails(welcome).Features);
        AssertEqual(SidecarFeatures.None, SidecarHandshake.DecodeWelcomeDetails(SidecarHandshake.EncodeWelcome(1, session)).Features);

        SidecarRegistrationItem original = new(SidecarRegistrationKind.Query, "plugin.query", 0, 0, null, null, null, null);
        var (kind, semantic, flags, codec, content, hash, required, target) = original;
        AssertEqual(SidecarRegistrationKind.Query, kind);
        AssertEqual("plugin.query", semantic);
        AssertEqual(0u, flags);
        AssertEqual(0u, codec);
        AssertTrue(content is null && hash is null && required is null && target is null);
        AssertEqual(0u, SidecarRegistration.DecodeItem(SidecarRegistration.EncodeItem(original)).ResultCodecId);
        var typed = original with { CodecId = SidecarWireCodecs.Bool, ResultCodecId = SidecarWireCodecs.I64 };
        var decoded = SidecarRegistration.DecodeItem(SidecarRegistration.EncodeItem(typed));
        AssertEqual(typed.CodecId, decoded.CodecId);
        AssertEqual(typed.ResultCodecId, decoded.ResultCodecId);
        var stream = new SidecarRegistrationItem(SidecarRegistrationKind.Stream, "plugin.stream", 0, SidecarWireCodecs.Bytes)
        { ResultCodecId = SidecarWireCodecs.GeneratedDto };
        AssertEqual(stream, SidecarRegistration.DecodeItem(SidecarRegistration.EncodeItem(stream)));
        AssertEqual((ushort)20, (ushort)SidecarMessageType.Unregister);
        AssertEqual((ushort)21, (ushort)SidecarMessageType.Unregistered);
        AssertEqual((ushort)25, (ushort)SidecarMessageType.Error);
        AssertEqual((ushort)81, (ushort)SidecarMessageType.StreamOpen);
        AssertEqual((ushort)82, (ushort)SidecarMessageType.StreamEnd);
        AssertEqual((ushort)83, (ushort)SidecarMessageType.StreamCredit);
    }

    private static void PortableValuesAndDataMessagesRoundTrip()
    {
        (uint Codec, object Value)[] values = [(0, "中文"), (1, true), (2, -123), (3, long.MinValue),
            (4, 3.125), (5, new byte[] { 1, 2, 3 }), (6, new byte[] { 7, 8 })];
        foreach (var (codec, value) in values)
        {
            byte[] bytes = SidecarWireCodecs.Encode(codec, value);
            var owned = new SidecarBinaryValue(codec, bytes);
            byte[] retained = owned.ToArray();
            if (bytes.Length > 0) bytes[0] ^= 0xff;
            AssertTrue(owned.Span.SequenceEqual(retained));
            byte[] exposed = owned.ToArray();
            if (exposed.Length > 0) exposed[0] ^= 0xff;
            AssertTrue(owned.Span.SequenceEqual(retained));
            var request = SidecarDataMessages.DecodeBinaryRequest(SidecarDataMessages.EncodeBinaryRequest(9, owned));
            AssertEqual(9u, request.ContractId);
            AssertEqual(codec, request.Value.CodecId);
            AssertTrue(SidecarDataMessages.DecodeBinaryRequestDetails(SidecarDataMessages.EncodeBinaryRequest(9, owned)).IsBinary);
            AssertTrue(owned.Span.SequenceEqual(request.Value.Span));
            var result = SidecarDataMessages.DecodeBinaryResult(SidecarDataMessages.EncodeBinaryResult(true, owned, null));
            AssertTrue(result.Success);
            AssertEqual(codec, result.Value.CodecId);
            AssertTrue(owned.Span.SequenceEqual(result.Value.Span));
            AssertTrue(SidecarDataMessages.DecodeBinaryResultDetails(SidecarDataMessages.EncodeBinaryResult(true, owned, null)).IsBinary);
            var @event = SidecarDataMessages.DecodeBinaryEvent(SidecarDataMessages.EncodeBinaryEvent(7, owned));
            AssertEqual(7u, @event.ContractId);
            AssertTrue(owned.Span.SequenceEqual(@event.Value.Span));
            object recovered = SidecarWireCodecs.Decode(codec, owned.Span);
            if (value is byte[] blob) AssertTrue(blob.SequenceEqual((byte[])recovered));
            else AssertEqual(value, recovered);
        }
        AssertEqual("legacy", SidecarDataMessages.DecodeBinaryRequest(SidecarDataMessages.EncodeRequest(1, "legacy")).Value is { } legacy
            ? (string)SidecarWireCodecs.Decode(legacy.CodecId, legacy.Span) : string.Empty);
        AssertFalse(SidecarDataMessages.DecodeBinaryRequestDetails(SidecarDataMessages.EncodeRequest(1, "legacy")).IsBinary);
        AssertFalse(SidecarDataMessages.DecodeBinaryResultDetails(SidecarDataMessages.EncodeResult(true, "legacy", null)).IsBinary);
        AssertFalse(SidecarDataMessages.DecodeBinaryEventDetails(SidecarDataMessages.EncodeEvent(1, "legacy")).IsBinary);
        AssertEqual("", SidecarDataMessages.DecodeRequest(SidecarDataMessages.EncodeRequest(1, null)).Argument);
        AssertEqual("result", SidecarDataMessages.DecodeResult(SidecarDataMessages.EncodeResult(true, "result", null)).Value);
        AssertEqual("event", SidecarDataMessages.DecodeEvent(SidecarDataMessages.EncodeEvent(1, "event")).Payload);
        var delta = SidecarDataMessages.DecodeStateDelta(SidecarDataMessages.EncodeStateDelta(3, [4, 5]));
        AssertEqual(3u, delta.ContractId);
        AssertTrue(delta.EncodedValue.SequenceEqual(new byte[] { 4, 5 }));
        var adapter = new PortableIntegerAdapter();
        AssertEqual(123, SidecarWireCodecs.Decode(adapter, SidecarWireCodecs.Encode(adapter, 123)));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Decode(adapter, new SidecarBinaryValue(0, [])));
    }

    private static void PortableMessagesRejectAmbiguousAndMalformedValues()
    {
        using var mixed = new SidecarPayloadWriter();
        mixed.WriteUInt32(1, 1); mixed.WriteString(2, "legacy"); mixed.WriteUInt32(3, 0); mixed.WriteBytes(4, []);
        AssertThrows<SidecarProtocolException>(() => SidecarDataMessages.DecodeBinaryRequest(mixed.ToArray()));
        using var missing = new SidecarPayloadWriter();
        missing.WriteUInt32(1, 1); missing.WriteUInt32(3, 1);
        AssertThrows<SidecarProtocolException>(() => SidecarDataMessages.DecodeBinaryRequest(missing.ToArray()));
        AssertThrows<SidecarProtocolException>(() => SidecarDataMessages.DecodeBinaryResult([]));
        AssertThrows<SidecarProtocolException>(() => SidecarDataMessages.EncodeRequest(0, "bad"));
        AssertThrows<SidecarProtocolException>(() => SidecarDataMessages.DecodeRequest(
            SidecarDataMessages.EncodeBinaryRequest(1, new SidecarBinaryValue(1, [1]))));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Validate(0, [0xc3, 0x28]));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Validate(1, [2]));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Validate(2, [0]));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Validate(7, []));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Validate(5, new byte[65536]));
        AssertThrows<SidecarProtocolException>(() => SidecarWireCodecs.Encode(0, "\ud800"));
        using var writer = new SidecarPayloadWriter();
        writer.WriteBoolean(1, true);
        byte[] duplicate = [.. writer.ToArray(), .. writer.ToArray()];
        AssertThrows<SidecarProtocolException>(() => ReadAllPortableFields(duplicate));
        byte[] noncanonicalBool = writer.ToArray(); noncanonicalBool[^1] = 2;
        AssertThrows<SidecarProtocolException>(() => ReadAs<bool>(noncanonicalBool, field => field.ReadBoolean()));
        using var text = new SidecarPayloadWriter(); text.WriteString(1, "xy");
        byte[] malformedText = text.ToArray(); malformedText[^2] = 0xc3; malformedText[^1] = 0x28;
        AssertThrows<SidecarProtocolException>(() => ReadAs<string>(malformedText, field => field.ReadString()));
        byte[] trailing = [.. SidecarRegistration.EncodeBegin(1), 0xff];
        AssertThrows<SidecarProtocolException>(() => SidecarRegistration.DecodeBegin(trailing));
        AssertThrows<SidecarProtocolException>(() => SidecarStateSnapshot.DecodeBegin(trailing));
        AssertThrows<SidecarProtocolException>(() => SidecarRegistration.DecodeEnd([0xff]));
        AssertThrows<SidecarProtocolException>(() => SidecarStateSnapshot.DecodeEnd([0xff]));
        using var future = new SidecarPayloadWriter();
        future.WriteUInt32(1, 3); future.WriteString(2, "argument"); future.WriteBytes(99, [8, 9]);
        AssertEqual("argument", SidecarDataMessages.DecodeRequest(future.ToArray()).Argument);
    }

    private static void PortableControlAndStreamMessagesRoundTrip()
    {
        AssertEqual(ulong.MaxValue, SidecarControlMessages.DecodeHealth(SidecarControlMessages.EncodeHealth(ulong.MaxValue)));
        AssertEqual(("plugin.failed", "Diagnostic"), SidecarControlMessages.DecodeFailure(SidecarControlMessages.EncodeFailure("plugin.failed", "Diagnostic")));
        AssertEqual("host stopping", SidecarControlMessages.DecodeUnregister(SidecarControlMessages.EncodeUnregister("host stopping")));
        Guid cancellation = Guid.NewGuid();
        AssertEqual((cancellation, "cancelled"), SidecarControlMessages.DecodeCancel(SidecarControlMessages.EncodeCancel(cancellation, "cancelled")));
        var argument = new SidecarBinaryValue(2, SidecarWireCodecs.Encode(2, 4));
        var open = SidecarStreamMessages.DecodeOpen(SidecarStreamMessages.EncodeOpen(7, argument, 16));
        AssertEqual(7u, open.ContractId); AssertEqual(16u, open.InitialCredit);
        AssertEqual(4, SidecarWireCodecs.Decode(open.Argument.CodecId, open.Argument.Span));
        var value = new SidecarBinaryValue(5, new byte[65535]);
        var chunk = SidecarStreamMessages.DecodeChunk(SidecarStreamMessages.EncodeChunk(0, value));
        AssertEqual(0ul, chunk.Sequence); AssertEqual(65535, chunk.Value.Length);
        AssertEqual((1ul, true, ""), SidecarStreamMessages.DecodeEnd(SidecarStreamMessages.EncodeEnd(1, true, null)));
        AssertEqual((2ul, false, "xsr.cancelled"), SidecarStreamMessages.DecodeEnd(SidecarStreamMessages.EncodeEnd(2, false, "xsr.cancelled")));
        AssertEqual(1u, SidecarStreamMessages.DecodeCredit(SidecarStreamMessages.EncodeCredit(1)));
        AssertThrows<SidecarProtocolException>(() => SidecarControlMessages.EncodeHealth(0));
        AssertThrows<SidecarProtocolException>(() => SidecarControlMessages.DecodeHealth([]));
        AssertThrows<SidecarProtocolException>(() => SidecarControlMessages.EncodeFailure("bad code", "message"));
        AssertThrows<SidecarProtocolException>(() => SidecarControlMessages.EncodeFailure("ok", "line\nsecret"));
        AssertThrows<SidecarProtocolException>(() => SidecarControlMessages.EncodeUnregister(new string('x', 1025)));
        AssertThrows<SidecarProtocolException>(() => SidecarStreamMessages.EncodeOpen(1, argument, 17));
        AssertThrows<SidecarProtocolException>(() => SidecarStreamMessages.DecodeChunk([]));
        AssertThrows<SidecarProtocolException>(() => SidecarStreamMessages.EncodeEnd(0, false, null));
        AssertThrows<SidecarProtocolException>(() => SidecarStreamMessages.EncodeCredit(0));
        AssertThrows<SidecarProtocolException>(() => SidecarStreamMessages.DecodeEnd([]));
    }

    private static void FrameHeadersValidateMetadataAndCombinedTraits()
    {
        var frame = BuildFrame() with { Flags = SidecarFrameTraits.Compressed | SidecarFrameTraits.Final };
        byte[] wire = EncodeFrame(frame);
        AssertEqual(frame.Flags, SidecarFrameCodec.Decode(wire).Flags);
        AssertEqual(frame.Payload.Length, SidecarFrameCodec.ValidateHeader(wire.AsSpan(0, 32)));
        wire.AsSpan(12, 16).Clear();
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.Decode(wire));
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.GetFrameSize(-1));
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.GetFrameSize(int.MaxValue));
        AssertThrows<SidecarProtocolException>(() => SidecarFrameCodec.ValidateHeader(new byte[31]));
        AssertThrows<SidecarProtocolException>(() => EncodeFrame(frame with { Flags = (SidecarFrameTraits)4 }));
        AssertThrows<SidecarProtocolException>(() => EncodeFrame(frame with { ProtocolVersion = 2 }));
    }

    private static void ReadAllPortableFields(byte[] bytes)
    {
        var reader = new SidecarPayloadReader(bytes);
        while (reader.HasMore) _ = reader.ReadNext();
    }

    private sealed class PortableIntegerAdapter : ISidecarBinaryCodec<int>
    {
        public uint CodecId => SidecarWireCodecs.I32;
        public int Decode(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadInt32LittleEndian(bytes);
        public byte[] Encode(int value) => SidecarWireCodecs.Encode(CodecId, value);
    }
}
