using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Nexa.Sidecar.Protocol;

namespace Nexa.Xsr.Runtime;

public enum XsrFunctionPatchPhase : byte { Head = 1, Args = 2, Tail = 3, Return = 4, Replace = 5 }

/// <summary>An instance-owned numeric host point with an explicit bounded primitive shape.</summary>
public sealed class XsrFunctionPatchPoint
{
    internal XsrFunctionPatchPoint(XsrFunctionPatchRuntime owner, int index, long callId, XsrSemanticId semanticId)
    { Owner = owner; Index = index; CallId = callId; SemanticId = semanticId; }
    internal XsrFunctionPatchRuntime Owner { get; }
    internal int Index { get; }
    internal long CallId { get; }
    public XsrSemanticId SemanticId { get; }
    public XsrFunctionShape Shape { get; internal set; } = XsrFunctionShape.String;
}

/// <summary>Copied host capability grant. A signed Sidecar does not create its own grant.</summary>
public sealed class XsrFunctionPatchAdmission
{
    private readonly HashSet<XsrSemanticId> _allowed = [];
    public XsrFunctionPatchAdmission(XsrFunctionPatchRuntime runtime, params XsrSemanticId[] targets)
    {
        Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(targets);
        foreach (var target in targets) { _ = runtime.Resolve(target); _allowed.Add(target); }
    }
    internal XsrFunctionPatchRuntime Runtime { get; }
    internal XsrPreparedFunctionPatch[] Prepare(IReadOnlyList<SidecarExtensionRegistration> declarations)
    {
        List<XsrPreparedFunctionPatch> result = [];
        foreach (var entry in declarations)
        {
            if (entry.Kind != SidecarRegistrationKind.FunctionPatch) continue;
            if (!_allowed.Contains(entry.Target) || entry.Flags != 0)
                throw new SidecarProtocolException("Function patch target or flags are not granted by the host.");
            var point = Runtime.Resolve(entry.Target);
            result.Add(new(point, XsrFunctionPatchProgram.Decode(entry.Payload.Span, point.Shape)));
            if (result.Count > 256) throw new SidecarProtocolException("Session function patch program budget exceeded.");
        }
        if (result.GroupBy(p => p.Point.Index).Any(group => group.Count() > 32))
            throw new SidecarProtocolException("Session function patch point budget exceeded.");
        return result.ToArray();
    }
}

/// <summary>Local bounded interpreter. Registration and retirement never add hot-path IPC.</summary>
public sealed partial class XsrFunctionPatchRuntime
{
    public const int MaximumStringCharacters = 2048;
    private readonly object _gate = new();
    private readonly Dictionary<XsrSemanticId, XsrFunctionPatchPoint> _points = [];
    private XsrActiveFunctionPatch[][] _table;
    private int _programCount;
    private static long _nextCallId;
    [ThreadStatic] private static long[]? _calls;
    [ThreadStatic] private static int _depth;

    public XsrFunctionPatchRuntime(params XsrSemanticId[] targets) : this((targets ?? throw new ArgumentNullException(nameof(targets))).Select(id => new XsrFunctionTarget(id, XsrFunctionShape.String)).ToArray()) { }

    public XsrFunctionPatchRuntime(XsrFunctionTarget first, params XsrFunctionTarget[] remaining) : this(new[] { first }.Concat(remaining).ToArray()) { }

