namespace Nexa.Sidecar.Protocol;

/// <summary>Immutable text-only New UI document; has no action or binding authority.</summary>
public sealed record SidecarUiCard(string Slot, string Title, string Body)
{
    public byte[] Encode()
    {
        Validate(this);
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, 1);
        writer.WriteString(2, Slot);
        writer.WriteString(3, Title);
        writer.WriteString(4, Body);
        return writer.ToArray();
    }

    public static SidecarUiCard Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4096) throw new SidecarProtocolException("UI card payload budget exceeded.");
        var reader = new SidecarPayloadReader(payload);
        uint schema = 0;
        ushort previous = 0;
        string? slot = null, title = null, body = null;
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id <= previous) throw new SidecarProtocolException("UI card fields must be ordered and unique.");
            previous = field.Id;
            switch (field.Id)
            {
                case 1: schema = field.ReadUInt32(); break;
                case 2: slot = field.ReadString(); break;
                case 3: title = field.ReadString(); break;
                case 4: body = field.ReadString(); break;
            }
        }
        if (schema != 1) throw new SidecarProtocolException("Unknown UI card schema.");
        var card = new SidecarUiCard(slot!, title!, body!);
        Validate(card);
        return card;
    }

    private static void Validate(SidecarUiCard card)
    {
        if (string.IsNullOrWhiteSpace(card.Slot) || card.Slot.Length > 256 || card.Slot.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || !IsText(card.Title, 80) || !IsText(card.Body, 512))
            throw new SidecarProtocolException("Invalid UI card text or character budget.");
    }

    private static bool IsText(string? text, int maximum) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximum && !text.Any(char.IsControl);
}
