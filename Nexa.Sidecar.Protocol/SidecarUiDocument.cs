using System.Buffers.Binary;

namespace Nexa.Sidecar.Protocol;

public enum SidecarUiNodeKind : byte { Text = 1, Button = 2, TextInput = 3, Toggle = 4, Image = 5, Stack = 6, Row = 7 }

/// <summary>Literal presentation with references only to this session's declared contracts.</summary>
public sealed record SidecarUiNode(ushort Id, ushort Parent, SidecarUiNodeKind Kind, string Label,
    string Command = "", string ValueState = "", string EnabledState = "", string VisibleState = "",
    string Resource = "", string Argument = "");

/// <summary>A copied finite layout. Parsing happens at registration, never while painting.</summary>
public sealed class SidecarUiDocument
{
    public SidecarUiDocument(string slot, string title, string body, IReadOnlyList<SidecarUiNode> nodes)
    {
        Card = new(slot, title, body);
        _ = Card.Encode();
        ArgumentNullException.ThrowIfNull(nodes);
        Nodes = Array.AsReadOnly(nodes.ToArray());
        Validate(Nodes);
    }
    public SidecarUiCard Card { get; }
    public IReadOnlyList<SidecarUiNode> Nodes { get; }
    public byte[] Encode()
    {
        using var writer = new SidecarPayloadWriter();
        writer.WriteUInt32(1, 2); writer.WriteString(2, Card.Slot); writer.WriteString(3, Card.Title); writer.WriteString(4, Card.Body);
        using var nodes = new MemoryStream();
        Span<byte> prefix = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(prefix, checked((ushort)Nodes.Count)); nodes.Write(prefix);
        foreach (var node in Nodes)
        {
            using var item = new SidecarPayloadWriter();
            item.WriteUInt32(1, node.Id); item.WriteUInt32(2, node.Parent); item.WriteUInt32(3, (uint)node.Kind);
            item.WriteString(4, node.Label); item.WriteString(5, node.Command); item.WriteString(6, node.ValueState);
            item.WriteString(7, node.EnabledState); item.WriteString(8, node.VisibleState); item.WriteString(9, node.Resource); item.WriteString(10, node.Argument);
            var bytes = item.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(prefix, checked((ushort)bytes.Length)); nodes.Write(prefix); nodes.Write(bytes);
        }
        writer.WriteBytes(5, nodes.ToArray());
        var payload = writer.ToArray();
        if (payload.Length > 32 * 1024) throw Invalid();
        return payload;
    }
    public static SidecarUiDocument Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 32 * 1024) throw Invalid();
        var reader = new SidecarPayloadReader(payload); ushort previous = 0; uint schema = 0;
        string? slot = null, title = null, body = null; byte[]? encoded = null;
        while (reader.HasMore)
        {
            var field = reader.ReadNext(); if (field.Id <= previous) throw Invalid(); previous = field.Id;
            switch (field.Id)
            {
                case 1: schema = field.ReadUInt32(); break;
                case 2: slot = field.ReadString(); break;
                case 3: title = field.ReadString(); break;
                case 4: body = field.ReadString(); break;
                case 5: encoded = field.ReadBytes().ToArray(); break;
            }
        }
        if (schema != 2 || encoded is null || encoded.Length < 2) throw Invalid();
        int count = BinaryPrimitives.ReadUInt16LittleEndian(encoded), cursor = 2;
        if (count is < 1 or > 64) throw Invalid();
        var nodes = new SidecarUiNode[count];
        for (int i = 0; i < count; i++)
        {
            if (cursor + 2 > encoded.Length) throw Invalid();
            int length = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(cursor)); cursor += 2;
            if (cursor + length > encoded.Length) throw Invalid();
            var nodeReader = new SidecarPayloadReader(encoded.AsSpan(cursor, length)); cursor += length;
            uint id = 0, parent = 0, kind = 0; var strings = new string[7]; Array.Fill(strings, string.Empty); previous = 0;
            while (nodeReader.HasMore)
            {
                var field = nodeReader.ReadNext(); if (field.Id <= previous) throw Invalid(); previous = field.Id;
                switch (field.Id)
                {
                    case 1: id = field.ReadUInt32(); break;
                    case 2: parent = field.ReadUInt32(); break;
                    case 3: kind = field.ReadUInt32(); break;
                    case >= 4 and <= 10: strings[field.Id - 4] = field.ReadString(); break;
                }
            }
            if (id > ushort.MaxValue || parent > ushort.MaxValue || kind > byte.MaxValue) throw Invalid();
            nodes[i] = new((ushort)id, (ushort)parent, (SidecarUiNodeKind)kind, strings[0], strings[1], strings[2], strings[3], strings[4], strings[5], strings[6]);
        }
        if (cursor != encoded.Length) throw Invalid();
        return new(slot!, title!, body!, nodes);
    }
    public static uint ReadSchema(ReadOnlySpan<byte> payload)
    {
        var reader = new SidecarPayloadReader(payload);
        if (!reader.HasMore) throw Invalid(); var field = reader.ReadNext(); if (field.Id != 1) throw Invalid(); return field.ReadUInt32();
    }
    private static void Validate(IReadOnlyList<SidecarUiNode> nodes)
    {
        if (nodes.Count is < 1 or > 64) throw Invalid();
        int[] depths = new int[nodes.Count + 1];
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.Id != i + 1 || node.Parent >= node.Id || !Enum.IsDefined(node.Kind)
                || node.Label is null || node.Label.Length > 256 || node.Label.Any(char.IsControl)
                || node.Argument is null || node.Argument.Length > 512 || node.Argument.Any(char.IsControl)) throw Invalid();
            if (node.Parent > 0 && nodes[node.Parent - 1].Kind is not (SidecarUiNodeKind.Stack or SidecarUiNodeKind.Row)) throw Invalid();
            if ((depths[node.Id] = depths[node.Parent] + 1) > 8) throw Invalid();
            foreach (var semantic in new[] { node.Command, node.ValueState, node.EnabledState, node.VisibleState, node.Resource })
                if (semantic is null || semantic.Length > 256 || semantic.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) throw Invalid();
            bool interactive = node.Kind is SidecarUiNodeKind.Button or SidecarUiNodeKind.TextInput or SidecarUiNodeKind.Toggle;
            if ((interactive || node.Kind == SidecarUiNodeKind.Image) && string.IsNullOrWhiteSpace(node.Label)
                || node.Kind == SidecarUiNodeKind.Text && node.ValueState.Length == 0 && string.IsNullOrWhiteSpace(node.Label)
                || node.Kind is SidecarUiNodeKind.Image or SidecarUiNodeKind.Stack or SidecarUiNodeKind.Row && node.ValueState.Length != 0) throw Invalid();
            if (interactive != !string.IsNullOrEmpty(node.Command) || (node.Kind == SidecarUiNodeKind.Image) != !string.IsNullOrEmpty(node.Resource)
                || node.Kind == SidecarUiNodeKind.Toggle && string.IsNullOrEmpty(node.ValueState)
                || node.Kind != SidecarUiNodeKind.Button && node.Argument.Length != 0) throw Invalid();
        }
    }
    private static SidecarProtocolException Invalid() => new("Invalid bounded interactive UI document.");
}