    private XsrFunctionPatchRuntime(XsrFunctionTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targets.Length, 256);
        _table = new XsrActiveFunctionPatch[targets.Length][];
        for (int i = 0; i < targets.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(targets[i]);
            ArgumentNullException.ThrowIfNull(targets[i].Shape);
            _ = XsrSemanticId.Parse(targets[i].Target.Value);
            _points.Add(targets[i].Target, new(this, i, Interlocked.Increment(ref _nextCallId), targets[i].Target) { Shape = targets[i].Shape });
            _table[i] = [];
        }
    }

    public XsrFunctionPatchPoint Resolve(XsrSemanticId target) => _points.TryGetValue(target, out var point)
        ? point : throw new ArgumentException("Unknown host function patch point.", nameof(target));

    public string Invoke(XsrFunctionPatchPoint point, string argument, Func<string, string> original,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(original);
        if (point.Owner != this) throw new ArgumentException("Point belongs to another runtime.", nameof(point));
        if (!point.Shape.IsString) throw new ArgumentException("Point has a different function shape.", nameof(point));
        cancellationToken.ThrowIfCancellationRequested();
        var programs = Volatile.Read(ref _table)[point.Index];
        if (programs.Length == 0 || argument.Length > MaximumStringCharacters || _depth == 32)
            return original(argument);
        _calls ??= new long[32];
        for (int i = 0; i < _depth; i++) if (_calls[i] == point.CallId) return original(argument);
        _calls[_depth++] = point.CallId;
        try
        {
            string result = string.Empty;
            bool skip = false;
            int budget = 512;
            RunPhase(XsrFunctionPatchPhase.Head);
            RunPhase(XsrFunctionPatchPhase.Args);
            if (!skip) RunPhase(XsrFunctionPatchPhase.Replace);
            if (!skip) result = original(argument);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) return result!; // Preserve an out-of-contract host result without patching it.
            RunPhase(XsrFunctionPatchPhase.Tail);
            RunPhase(XsrFunctionPatchPhase.Return);
            return result;

            bool RunTyped(XsrActiveFunctionPatch active, ref string argumentValue, ref string resultValue, ref bool skipValue)
            {
                if (argumentValue.Length > MaximumStringCharacters || resultValue.Length > MaximumStringCharacters) return false;
                var values = ArrayPool<XsrFunctionValue>.Shared.Rent(8);
                try
                {
                    values[0] = XsrFunctionValue.From(argumentValue);
                    var typedResult = XsrFunctionValue.From(resultValue);
                    if (!active.RunTyped(values, ref typedResult, ref skipValue, ref budget, cancellationToken)) return false;
                    argumentValue = values[0].AsString(); resultValue = typedResult.AsString(); return true;
                }
                finally { ArrayPool<XsrFunctionValue>.Shared.Return(values, true); }
            }

            void RunPhase(XsrFunctionPatchPhase phase)
            {
                foreach (var active in programs)
                {
                    if (active.Program.Phase != phase || !active.CanRun) continue;
                    if (phase == XsrFunctionPatchPhase.Replace && skip) break;
                    string nextArgument = argument, nextResult = result;
                    bool nextSkip = skip;
                    if (active.Program.Typed is not null
                        ? RunTyped(active, ref nextArgument, ref nextResult, ref nextSkip)
                        : active.Run(ref nextArgument, ref nextResult, ref nextSkip, ref budget, cancellationToken))
                    { argument = nextArgument; result = nextResult; skip = nextSkip; }
                }
            }
        }
        finally { _calls[--_depth] = 0; }
    }

    internal IDisposable Activate(XsrPreparedFunctionPatch[] prepared)
    {
        lock (_gate)
        {
            if (_programCount + prepared.Length > 256)
                throw new SidecarProtocolException("Host function patch program budget exceeded.");
            var table = (XsrActiveFunctionPatch[][])_table.Clone();
            XsrFunctionPatchLease lease = new(this);
            foreach (var group in prepared.GroupBy(p => p.Point.Index))
            {
                if (table[group.Key].Length + group.Count() > 32)
                    throw new SidecarProtocolException("Host function patch point budget exceeded.");
                table[group.Key] = [.. table[group.Key], .. group.Select(p => new XsrActiveFunctionPatch(lease, p.Program))];
            }
            _programCount += prepared.Length;
            Volatile.Write(ref _table, table);
            return lease;
        }
    }

    internal void Retire(XsrFunctionPatchLease lease)
    {
        lock (_gate)
        {
            var table = (XsrActiveFunctionPatch[][])_table.Clone();
            for (int i = 0; i < table.Length; i++)
            {
                var remaining = table[i].Where(p => p.Lease != lease).ToArray();
                _programCount -= table[i].Length - remaining.Length;
                table[i] = remaining;
            }
            Volatile.Write(ref _table, table);
        }
    }
}

internal sealed class XsrFunctionPatchLease(XsrFunctionPatchRuntime owner) : IDisposable
{
    private int _retired;
    public bool Retired => Volatile.Read(ref _retired) != 0;
    public void Dispose() { if (Interlocked.Exchange(ref _retired, 1) == 0) owner.Retire(this); }
}

internal sealed record XsrPreparedFunctionPatch(XsrFunctionPatchPoint Point, XsrFunctionPatchProgram Program);
internal readonly record struct XsrPatchInstruction(byte Opcode, string? Constant = null);

