#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// Numbers for variable names, given out when bytecode is decoded, so a frame finds a name by
/// comparing numbers rather than hashing text. Names are case-sensitive, as the instance's
/// variables are: <c>mbx</c> and <c>mbX</c> are two variables. Only the built-in instance
/// variables are the same whatever their case, and the compiler turns those into registers.
/// </summary>
internal static class PgslSymbols
{
    private static readonly ConcurrentDictionary<string, int> Ids = new(StringComparer.Ordinal);
    private static int _next;

    public static int Of(string name) => Ids.GetOrAdd(name, static _ => Interlocked.Increment(ref _next));

    /// <summary>The name's number, or -1 when no decoded bytecode uses it (no frame can hold it by slot).</summary>
    public static int Find(string name) => name != null && Ids.TryGetValue(name, out int id) ? id : -1;
}

/// <summary>
/// Where each of a function's variables lives in its frame: its parameters and every name its
/// bytecode reads or writes. Fixed once the function is decoded.
/// </summary>
internal sealed class FrameLayout
{
    private readonly Dictionary<int, int> _slotBySymbol = new();
    private readonly List<string> _names = new();
    // A 256-bit filter of the symbols the layout holds: a clear bit means "not here" for certain.
    private ulong _filter0, _filter1, _filter2, _filter3;

    public int Count => _names.Count;
    public int[] ParameterSlots { get; private set; } = Array.Empty<int>();

    public string NameOf(int slot) => _names[slot];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetSlot(int symbol, out int slot) => _slotBySymbol.TryGetValue(symbol, out slot);

    /// <summary>False when the layout certainly has no slot for the symbol; true when it may (then <see cref="TryGetSlot"/> says).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MightHold(int symbol)
    {
        ulong bit = 1UL << (symbol & 63);
        ulong word = ((symbol >> 6) & 3) switch { 0 => _filter0, 1 => _filter1, 2 => _filter2, _ => _filter3 };
        return (word & bit) != 0;
    }

    internal int Add(string name)
    {
        int symbol = PgslSymbols.Of(name);
        if (_slotBySymbol.TryGetValue(symbol, out int slot)) return slot;
        slot = _names.Count;
        _slotBySymbol[symbol] = slot;
        _names.Add(name);
        ulong bit = 1UL << (symbol & 63);
        switch ((symbol >> 6) & 3)
        {
            case 0: _filter0 |= bit; break;
            case 1: _filter1 |= bit; break;
            case 2: _filter2 |= bit; break;
            default: _filter3 |= bit; break;
        }
        return slot;
    }

    internal void SetParameters(List<string> parameters)
    {
        var slots = new int[parameters.Count];
        for (int i = 0; i < slots.Length; i++) slots[i] = Add(parameters[i]);
        ParameterSlots = slots;
    }
}

/// <summary>
/// One function call's variables: a value per slot of its layout, whether that slot has been
/// assigned (an unassigned name is looked for in the callers' frames, then the instance), and any
/// other name stored into it (code of a called script runs in its caller's frame).
/// </summary>
internal sealed class VariableFrame
{
    public FrameLayout Layout;
    public VmValue[] Values = Array.Empty<VmValue>();
    public bool[] Present = Array.Empty<bool>();
    /// <summary>The function this frame is a call of (errors and the debugger name it).</summary>
    public string Function;
    private Dictionary<string, VmValue> _extra;

    public void Bind(FrameLayout layout)
    {
        Layout = layout;
        int count = layout?.Count ?? 0;
        if (Values.Length < count)
        {
            Values = new VmValue[count];
            Present = new bool[count];
        }
    }

    public void Reset()
    {
        int count = Layout?.Count ?? 0;
        Array.Clear(Values, 0, count);
        Array.Clear(Present, 0, count);
        _extra?.Clear();
        Layout = null;
        Function = null;
    }

    public bool TryGet(int symbol, string name, out VmValue value)
    {
        // Most names looked for in a caller's frame (an instance variable read deep in a call) are
        // not that function's: the layout's filter says so without a lookup.
        if (Layout != null && symbol >= 0 && Layout.MightHold(symbol) && Layout.TryGetSlot(symbol, out int slot))
        {
            value = Values[slot];
            return Present[slot];
        }
        if (_extra != null && _extra.TryGetValue(name, out value)) return true;
        value = default;
        return false;
    }

