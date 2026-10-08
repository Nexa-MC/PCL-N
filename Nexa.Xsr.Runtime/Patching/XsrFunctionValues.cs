using System.Buffers;
using System.Buffers.Binary;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

#pragma warning disable CA1720 // Protocol ABI kind names intentionally match frozen primitive codec names.
public enum XsrFunctionValueKind : byte { String = 0, Boolean = 1, Int32 = 2, Int64 = 3, Float64 = 4, Void = 7 }
#pragma warning restore CA1720

/// <summary>A finite local ABI value. No object, delegate or reflected type is wire data.</summary>
public readonly record struct XsrFunctionValue
{
    private readonly string? _text;
    private readonly long _integer;
    private readonly double _number;
    private XsrFunctionValue(XsrFunctionValueKind kind, string? text = null, long integer = 0, double number = 0)
    { Kind = kind; _text = text; _integer = integer; _number = number; }
    public XsrFunctionValueKind Kind { get; }
    public static XsrFunctionValue From(string value) => (value ?? throw new ArgumentNullException(nameof(value))).Length <= XsrFunctionPatchRuntime.MaximumStringCharacters
        ? new(XsrFunctionValueKind.String, value) : throw new ArgumentException("Function string budget exceeded.", nameof(value));
    public static XsrFunctionValue From(bool value) => new(XsrFunctionValueKind.Boolean, integer: value ? 1 : 0);
    public static XsrFunctionValue From(int value) => new(XsrFunctionValueKind.Int32, integer: value);
    public static XsrFunctionValue From(long value) => new(XsrFunctionValueKind.Int64, integer: value);
    public static XsrFunctionValue From(double value) => double.IsFinite(value) ? new(XsrFunctionValueKind.Float64, number: value)
        : throw new ArgumentException("Function number must be finite.", nameof(value));
    public static XsrFunctionValue Empty => new(XsrFunctionValueKind.Void);
    public string AsString() => Kind == XsrFunctionValueKind.String ? _text! : throw Mismatch();
    public bool AsBoolean() => Kind == XsrFunctionValueKind.Boolean ? _integer != 0 : throw Mismatch();
    public int AsInt32() => Kind == XsrFunctionValueKind.Int32 ? (int)_integer : throw Mismatch();
    public long AsInt64() => Kind == XsrFunctionValueKind.Int64 ? _integer : throw Mismatch();
    public double AsFloat64() => Kind == XsrFunctionValueKind.Float64 ? _number : throw Mismatch();
    private static InvalidOperationException Mismatch() => new("Function value shape mismatch.");
    internal static XsrFunctionValue Default(XsrFunctionValueKind kind) => kind switch
    {
        XsrFunctionValueKind.String => From(string.Empty),
        XsrFunctionValueKind.Boolean => From(false),
        XsrFunctionValueKind.Int32 => From(0),
        XsrFunctionValueKind.Int64 => From(0L),
        XsrFunctionValueKind.Float64 => From(0d),
        _ => Empty
    };
    internal static XsrFunctionValue Add(XsrFunctionValue left, XsrFunctionValue right) => left.Kind == right.Kind ? left.Kind switch
    {
        XsrFunctionValueKind.String => From(left.AsString() + right.AsString()),
        XsrFunctionValueKind.Int32 => From(checked(left.AsInt32() + right.AsInt32())),
        XsrFunctionValueKind.Int64 => From(checked(left.AsInt64() + right.AsInt64())),
        XsrFunctionValueKind.Float64 => From(left.AsFloat64() + right.AsFloat64()),
        _ => throw Mismatch()
    } : throw Mismatch();
}

public sealed class XsrFunctionShape
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Names the compatible string ABI shape rather than a product identifier.")]
    public static XsrFunctionShape String { get; } = new(XsrFunctionValueKind.String, XsrFunctionValueKind.String);
    public XsrFunctionShape(XsrFunctionValueKind result, params XsrFunctionValueKind[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Valid(result) || arguments.Length > 8 || arguments.Any(kind => !Valid(kind) || kind == XsrFunctionValueKind.Void))
            throw new ArgumentException("Invalid bounded function shape.");
        Result = result; Arguments = Array.AsReadOnly(arguments.ToArray());
    }
    public XsrFunctionValueKind Result { get; }
    public IReadOnlyList<XsrFunctionValueKind> Arguments { get; }
    internal bool IsString => Result == XsrFunctionValueKind.String && Arguments.Count == 1 && Arguments[0] == XsrFunctionValueKind.String;
    internal static bool Valid(XsrFunctionValueKind kind) => kind is >= XsrFunctionValueKind.String and <= XsrFunctionValueKind.Float64 or XsrFunctionValueKind.Void;
}

public sealed record XsrFunctionTarget(XsrSemanticId Target, XsrFunctionShape Shape);
public delegate XsrFunctionValue XsrFunctionOriginal(ReadOnlySpan<XsrFunctionValue> arguments);

