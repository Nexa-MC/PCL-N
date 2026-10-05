namespace Nexa.Sidecar.Protocol;

/// <summary>Portable numeric data-plane payloads; legacy text fields retain their wire meaning.</summary>
public static class SidecarDataMessages
{
    public static byte[] EncodeRequest(uint contractId, string? argument) => EncodeText(contractId, argument ?? string.Empty);
    public static (uint ContractId, string Argument) DecodeRequest(ReadOnlySpan<byte> payload)
    {
        var decoded = DecodeBinaryRequest(payload);
        return (decoded.ContractId, RequireText(decoded.Value));
    }

    public static byte[] EncodeBinaryRequest(uint contractId, SidecarBinaryValue value) => EncodeBinary(contractId, value);
    public static (uint ContractId, SidecarBinaryValue Value) DecodeBinaryRequest(ReadOnlySpan<byte> payload) => DecodeValue(payload);
    public static (uint ContractId, SidecarBinaryValue Value, bool IsBinary) DecodeBinaryRequestDetails(ReadOnlySpan<byte> payload) => DecodeValueDetails(payload);

    public static byte[] EncodeResult(bool success, string value, string? errorCode)
    {
        using var writer = new SidecarPayloadWriter();
        writer.WriteBoolean(1, success);
        writer.WriteString(2, value);
        writer.WriteString(3, errorCode ?? string.Empty);
        return writer.ToArray();
    }

    public static (bool Success, string Value, string ErrorCode) DecodeResult(ReadOnlySpan<byte> payload)
    {
        var decoded = DecodeBinaryResult(payload);
        return (decoded.Success, RequireText(decoded.Value), decoded.ErrorCode);
    }

    public static byte[] EncodeBinaryResult(bool success, SidecarBinaryValue value, string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var writer = new SidecarPayloadWriter();
        writer.WriteBoolean(1, success);
        writer.WriteString(3, errorCode ?? string.Empty);
        writer.WriteUInt32(4, value.CodecId);
        writer.WriteBytes(5, value.Span);
        return writer.ToArray();
    }

    public static (bool Success, SidecarBinaryValue Value, string ErrorCode) DecodeBinaryResult(ReadOnlySpan<byte> payload)
    {
        var details = DecodeBinaryResultDetails(payload);
        return (details.Success, details.Value, details.ErrorCode);
    }

    public static (bool Success, SidecarBinaryValue Value, string ErrorCode, bool IsBinary) DecodeBinaryResultDetails(ReadOnlySpan<byte> payload)
    {
        bool? success = null;
        string? text = null;
        string error = string.Empty;
        uint? codec = null;
        byte[]? bytes = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            switch (field.Id)
            {
                case 1: success = field.ReadBoolean(); break;
                case 2: text = field.ReadString(); break;
                case 3: error = field.ReadString(); break;
                case 4: codec = field.ReadUInt32(); break;
                case 5: bytes = field.ReadBytes(); break;
            }
        }
        if (success is null) throw new SidecarProtocolException("Sidecar result has no success field.");
        return (success.Value, FinishValue(text, codec, bytes), error, text is null);
    }

    public static byte[] EncodeStateDelta(uint contractId, ReadOnlySpan<byte> encodedValue) => SidecarStateSnapshot.EncodeItem(contractId, encodedValue);
    public static (uint ContractId, byte[] EncodedValue) DecodeStateDelta(ReadOnlySpan<byte> payload) => SidecarStateSnapshot.DecodeItem(payload);
    public static byte[] EncodeEvent(uint contractId, string payload) => EncodeText(contractId, payload);
    public static (uint ContractId, string Payload) DecodeEvent(ReadOnlySpan<byte> payload)
    {
        var decoded = DecodeValue(payload);
        return (decoded.ContractId, RequireText(decoded.Value));
    }
    public static byte[] EncodeBinaryEvent(uint contractId, SidecarBinaryValue value) => EncodeBinary(contractId, value);
    public static (uint ContractId, SidecarBinaryValue Value) DecodeBinaryEvent(ReadOnlySpan<byte> payload) => DecodeValue(payload);
    public static (uint ContractId, SidecarBinaryValue Value, bool IsBinary) DecodeBinaryEventDetails(ReadOnlySpan<byte> payload) => DecodeValueDetails(payload);

    internal static string RequireText(SidecarBinaryValue value)
    {
        if (value.CodecId != SidecarWireCodecs.Utf8String) throw new SidecarProtocolException("Binary Sidecar value cannot be decoded as legacy text.");
        return (string)SidecarWireCodecs.Decode(value.CodecId, value.Span);
    }

    internal static SidecarBinaryValue FinishValue(string? text, uint? codec, byte[]? bytes)
    {
        if (text is not null)
        {
            if (codec is not null || bytes is not null) throw new SidecarProtocolException("Sidecar value mixes text and binary representations.");
            return new SidecarBinaryValue(SidecarWireCodecs.Utf8String, SidecarWireCodecs.Encode(SidecarWireCodecs.Utf8String, text));
        }
        if (codec is null || bytes is null) throw new SidecarProtocolException("Sidecar value has no complete representation.");
        return new SidecarBinaryValue(codec.Value, bytes);
    }

    private static byte[] EncodeText(uint contractId, string value)
    {
        RequireContract(contractId);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, contractId);
        writer.WriteString(2, value);
        return writer.ToArray();
    }

    private static byte[] EncodeBinary(uint contractId, SidecarBinaryValue value)
    {
        RequireContract(contractId);
        ArgumentNullException.ThrowIfNull(value);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, contractId);
        writer.WriteUInt32(3, value.CodecId);
        writer.WriteBytes(4, value.Span);
        return writer.ToArray();
    }

    private static (uint ContractId, SidecarBinaryValue Value) DecodeValue(ReadOnlySpan<byte> payload)
    {
        var details = DecodeValueDetails(payload);
        return (details.ContractId, details.Value);
    }

    private static (uint ContractId, SidecarBinaryValue Value, bool IsBinary) DecodeValueDetails(ReadOnlySpan<byte> payload)
    {
        uint contract = 0;
        string? text = null;
        uint? codec = null;
        byte[]? bytes = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            switch (field.Id)
            {
                case 1: contract = field.ReadUInt32(); break;
                case 2: text = field.ReadString(); break;
                case 3: codec = field.ReadUInt32(); break;
                case 4: bytes = field.ReadBytes(); break;
            }
        }
        RequireContract(contract);
        return (contract, FinishValue(text, codec, bytes), text is null);
    }

    internal static void RequireContract(uint contractId)
    {
        if (contractId == 0) throw new SidecarProtocolException("Sidecar value has no contract ID.");
    }
}
