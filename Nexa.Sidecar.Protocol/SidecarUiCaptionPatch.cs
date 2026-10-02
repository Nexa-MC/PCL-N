namespace Nexa.Sidecar.Protocol;

/// <summary>Versioned presentation data; carries no action, binding or entity authority.</summary>
public static class SidecarUiCaptionPatch
{
    public static byte[] Encode(string caption)
    {
        Validate(caption);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, 1);
        writer.WriteString(2, caption);
        return writer.ToArray();
    }

    public static string Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 2048) throw new SidecarProtocolException("UI caption payload budget exceeded.");
        var reader = new SidecarPayloadReader(payload);
        uint schema = 0;
        ushort previous = 0;
        string? caption = null;
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id <= previous) throw new SidecarProtocolException("UI patch fields must be ordered and unique.");
            previous = field.Id;
            switch (field.Id)
            {
                case 1: schema = field.ReadUInt32(); break;
                case 2: caption = field.ReadString(); break;
            }
        }
        if (schema != 1) throw new SidecarProtocolException("Unknown UI caption schema.");
        Validate(caption);
        return caption!;
    }

    private static void Validate(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption) || caption.Length > 256 || caption.Any(char.IsControl))
            throw new SidecarProtocolException("Invalid UI caption or character budget.");
    }
}