    public void Set(int symbol, string name, VmValue value)
    {
        if (Layout != null && symbol >= 0 && Layout.TryGetSlot(symbol, out int slot))
        {
            Values[slot] = value;
            Present[slot] = true;
            return;
        }
        (_extra ??= new Dictionary<string, VmValue>(StringComparer.Ordinal))[name] = value;
    }

    /// <summary>Every assigned variable, for the debugger.</summary>
    public IEnumerable<KeyValuePair<string, VmValue>> Entries()
    {
        int count = Layout?.Count ?? 0;
        for (int i = 0; i < count; i++)
            if (Present[i]) yield return new KeyValuePair<string, VmValue>(Layout.NameOf(i), Values[i]);
        if (_extra != null)
            foreach (KeyValuePair<string, VmValue> pair in _extra) yield return pair;
    }
}

internal enum VarKind : byte { Plain, FunctionLocal, Instance }

/// <summary>A LOAD_VAR / STORE_VAR operand, worked out once: which kind of name, its symbol and slot.</summary>
internal sealed class VarRef
{
    public VarKind Kind;
    /// <summary>The name without its compiler prefix.</summary>
    public string Name;
    public int Symbol;
    /// <summary>Slot in the decoded function's own frame, or -1 outside a function.</summary>
    public int Slot = -1;
    /// <summary>Built-in instance register a plain store writes, or -1.</summary>
    public int RegisterSlot = -1;
    /// <summary>The command table (bridge and generation) in which this name was found not to be a property.</summary>
    public object NotPropertyIn;
}

/// <summary>A command table and generation, for <see cref="VarRef.NotPropertyIn"/>.</summary>
internal sealed record PropertyTable(PgslEngineBridge Bridge, int Generation);

/// <summary>One decoded instruction: operands unboxed, constants converted, names resolved.</summary>
internal struct VmOp
{
    /// <summary>The opcode, or <see cref="PgslProgram.Slow"/> for an instruction run as written.</summary>
    public Opcode Code;
    public int A;
    public int B;
    public VmValue Constant;
    public object Ref;
}

/// <summary>
/// Bytecode decoded for the interpreter's fast loop. The instruction list stays the source of
/// truth (line numbers, the debugger, errors); this is derived from it once and reused.
/// </summary>
internal sealed class PgslProgram
{
    /// <summary>Marks an instruction the loop runs through the original, general path.</summary>
    public const Opcode Slow = (Opcode)(-1);

    private static readonly ConditionalWeakTable<List<Instruction>, PgslProgram> TopLevel = new();

    public List<Instruction> Source { get; private init; }
    public List<object> Constants { get; private init; }
    public int ConstantCount { get; private init; }
    public VmOp[] Ops { get; private init; }
    /// <summary>The function's frame layout; null for an event body or a script's top level.</summary>
    public FrameLayout Layout { get; private init; }
    public List<string> Parameters { get; private init; }

    public bool Matches(List<Instruction> instructions, List<object> constants) =>
        ReferenceEquals(Source, instructions) && Ops.Length == instructions.Count
        && ReferenceEquals(Constants, constants) && ConstantCount == (constants?.Count ?? 0);

    /// <summary>The decoded form of a function's body.</summary>
    public static PgslProgram ForFunction(UserFunction function)
    {
        PgslProgram program = function.Program;
        if (program != null && program.Matches(function.Bytecode, function.Constants)
            && ReferenceEquals(program.Parameters, function.Parameters) && program.Layout.ParameterSlots.Length == function.Parameters.Count)
            return program;
        program = Decode(function.Bytecode, function.Constants, function.Parameters);
        function.Program = program;
        return program;
    }

    /// <summary>The decoded form of an event body or a script's top level.</summary>
    public static PgslProgram ForTopLevel(List<Instruction> instructions, List<object> constants)
    {
        if (TopLevel.TryGetValue(instructions, out PgslProgram program) && program.Matches(instructions, constants))
            return program;
        program = Decode(instructions, constants, null);
        TopLevel.AddOrUpdate(instructions, program);
        return program;
    }

