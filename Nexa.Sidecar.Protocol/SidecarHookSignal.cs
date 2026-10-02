namespace Nexa.Sidecar.Protocol;

/// <summary>A bounded host signal notification, scoped to one activation.</summary>
public sealed record SidecarHookSignal(Guid ActivationId, SidecarRegistrationKind Kind,
    uint ContractId, ulong Sequence, string Value)
{
    public byte[] Encode()
    {
        Validate();
        using var writer = new SidecarPayloadWriter();
        writer.WriteGuid(1, ActivationId);
        writer.WriteUInt32(2, (uint)Kind);
        writer.WriteUInt32(3, ContractId);
        writer.WriteUInt64(4, Sequence);
        writer.WriteString(5, Value);
        return writer.ToArray();
    }

    public static SidecarHookSignal Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4200) throw new SidecarProtocolException("Signal notification byte budget exceeded.");
        var reader = new SidecarPayloadReader(payload);
        Guid activation = default;
        SidecarRegistrationKind kind = default;
        uint contract = 0;
        ulong sequence = 0;
        string? value = null;
        ushort previous = 0;
        while (reader.HasMore)
        {
            var field = reader.ReadNext();
            if (field.Id <= previous) throw new SidecarProtocolException("Signal fields must be ordered and unique.");
            previous = field.Id;
            switch (field.Id)
            {
                case 1: activation = field.ReadGuid(); break;
                case 2: kind = (SidecarRegistrationKind)field.ReadUInt32(); break;
                case 3: contract = field.ReadUInt32(); break;
                case 4: sequence = field.ReadUInt64(); break;
                case 5: value = field.ReadString(); break;
            }
        }
        var signal = new SidecarHookSignal(activation, kind, contract, sequence, value!);
        signal.Validate();
        return signal;
    }

    private void Validate()
    {
        if (ActivationId == Guid.Empty || ContractId == 0 || Sequence == 0 || Value is null || Value.Length > 1024
            || Kind is not (SidecarRegistrationKind.EventCatch or SidecarRegistrationKind.EventListen
                or SidecarRegistrationKind.IntentCatch or SidecarRegistrationKind.IntentWait))
            throw new SidecarProtocolException("Invalid signal notification identity or value.");
    }
}
