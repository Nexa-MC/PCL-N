namespace Nexa.Sidecar.Protocol;

/// <summary>
/// Encodes and decodes the HELLO and WELCOME handshake payloads. Both sides validate the
/// protocol version; the session id is assigned by the accepting side.
/// </summary>
public static class SidecarHandshake
{
    /// <summary>
    /// Encodes a HELLO payload: the sender's protocol version and its peer name.
    /// </summary>
    public static byte[] EncodeHello(uint protocolVersion, string peerName)
        => EncodeHello(protocolVersion, peerName, SidecarFeatures.None);

    public static byte[] EncodeHello(uint protocolVersion, string peerName, SidecarFeatures features)
    {
        using SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, protocolVersion);
        writer.WriteString(2, peerName);
        if (features != SidecarFeatures.None) writer.WriteUInt32(3, (uint)features);
        return writer.ToArray();
    }

    public static (uint ProtocolVersion, string PeerName) DecodeHello(ReadOnlySpan<byte> payload)
    {
        var details = DecodeHelloDetails(payload);
        return (details.ProtocolVersion, details.PeerName);
    }

    public static (uint ProtocolVersion, string PeerName, SidecarFeatures Features) DecodeHelloDetails(ReadOnlySpan<byte> payload)
    {
        uint version = 0;
        string name = string.Empty;
        SidecarFeatures features = SidecarFeatures.None;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    version = field.ReadUInt32();
                    break;
                case 2:
                    name = field.ReadString();
                    break;
                case 3:
                    features = (SidecarFeatures)field.ReadUInt32();
                    break;
            }
        }

        if (version == 0)
        {
            throw new SidecarProtocolException("The HELLO payload carries no protocol version.");
        }

        return (version, name, features);
    }

    /// <summary>
    /// Encodes a WELCOME payload: the negotiated protocol version and the session identity.
    /// </summary>
    public static byte[] EncodeWelcome(uint negotiatedVersion, Guid sessionId) =>
        EncodeWelcome(negotiatedVersion, sessionId, null);

    public static byte[] EncodeWelcome(uint negotiatedVersion, Guid sessionId, string? notice)
        => EncodeWelcome(negotiatedVersion, sessionId, notice, SidecarFeatures.None);

    public static byte[] EncodeWelcome(uint negotiatedVersion, Guid sessionId, string? notice, SidecarFeatures features)
    {
        using SidecarPayloadWriter writer = new();
        writer.WriteUInt32(1, negotiatedVersion);
        writer.WriteGuid(2, sessionId);
        if (notice is not null)
        {
            writer.WriteString(3, notice);
        }
        if (features != SidecarFeatures.None) writer.WriteUInt32(4, (uint)features);

        return writer.ToArray();
    }

    public static (uint NegotiatedVersion, Guid SessionId) DecodeWelcome(ReadOnlySpan<byte> payload)
    {
        var details = DecodeWelcomeDetails(payload);
        return (details.NegotiatedVersion, details.SessionId);
    }

    public static (uint NegotiatedVersion, Guid SessionId, string? Notice, SidecarFeatures Features) DecodeWelcomeDetails(ReadOnlySpan<byte> payload)
    {
        uint version = 0;
        Guid sessionId = Guid.Empty;
        string? notice = null;
        SidecarFeatures features = SidecarFeatures.None;
        SidecarPayloadReader reader = new(payload);
        while (reader.HasMore)
        {
            SidecarPayloadField field = reader.ReadNext();
            switch (field.Id)
            {
                case 1:
                    version = field.ReadUInt32();
                    break;
                case 2:
                    sessionId = field.ReadGuid();
                    break;
                case 3: notice = field.ReadString(); break;
                case 4: features = (SidecarFeatures)field.ReadUInt32(); break;
            }
        }

        if (version == 0 || sessionId == Guid.Empty)
        {
            throw new SidecarProtocolException("The WELCOME payload is missing the version or session id.");
        }

        return (version, sessionId, notice, features);
    }
}