    private static PgslProgram Decode(List<Instruction> instructions, List<object> constants, List<string> parameters)
    {
        FrameLayout layout = null;
        if (parameters != null)
        {
            layout = new FrameLayout();
            layout.SetParameters(parameters);
        }
        var ops = new VmOp[instructions.Count];
        for (int i = 0; i < ops.Length; i++)
        {
            Instruction instruction = instructions[i];
            ref VmOp op = ref ops[i];
            op.Code = Slow;
            object operand = instruction.Operand;
            switch (instruction.Opcode)
            {
                case Opcode.PUSH_NULL: case Opcode.POP: case Opcode.DUP: case Opcode.SWAP:
                case Opcode.ADD: case Opcode.SUB: case Opcode.MUL: case Opcode.DIV: case Opcode.MOD:
                case Opcode.NEG: case Opcode.AND: case Opcode.OR: case Opcode.NOT:
                case Opcode.EQ: case Opcode.NEQ: case Opcode.LT: case Opcode.LTE: case Opcode.GT: case Opcode.GTE:
                case Opcode.BIT_AND: case Opcode.BIT_OR: case Opcode.BIT_XOR: case Opcode.BIT_NOT:
                case Opcode.SHL: case Opcode.SHR: case Opcode.RETURN:
                    op.Code = instruction.Opcode;
                    break;
                case Opcode.PUSH:
                    op.Code = Opcode.PUSH;
                    op.Constant = VmValue.FromObject(operand);
                    break;
                case Opcode.LOAD_CONST:
                    if (operand is int index && index >= 0 && constants != null && index < constants.Count)
                    {
                        op.Code = Opcode.LOAD_CONST;
                        op.Constant = VmValue.FromObject(constants[index]);
                    }
                    break;
                case Opcode.LOAD_VAR:
                case Opcode.STORE_VAR:
                    if (operand is string name)
                    {
                        op.Code = instruction.Opcode;
                        op.Ref = DecodeName(name, layout);
                    }
                    break;
                case Opcode.JUMP: case Opcode.JUMP_IF_FALSE: case Opcode.JUMP_IF_TRUE:
                case Opcode.LOAD_REG: case Opcode.STORE_REG:
                    if (operand is int target)
                    {
                        op.Code = instruction.Opcode;
                        op.A = target;
                    }
                    break;
                case Opcode.CALL_NATIVE:
                    if (operand is (int id, int nativeArguments))
                    {
                        op.Code = Opcode.CALL_NATIVE;
                        op.A = id;
                        op.B = nativeArguments;
                    }
                    break;
                case Opcode.CALL:
                    if (operand is (string function, int arguments))
                    {
                        op.Code = Opcode.CALL;
                        op.Ref = function;
                        op.B = arguments;
                    }
                    break;
            }
        }
        return new PgslProgram
        {
            Source = instructions,
            Constants = constants,
            ConstantCount = constants?.Count ?? 0,
            Ops = ops,
            Layout = layout,
            Parameters = parameters,
        };
    }

    private static VarRef DecodeName(string operand, FrameLayout layout)
    {
        var reference = new VarRef();
        if (operand.StartsWith(PgslParser.InstanceVariablePrefix, StringComparison.Ordinal))
        {
            reference.Kind = VarKind.Instance;
            reference.Name = operand.Substring(PgslParser.InstanceVariablePrefix.Length);
            reference.Symbol = -1;
            return reference;
        }
        if (operand.StartsWith(PgslParser.FunctionLocalPrefix, StringComparison.Ordinal))
        {
            reference.Kind = VarKind.FunctionLocal;
            reference.Name = operand.Substring(PgslParser.FunctionLocalPrefix.Length);
        }
        else
        {
            reference.Kind = VarKind.Plain;
            reference.Name = operand;
            if (PgslRegisterFile.Slots.TryGetValue(operand, out int register)) reference.RegisterSlot = register;
        }
        reference.Symbol = PgslSymbols.Of(reference.Name);
        if (layout != null) reference.Slot = layout.Add(reference.Name);
        return reference;
    }
}