internal sealed partial class XsrFunctionPatchProgram(XsrFunctionPatchPhase phase, XsrPatchInstruction[] instructions)
{
    public XsrFunctionPatchPhase Phase { get; } = phase;
    public XsrPatchInstruction[] Instructions { get; } = instructions;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static XsrFunctionPatchProgram Decode(ReadOnlySpan<byte> bytes, XsrFunctionShape? shape = null)
    {
        shape ??= XsrFunctionShape.String;
        if (bytes.Length >= 4 && bytes[..4].SequenceEqual("NFP2"u8)) return DecodeTyped(bytes, shape);
        if (!shape.IsString) throw Invalid();
        if (bytes.Length is < 9 or > 8192 || !bytes[..4].SequenceEqual("NFP1"u8) || bytes[4] != 1
            || !Enum.IsDefined((XsrFunctionPatchPhase)bytes[5])) throw Invalid();
        var phase = (XsrFunctionPatchPhase)bytes[5];
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (count is < 1 or > 64) throw Invalid();
        XsrPatchInstruction[] instructions = new XsrPatchInstruction[count];
        int cursor = 8, stack = 0;
        bool resultSet = false, skip = false;
        for (int i = 0; i < count; i++)
        {
            if (cursor >= bytes.Length) throw Invalid();
            byte op = bytes[cursor++];
            string? constant = null;
            switch (op)
            {
                case 1: stack++; break;
                case 2:
                    if (phase is not (XsrFunctionPatchPhase.Tail or XsrFunctionPatchPhase.Return)) throw Invalid();
                    stack++; break;
                case 3:
                    if (cursor + 2 > bytes.Length) throw Invalid();
                    int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]); cursor += 2;
                    if (length > 1024 || cursor + length > bytes.Length) throw Invalid();
                    try { constant = StrictUtf8.GetString(bytes.Slice(cursor, length)); }
                    catch (DecoderFallbackException) { throw Invalid(); }
                    cursor += length; stack++; break;
                case 4:
                    if (stack < 2) throw Invalid(); stack--; break;
                case 5:
                    if (stack < 1 || phase != XsrFunctionPatchPhase.Args) throw Invalid(); stack--; break;
                case 6:
                    if (stack < 1 || phase == XsrFunctionPatchPhase.Args) throw Invalid();
                    stack--; resultSet = true; break;
                case 7:
                    if (phase is not (XsrFunctionPatchPhase.Head or XsrFunctionPatchPhase.Replace) || !resultSet) throw Invalid();
                    skip = true; break;
                case 8:
                    if (i != count - 1 || stack != 0 || (phase == XsrFunctionPatchPhase.Replace && !skip)) throw Invalid();
                    break;
                default: throw Invalid();
            }
            if (stack > 16 || (i == count - 1 && op != 8)) throw Invalid();
            instructions[i] = new(op, constant);
        }
        if (cursor != bytes.Length) throw Invalid();
        return new(phase, instructions);
    }
    private static SidecarProtocolException Invalid() => new("Invalid bounded function patch program.");
}

internal sealed partial class XsrActiveFunctionPatch(XsrFunctionPatchLease lease, XsrFunctionPatchProgram program)
{
    private int _faulted;
    public XsrFunctionPatchLease Lease { get; } = lease;
    public XsrFunctionPatchProgram Program { get; } = program;
    public bool CanRun => !Lease.Retired && Volatile.Read(ref _faulted) == 0;
    public bool Run(ref string argument, ref string result, ref bool skip, ref int budget, CancellationToken token)
    {
        string?[] stack = ArrayPool<string?>.Shared.Rent(16);
        int top = 0;
        try
        {
            foreach (var instruction in Program.Instructions)
            {
                token.ThrowIfCancellationRequested();
                if (!CanRun) return false;
                if (--budget < 0) return Fault();
                switch (instruction.Opcode)
                {
                    case 1: if (!Fits(argument)) return Fault(); stack[top++] = argument; break;
                    case 2: if (!Fits(result)) return Fault(); stack[top++] = result; break;
                    case 3: stack[top++] = instruction.Constant; break;
                    case 4:
                        string left = stack[top - 2]!, right = stack[top - 1]!;
                        if (left.Length + right.Length > XsrFunctionPatchRuntime.MaximumStringCharacters) return Fault();
                        stack[--top] = null; stack[top - 1] = string.Concat(left, right); break;
                    case 5: argument = stack[--top]!; stack[top] = null; break;
                    case 6: result = stack[--top]!; stack[top] = null; break;
                    case 7: skip = true; break;
                    case 8: token.ThrowIfCancellationRequested(); return CanRun;
                }
            }
            return Fault();
        }
        finally { ArrayPool<string?>.Shared.Return(stack, clearArray: true); }
    }
    private bool Fault() { Interlocked.Exchange(ref _faulted, 1); return false; }
    private static bool Fits(string text) => text.Length <= XsrFunctionPatchRuntime.MaximumStringCharacters;
}
