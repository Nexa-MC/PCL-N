namespace Nexa.Sidecar.Protocol;

/// <summary>Bounded control-plane payloads that never expose CLR exception objects.</summary>
public static class SidecarControlMessages
{
    public static byte[] EncodeCancel(Guid correlationId, string reason) => SidecarStateSnapshot.EncodeCancel(correlationId, reason);
    public static (Guid CorrelationId, string Reason) DecodeCancel(ReadOnlySpan<byte> payload) => SidecarStateSnapshot.DecodeCancel(payload);

    public static byte[] EncodeHealth(ulong nonce)
    {
        if (nonce == 0) throw new SidecarProtocolException("Health nonce must be assigned.");
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt64(1, nonce);
        return writer.ToArray();
    }

    public static ulong DecodeHealth(ReadOnlySpan<byte> payload)
    {
        RequireBudget(payload);
        ulong nonce = 0;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id == 1) nonce = field.ReadUInt64();
        }
        if (nonce == 0) throw new SidecarProtocolException("Health nonce must be assigned.");
        return nonce;
    }

    public static byte[] EncodeFailure(string code, string message)
    {
        ValidateFailure(code, message);
        using var writer = new SidecarPayloadWriter();
        writer.WriteString(1, code);
        writer.WriteString(2, message);
        return writer.ToArray();
    }

    public static (string Code, string Message) DecodeFailure(ReadOnlySpan<byte> payload)
    {
        RequireBudget(payload);
        string? code = null, message = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id == 1) code = field.ReadString();
            else if (field.Id == 2) message = field.ReadString();
        }
        ValidateFailure(code, message);
        return (code!, message!);
    }

    public static byte[] EncodeUnregister(string reason)
    {
        ValidateText(reason);
        using var writer = new SidecarPayloadWriter();
        writer.WriteString(1, reason);
        return writer.ToArray();
    }

    public static string DecodeUnregister(ReadOnlySpan<byte> payload)
    {
        RequireBudget(payload);
        string? reason = null;
        var reader = new SidecarPayloadReader(payload);
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id == 1) reason = field.ReadString();
        }
        ValidateText(reason);
        return reason!;
    }

    public static bool IsStableErrorCode(string? code) => !string.IsNullOrEmpty(code) && code.Length <= 256
        && code.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
        && code[0] is >= 'a' and <= 'z';

    internal static void ValidateFailure(string? code, string? message)
    {
        if (!IsStableErrorCode(code)) throw new SidecarProtocolException("Sidecar failure code is invalid.");
        ValidateText(message);
    }

    internal static void ValidateText(string? text)
    {
        if (text is null || text.Length > 1024 || text.Any(char.IsControl)) throw new SidecarProtocolException("Sidecar control text is invalid or exceeds its budget.");
    }

    private static void RequireBudget(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 6144) throw new SidecarProtocolException("Sidecar control payload exceeds its budget.");
    }
}
