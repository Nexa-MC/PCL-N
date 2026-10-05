namespace Nexa.Sidecar.Protocol;

/// <summary>Credit-based, ordered streams scoped to the frame correlation identity.</summary>
public static class SidecarStreamMessages
{
    public const uint MaximumCredit = 16;

    public static byte[] EncodeOpen(uint contractId, SidecarBinaryValue argument, uint initialCredit)
    {
        SidecarDataMessages.RequireContract(contractId);
        ArgumentNullException.ThrowIfNull(argument);
        RequireCredit(initialCredit);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, contractId);
        writer.WriteUInt32(3, argument.CodecId);
        writer.WriteBytes(4, argument.Span);
        writer.WriteUInt32(5, initialCredit);
        return writer.ToArray();
    }

    public static (uint ContractId, SidecarBinaryValue Argument, uint InitialCredit) DecodeOpen(ReadOnlySpan<byte> payload)
    {
        uint contract = 0, credit = 0;
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
                case 5: credit = field.ReadUInt32(); break;
            }
        }
        SidecarDataMessages.RequireContract(contract);
        RequireCredit(credit);
        return (contract, SidecarDataMessages.FinishValue(text, codec, bytes), credit);
    }

    public static byte[] EncodeChunk(ulong sequence, SidecarBinaryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt64(1, sequence);
        writer.WriteUInt32(2, value.CodecId);
        writer.WriteBytes(3, value.Span);
        return writer.ToArray();
    }

    public static (ulong Sequence, SidecarBinaryValue Value) DecodeChunk(ReadOnlySpan<byte> payload)
    {
        ulong? sequence = null;
        uint? codec = null;
        byte[]? bytes = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            switch (field.Id)
            {
                case 1: sequence = field.ReadUInt64(); break;
                case 2: codec = field.ReadUInt32(); break;
                case 3: bytes = field.ReadBytes(); break;
            }
        }
        if (sequence is null || codec is null || bytes is null) throw new SidecarProtocolException("Sidecar stream chunk is incomplete.");
        return (sequence.Value, new SidecarBinaryValue(codec.Value, bytes));
    }

    public static byte[] EncodeEnd(ulong nextSequence, bool success, string? errorCode)
    {
        RequireEnd(success, errorCode ?? string.Empty);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt64(1, nextSequence);
        writer.WriteBoolean(2, success);
        writer.WriteString(3, errorCode ?? string.Empty);
        return writer.ToArray();
    }

    public static (ulong NextSequence, bool Success, string ErrorCode) DecodeEnd(ReadOnlySpan<byte> payload)
    {
        ulong? sequence = null;
        bool? success = null;
        string? error = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            switch (field.Id)
            {
                case 1: sequence = field.ReadUInt64(); break;
                case 2: success = field.ReadBoolean(); break;
                case 3: error = field.ReadString(); break;
            }
        }
        if (sequence is null || success is null || error is null) throw new SidecarProtocolException("Sidecar stream end is incomplete.");
        RequireEnd(success.Value, error);
        return (sequence.Value, success.Value, error);
    }

    public static byte[] EncodeCredit(uint credit)
    {
        RequireCredit(credit);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, credit);
        return writer.ToArray();
    }

    public static uint DecodeCredit(ReadOnlySpan<byte> payload)
    {
        uint credit = 0;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id == 1) credit = field.ReadUInt32();
        }
        RequireCredit(credit);
        return credit;
    }

    private static void RequireCredit(uint credit)
    {
        if (credit is 0 or > MaximumCredit) throw new SidecarProtocolException("Sidecar stream credit is outside its bound.");
    }

    private static void RequireEnd(bool success, string error)
    {
        if ((success && error.Length != 0) || (!success && !SidecarControlMessages.IsStableErrorCode(error)))
            throw new SidecarProtocolException("Sidecar stream termination has an invalid result.");
    }
}