public sealed partial class XsrFunctionPatchRuntime
{
    public bool HasPatches(XsrFunctionPatchPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (point.Owner != this) throw new ArgumentException("Point belongs to another runtime.", nameof(point));
        return Volatile.Read(ref _table)[point.Index].Length != 0;
    }

    public XsrFunctionValue InvokeValues(XsrFunctionPatchPoint point, ReadOnlySpan<XsrFunctionValue> arguments,
        XsrFunctionOriginal original, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        _ = HasPatches(point);
        cancellationToken.ThrowIfCancellationRequested();
        if (arguments.Length != point.Shape.Arguments.Count) throw new ArgumentException("Function argument count mismatch.", nameof(arguments));
        for (int i = 0; i < arguments.Length; i++)
            if (arguments[i].Kind != point.Shape.Arguments[i] || arguments[i].Kind == XsrFunctionValueKind.String && arguments[i].AsString() is null) throw new ArgumentException("Function argument shape mismatch.", nameof(arguments));
        var programs = Volatile.Read(ref _table)[point.Index];
        if (programs.Length == 0 || _depth == 32) return original(arguments);
        _calls ??= new long[32];
        for (int i = 0; i < _depth; i++) if (_calls[i] == point.CallId) return original(arguments);
        _calls[_depth++] = point.CallId;
        var owned = ArrayPool<XsrFunctionValue>.Shared.Rent(8);
        arguments.CopyTo(owned);
        try
        {
            var result = XsrFunctionValue.Default(point.Shape.Result);
            bool skip = false; int budget = 512;
            RunPhase(XsrFunctionPatchPhase.Head); RunPhase(XsrFunctionPatchPhase.Args); RunPhase(XsrFunctionPatchPhase.Replace);
            if (!skip) result = original(owned.AsSpan(0, arguments.Length));
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Kind != point.Shape.Result) throw new InvalidOperationException("Original function result shape mismatch.");
            RunPhase(XsrFunctionPatchPhase.Tail); RunPhase(XsrFunctionPatchPhase.Return);
            return result;

            void RunPhase(XsrFunctionPatchPhase phase)
            {
                foreach (var active in programs)
                {
                    if (active.Program.Phase != phase || !active.CanRun) continue;
                    if (phase == XsrFunctionPatchPhase.Replace && skip) break;
                    if (active.Program.Typed is null) continue;
                    var nextArgs = ArrayPool<XsrFunctionValue>.Shared.Rent(8);
                    owned.AsSpan(0, point.Shape.Arguments.Count).CopyTo(nextArgs);
                    var nextResult = result; bool nextSkip = skip;
                    try
                    {
                        if (active.RunTyped(nextArgs, ref nextResult, ref nextSkip, ref budget, cancellationToken))
                        { nextArgs.AsSpan(0, point.Shape.Arguments.Count).CopyTo(owned); result = nextResult; skip = nextSkip; }
                    }
                    finally { ArrayPool<XsrFunctionValue>.Shared.Return(nextArgs, true); }
                }
            }
        }
        finally { ArrayPool<XsrFunctionValue>.Shared.Return(owned, true); _calls[--_depth] = 0; }
    }
}

internal readonly record struct XsrTypedPatchInstruction(byte Opcode, byte Argument = 0, XsrFunctionValue Constant = default);

internal sealed partial class XsrFunctionPatchProgram
{
    internal XsrTypedPatchInstruction[]? Typed { get; private init; }
    // NFP2, version, phase, argument count, result kind, argument kinds, u16 instruction count.
    // 1 + u8 argument: load argument; 2 result; 3 + kind + u16 length + bytes: constant;
    // 4 add/concat; 5 + u8 argument: store argument; 6 result; 7 skip; 8 end; 9 equality; 10 Boolean not.
    private static XsrFunctionPatchProgram DecodeTyped(ReadOnlySpan<byte> bytes, XsrFunctionShape shape)
    {
        if (bytes.Length is < 11 or > 8192 || bytes[4] != 1 || !Enum.IsDefined((XsrFunctionPatchPhase)bytes[5])
            || bytes[6] != shape.Arguments.Count || bytes[7] != (byte)shape.Result || bytes.Length < 10 + shape.Arguments.Count) throw Invalid();
        var phase = (XsrFunctionPatchPhase)bytes[5];
        for (int i = 0; i < shape.Arguments.Count; i++) if (bytes[8 + i] != (byte)shape.Arguments[i]) throw Invalid();
        int cursor = 8 + shape.Arguments.Count;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]); cursor += 2;
        if (count is < 1 or > 64) throw Invalid();
        var instructions = new XsrTypedPatchInstruction[count];
        var stack = new XsrFunctionValueKind[16]; int top = 0; bool resultSet = false, skip = false;
        for (int i = 0; i < count; i++)
        {
            if (cursor >= bytes.Length) throw Invalid();
            byte op = bytes[cursor++], argument = 0; XsrFunctionValue value = default;
            switch (op)
            {
                case 1:
                    if (cursor >= bytes.Length || (argument = bytes[cursor++]) >= shape.Arguments.Count || top == 16) throw Invalid();
                    stack[top++] = shape.Arguments[argument]; break;
                case 2:
                    if (phase is not (XsrFunctionPatchPhase.Tail or XsrFunctionPatchPhase.Return) || shape.Result == XsrFunctionValueKind.Void || top == 16) throw Invalid();
                    stack[top++] = shape.Result; break;
                case 3:
                    if (cursor + 3 > bytes.Length || top == 16) throw Invalid();
                    var kind = (XsrFunctionValueKind)bytes[cursor++]; int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]); cursor += 2;
                    if (!XsrFunctionShape.Valid(kind) || kind == XsrFunctionValueKind.Void || length > 1024 || cursor + length > bytes.Length) throw Invalid();
                    var raw = bytes.Slice(cursor, length); cursor += length;
                    SidecarWireCodecs.Validate((uint)kind, raw);
                    value = kind switch
                    {
                        XsrFunctionValueKind.String => XsrFunctionValue.From(StrictUtf8.GetString(raw)),
                        XsrFunctionValueKind.Boolean => XsrFunctionValue.From(raw[0] != 0),
                        XsrFunctionValueKind.Int32 => XsrFunctionValue.From(BinaryPrimitives.ReadInt32LittleEndian(raw)),
                        XsrFunctionValueKind.Int64 => XsrFunctionValue.From(BinaryPrimitives.ReadInt64LittleEndian(raw)),
                        _ => double.IsFinite(BinaryPrimitives.ReadDoubleLittleEndian(raw)) ? XsrFunctionValue.From(BinaryPrimitives.ReadDoubleLittleEndian(raw)) : throw Invalid()
                    };
                    stack[top++] = kind; break;
                case 4:
                    if (top < 2 || stack[top - 1] != stack[top - 2] || stack[top - 1] == XsrFunctionValueKind.Boolean) throw Invalid();
                    top--; break;
                case 5:
                    if (cursor >= bytes.Length || (argument = bytes[cursor++]) >= shape.Arguments.Count || phase != XsrFunctionPatchPhase.Args
                        || top == 0 || stack[--top] != shape.Arguments[argument]) throw Invalid(); break;
                case 6:
                    if (top == 0 || phase == XsrFunctionPatchPhase.Args || stack[--top] != shape.Result) throw Invalid(); resultSet = true; break;
                case 7:
                    if (phase is not (XsrFunctionPatchPhase.Head or XsrFunctionPatchPhase.Replace) || (!resultSet && shape.Result != XsrFunctionValueKind.Void)) throw Invalid(); skip = true; break;
                case 8:
                    if (i != count - 1 || top != 0 || phase == XsrFunctionPatchPhase.Replace && !skip) throw Invalid(); break;
                case 9:
                    if (top < 2 || stack[top - 1] != stack[top - 2]) throw Invalid(); top--; stack[top - 1] = XsrFunctionValueKind.Boolean; break;
                case 10: if (top == 0 || stack[top - 1] != XsrFunctionValueKind.Boolean) throw Invalid(); break;
                default: throw Invalid();
            }
            if (i == count - 1 && op != 8) throw Invalid();
            instructions[i] = new(op, argument, value);
        }
        if (cursor != bytes.Length) throw Invalid();
        return new(phase, []) { Typed = instructions };
    }
}

internal sealed partial class XsrActiveFunctionPatch
{
    internal bool RunTyped(XsrFunctionValue[] arguments, ref XsrFunctionValue result, ref bool skip, ref int budget, CancellationToken token)
    {
        var stack = ArrayPool<XsrFunctionValue>.Shared.Rent(16); int top = 0;
        try
        {
            foreach (var instruction in Program.Typed!)
            {
                token.ThrowIfCancellationRequested();
                if (!CanRun) return false;
                if (--budget < 0) return Fault();
                switch (instruction.Opcode)
                {
                    case 1: stack[top++] = arguments[instruction.Argument]; break;
                    case 2: stack[top++] = result; break;
                    case 3: stack[top++] = instruction.Constant; break;
                    case 4: stack[top - 2] = XsrFunctionValue.Add(stack[top - 2], stack[top - 1]); stack[--top] = default; break;
                    case 5: arguments[instruction.Argument] = stack[--top]; stack[top] = default; break;
                    case 6: result = stack[--top]; stack[top] = default; break;
                    case 7: skip = true; break;
                    case 8: token.ThrowIfCancellationRequested(); return CanRun;
                    case 9: stack[top - 2] = XsrFunctionValue.From(stack[top - 2] == stack[top - 1]); stack[--top] = default; break;
                    case 10: stack[top - 1] = XsrFunctionValue.From(!stack[top - 1].AsBoolean()); break;
                }
            }
            return Fault();
        }
        catch (Exception error) when (error is OverflowException or ArgumentException or InvalidOperationException) { return Fault(); }
        finally { ArrayPool<XsrFunctionValue>.Shared.Return(stack, true); }
    }
}
