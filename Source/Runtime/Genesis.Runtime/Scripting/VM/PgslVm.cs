#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Genesis.Runtime.Scripting.PG;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting.VM;

public class PgslVm
{
    private class WithState
    {
        public IEnumerator<object> Enumerator { get; set; }
        public PgslContext PreviousContext { get; set; }
        public IPgslInstance Current { get; set; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> InstanceVariableNames = new();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> FunctionLocalNames = new();

    private static string FunctionLocalName(string operand) =>
        FunctionLocalNames.GetOrAdd(operand, name => name.Substring(PgslParser.FunctionLocalPrefix.Length));

    private static string InstanceVariableName(string operand) =>
        InstanceVariableNames.GetOrAdd(operand, name => name.Substring(PgslParser.InstanceVariablePrefix.Length));

    // Inside a with-block, the VM of the instance the block runs as holds its variables.
    private PgslVm InstanceVm => (_bridge.GetContext()?.ActiveVm as PgslVm) ?? this;

    private VmValue LoadInstanceVariable(string operand) => LoadInstanceVariableByName(InstanceVariableName(operand));

    private VmValue LoadInstanceVariableByName(string name) =>
        InstanceVm._variables.TryGetValue(name, out VmValue value) ? value : ResolvePGSLPropertyOrZero(name);

    private void StoreInstanceVariable(string operand, VmValue value) =>
        StoreInstanceVariableByName(InstanceVariableName(operand), value);

    private void StoreInstanceVariableByName(string name, VmValue value)
    {
        bool property = _bridge is PgslEngineBridge typedBridge
            ? typedBridge.TrySetTypedProperty(name, value)
            : _bridge.TrySetProperty(name, value.ToObject());
        if (!property) InstanceVm._variables[name] = value;
    }

    /// <summary>Leaves with-blocks a return (or an error) jumped out of, restoring the caller.</summary>
    private void UnwindWith(int depth)
    {
        while (_withStack.Count > depth)
        {
            WithState state = _withStack.Pop();
            try { state.Current?.EndActiveContext(); }
            finally
            {
                _bridge.SetContext(state.PreviousContext);
                state.Enumerator.Dispose();
            }
        }
    }

    private readonly VmValueStack _stack = new();
    private readonly Dictionary<string, VmValue> _variables = new();
    // The frames of the user-function calls in progress, innermost last. A function's own names
    // live in slots of its frame; a name it does not hold is looked for in its callers' frames,
    // then the instance's variables.
    private VariableFrame[] _frames = new VariableFrame[16];
    private int _frameCount;
    private readonly Stack<VariableFrame> _framePool = new();
    private readonly Dictionary<int, Stack<object[]>> _argumentPools = new();
    private IReadOnlyList<object> _constants = Array.Empty<object>();
    private readonly Dictionary<string, UserFunction> _userFunctions = new();
    private readonly Stack<WithState> _withStack = new();
    private readonly Stack<string> _debugCallStack = new();
    private readonly IPgslEngineBridge _bridge;
    private readonly PgslEngineBridge _typedBridge;
    private bool _debugMode = false;
    private bool _returnRequested;
    private VmValue _returnValue;
    private const int MAX_INSTRUCTIONS = 100000; // Hard limit per call to prevent hangs
    /// <summary>Most user-function calls in progress at once (recursion depth).</summary>
    public const int MaxCallDepth = 200;
    private static readonly string[] LocalSlotNames = CreateSlotNames("@local", 256);
    private static readonly string[] ArgumentNames = CreateSlotNames("argument", 64);

    private readonly record struct ExecutionResult(bool Returned, VmValue Value);

    public IPgslEngineBridge Bridge => _bridge;
    /// <summary>Instructions this VM has run in total (every event body and function call), for speed reports.</summary>
    public long InstructionsExecuted { get; private set; }

    /// <summary>
    /// Set for a VM running a worker job: one instruction budget for the whole job (every call
    /// counted) instead of the per-call limit, and cancellation. Null for a game's VMs.
    /// </summary>
    internal PgslJobBudget JobBudget { get; set; }

    /// <summary>
    /// The library functions a worker job may call, fixed when it started; a game's VMs (null here)
    /// look in the project's Scripts as they are now.
    /// </summary>
    internal IReadOnlyDictionary<string, UserFunction> LibraryFunctions { get; set; }
    public PgslDebugController Debugger { get; set; }
    public string DebugSourceName { get; set; } = string.Empty;
    public string DebugEventName { get; set; } = string.Empty;

    public PgslVm(IPgslEngineBridge bridge, bool debugMode = false)
    {
        _bridge = bridge;
        _typedBridge = bridge as PgslEngineBridge;
        _debugMode = debugMode;
    }

    public void SetScriptArguments(object[] args)
    {
      if (args == null) return;
      for (int i = 0; i < args.Length; i++)
        _variables[SlotName(ArgumentNames, "argument", i)] = VmValue.FromObject(args[i]);
      _variables["argument_count"] = args.Length;
    }

    /// <summary>Sets a persistent script variable by name for PGSL visual-action commands.</summary>
    public void SetVariable(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _variables[name.Trim()] = VmValue.FromObject(value);
    }

    /// <summary>Reads a persistent script variable without exposing the VM's mutable dictionary.</summary>
    public bool TryReadVariable(string name, out object value)
    {
        bool found = TryGetVariable(name?.Trim() ?? string.Empty, out VmValue typed);
        value = typed.ToObject(); return found;
    }

    public void LoadUserFunctions(Dictionary<string, UserFunction> userFunctions)
    {
        foreach (var func in userFunctions)
        {
            _userFunctions[func.Key] = func.Value;
        }
    }

    /// <summary>
    /// Runs a called script's set-up in a scope of its own: its functions and its argument0..N
    /// apply until the scope ends, then the caller's come back. Two scripts may each define a
    /// function called Helper, and a script that calls another still reads its own arguments.
    /// </summary>
    public IDisposable EnterScriptScope(object[] args, Dictionary<string, UserFunction> functions)
    {
        var scope = new ScriptScope(this);
        int slots = Math.Max(args?.Length ?? 0, ArgumentCount());
        for (int i = 0; i < slots; i++) scope.Remember(SlotName(ArgumentNames, "argument", i));
        scope.Remember("argument_count");
        if (functions != null)
            foreach (string name in functions.Keys) scope.RememberFunction(name);
        if (args != null) SetScriptArguments(args);
        if (functions != null) LoadUserFunctions(functions);
        return scope;
    }

    private int ArgumentCount() =>
        _variables.TryGetValue("argument_count", out VmValue count) && count.ToObject() is { } value
            ? Math.Max(0, (int)Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture))
            : 0;

    private sealed class ScriptScope : IDisposable
    {
        private readonly PgslVm _vm;
        private readonly List<(string Name, bool Had, VmValue Value)> _variables = [];
        private readonly List<(string Name, UserFunction Function)> _functions = [];
        private bool _ended;

        public ScriptScope(PgslVm vm) => _vm = vm;

        public void Remember(string name)
        {
            bool had = _vm._variables.TryGetValue(name, out VmValue value);
            _variables.Add((name, had, value));
        }

        public void RememberFunction(string name)
        {
            _vm._userFunctions.TryGetValue(name, out UserFunction function);
            _functions.Add((name, function));
        }

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            foreach ((string name, bool had, VmValue value) in _variables)
            {
                if (had) _vm._variables[name] = value;
                else _vm._variables.Remove(name);
            }
            // A script's own functions stay callable by its caller (a library script is run once,
            // then its functions are called), but a name the caller already had is put back.
            foreach ((string name, UserFunction function) in _functions)
                if (function != null) _vm._userFunctions[name] = function;
        }
    }

    public void Execute(List<Instruction> instructions, List<object> constants, bool clearVariables = true)
    {
        try
        {
            ExecutionResult result = ExecuteCore(PgslProgram.ForTopLevel(instructions, constants), instructions, constants, clearVariables);
            if (result.Returned)
                throw new ReturnException(result.Value.ToObject());
        }
        finally
        {
            Debugger?.OnCompleted();
        }
    }

    private ExecutionResult ExecuteCore(
        PgslProgram program,
        List<Instruction> instructions,
        List<object> constants,
        bool clearVariables)
    {
        bool previousReturnRequested = _returnRequested;
        VmValue previousReturnValue = _returnValue;

        IReadOnlyList<object> previousConstants = _constants;
        _returnRequested = false;
        _returnValue = default;
        _constants = constants;
        if (clearVariables)
        {
            _variables.Clear();
            _stack.Clear();
            while (_frameCount > 0)
                ReleaseFrame(PopFrame());
        }

        int pc = 0;
        int withDepth = _withStack.Count;
        // The budget is per event body and per function call: each call counts its own. A worker
        // job instead checks its whole-job budget (and whether it was cancelled) at every slice.
        int executed = 0;
        int checkpoint = JobBudget is null ? MAX_INSTRUCTIONS : PgslJobBudget.Slice;
        VmOp[] ops = program.Ops;
        // A function's body runs in its own frame (the top one): its names are read by slot.
        VariableFrame frame = program.Layout != null && _frameCount > 0 && _frames[_frameCount - 1].Layout == program.Layout
            ? _frames[_frameCount - 1]
            : null;

        // The operand stack lives in locals while the loop runs: `stack` and `sp` are the truth
        // while `local` is set; around anything that may use the VM's stack (a call, a command, a
        // general-path instruction, the debugger) the count is set back and read again after.
        VmValueStack store = _stack;
        VmValue[] stack = store.Items;
        int sp = store.Count;
        bool local = true;

        try
        {
            while (pc < ops.Length)
            {
                if (++executed > checkpoint)
                {
                    if (JobBudget is null)
                        ThrowInstructionLimit(_debugCallStack.Count > 0 ? _debugCallStack.Peek() : null);
                    JobBudget.Check(InstructionsExecuted + executed);
                    checkpoint += PgslJobBudget.Slice;
                }

                if (Debugger is not null)
                {
                    store.Count = sp; local = false;
                    Debugger.BeforeInstruction(this, CreateDebugLocation(instructions[pc], pc));
                    stack = store.Items; sp = store.Count; local = true;
                }

                if (_debugMode)
                    Console.WriteLine($"[PC:{pc}] {instructions[pc]}");

                ref VmOp op = ref ops[pc];
                switch (op.Code)
                {
                    case Opcode.LOAD_VAR:
                    {
                        VarRef variable = Unsafe.As<VarRef>(op.Ref);
                        // The common case: a name this call has set, read from its slot.
                        if (frame != null && variable.Slot >= 0 && frame.Present[variable.Slot])
                        {
                            if (sp == stack.Length) { store.Count = sp; stack = store.Grow(); }
                            stack[sp++] = frame.Values[variable.Slot];
                        }
                        else
                        {
                            store.Count = sp; local = false;
                            LoadVariable(variable, frame);
                            stack = store.Items; sp = store.Count; local = true;
                        }
                        break;
                    }

                    case Opcode.STORE_VAR:
                    {
                        if (sp == 0)
                            throw new InvalidOperationException("Cannot store variable");
                        VarRef variable = Unsafe.As<VarRef>(op.Ref);
                        VmValue value = stack[--sp];
                        stack[sp] = default;
                        if (frame != null && variable.Kind == VarKind.FunctionLocal)
                        {
                            frame.Values[variable.Slot] = value;
                            frame.Present[variable.Slot] = true;
                        }
                        else
                        {
                            store.Count = sp; local = false;
                            StoreVariable(variable, frame, value);
                            stack = store.Items; sp = store.Count; local = true;
                        }
                        break;
                    }

                    case Opcode.LOAD_CONST:
                    case Opcode.PUSH:
                        if (sp == stack.Length) { store.Count = sp; stack = store.Grow(); }
                        stack[sp++] = op.Constant;
                        break;

                    case Opcode.PUSH_NULL:
                        if (sp == stack.Length) { store.Count = sp; stack = store.Grow(); }
                        stack[sp++] = default;
                        break;

                    case Opcode.POP:
                        if (sp > 0)
                            stack[--sp] = default;
                        break;

                    case Opcode.JUMP:
                        pc = op.A - 1;
                        break;

                    case Opcode.JUMP_IF_FALSE:
                        if (sp > 0)
                        {
                            bool truth = stack[--sp].Truth;
                            stack[sp] = default;
                            if (!truth)
                                pc = op.A - 1;
                        }
                        break;

                    case Opcode.JUMP_IF_TRUE:
                        if (sp > 0)
                        {
                            bool truth = stack[--sp].Truth;
                            stack[sp] = default;
                            if (truth)
                                pc = op.A - 1;
                        }
                        break;

                    case Opcode.ADD:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        if (left.IsString || right.IsString)
                            left = left.ToString() + right.ToString();
                        else
                            left = left.Number + right.Number;
                        break;
                    }

                    case Opcode.SUB:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a - b;
                        break;
                    }

                    case Opcode.MUL:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a * b;
                        break;
                    }

                    case Opcode.DIV:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a / b;
                        break;
                    }

                    case Opcode.MOD:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = Math.Abs(b) < 1e-12 ? (VmValue)0 : a - Math.Floor(a / b) * b;
                        break;
                    }

                    case Opcode.LT:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a < b ? 1 : 0;
                        break;
                    }

                    case Opcode.LTE:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a <= b ? 1 : 0;
                        break;
                    }

                    case Opcode.GT:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a > b ? 1 : 0;
                        break;
                    }

                    case Opcode.GTE:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        double b = right.Number, a = left.Number;
                        left = a >= b ? 1 : 0;
                        break;
                    }

                    case Opcode.EQ:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        left = VMEquals(left, right) ? 1 : 0;
                        break;
                    }

                    case Opcode.NEQ:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        left = !VMEquals(left, right) ? 1 : 0;
                        break;
                    }

                    case Opcode.AND:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        left = left.Truth && right.Truth ? 1 : 0;
                        break;
                    }

                    case Opcode.OR:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        left = left.Truth || right.Truth ? 1 : 0;
                        break;
                    }

                    case Opcode.NOT:
                        if (sp > 0)
                            stack[sp - 1] = stack[sp - 1].Truth ? 0 : 1;
                        else
                            throw new InvalidOperationException("Cannot negate empty stack");
                        break;

                    case Opcode.BIT_AND:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        int a = AsInt32(left), b = AsInt32(right);
                        left = a & b;
                        break;
                    }

                    case Opcode.BIT_OR:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        int a = AsInt32(left), b = AsInt32(right);
                        left = a | b;
                        break;
                    }

                    case Opcode.BIT_XOR:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        int a = AsInt32(left), b = AsInt32(right);
                        left = a ^ b;
                        break;
                    }

                    case Opcode.SHL:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        int a = AsInt32(left), b = AsInt32(right);
                        left = a << b;
                        break;
                    }

                    case Opcode.SHR:
                    {
                        if (sp < 2) ThrowBinaryUnderflow();
                        VmValue right = stack[--sp];
                        stack[sp] = default;
                        ref VmValue left = ref stack[sp - 1];
                        int a = AsInt32(left), b = AsInt32(right);
                        left = a >> b;
                        break;
                    }

                    case Opcode.CALL_NATIVE:
                        store.Count = sp; local = false;
                        ExecuteNativeCall(op.A, op.B);
                        stack = store.Items; sp = store.Count; local = true;
                        break;

                    case Opcode.CALL:
                        store.Count = sp; local = false;
                        ExecuteCall(Unsafe.As<string>(op.Ref), op.B);
                        stack = store.Items; sp = store.Count; local = true;
                        break;

                    case Opcode.RETURN:
                        if (sp > 0)
                        {
                            _returnValue = stack[--sp];
                            stack[sp] = default;
                        }
                        else
                            _returnValue = default;
                        _returnRequested = true;
                        goto Finished;

                    case Opcode.LOAD_REG:
                    {
                        int slot = op.A;
                        var ctx = _bridge.GetContext();
                        VmValue value;
                        if (ctx == null || slot < 0 || slot >= PgslRegisterFile.NumberGetters.Length) value = 0.0;
                        else if (slot == PgslRegisterFile.SlotSpriteIndex) value = ctx.SpriteIndex ?? "";
                        else value = PgslRegisterFile.NumberGetters[slot](ctx);
                        if (sp == stack.Length) { store.Count = sp; stack = store.Grow(); }
                        stack[sp++] = value;
                        break;
                    }

                    case Opcode.STORE_REG:
                    {
                        if (sp == 0)
                            throw new InvalidOperationException("Stack underflow on STORE_REG");
                        VmValue value = stack[--sp];
                        stack[sp] = default;
                        var ctx = _bridge.GetContext();
                        if (ctx != null && op.A >= 0 && op.A < PgslRegisterFile.NumberSetters.Length)
                            PgslRegisterFile.Write(ctx, op.A, value);
                        break;
                    }

                    case Opcode.NEG:
                        if (sp > 0)
                        {
                            VmValue operand = stack[--sp];
                            stack[sp] = default;
                            stack[sp++] = -operand.Number;
                        }
                        else
                            throw new InvalidOperationException("Cannot negate empty stack");
                        break;

                    default:
                        // Everything else (with-blocks, indexing, printing, unusual operands) runs as written.
                        store.Count = sp; local = false;
                        ExecuteInstruction(instructions[pc], ref pc);
                        stack = store.Items; sp = store.Count; local = true;
                        if (_returnRequested)
                            goto Finished;
                        break;
                }
                pc++;
            }
            Finished:

            if (_debugMode)
            {
                Console.WriteLine("\nExecution completed successfully.");
                Console.WriteLine("Variables:");
                foreach (var kvp in _variables)
                {
                    Console.WriteLine($"  {kvp.Key} = {kvp.Value}");
                }
            }

            return new ExecutionResult(_returnRequested, _returnValue);
        }
        catch (ReturnException)
        {
            throw;
        }
        catch (PgslDebugStopException)
        {
            throw;
        }
        // An error already given its line by a deeper call passes through untouched: catching and
        // re-throwing it at every level of a deep recursion would overflow the stack while unwinding.
        catch (Exception ex) when (ex.Data["PgslWrapped"] is not true)
        {
            var currentInstr = (pc < instructions.Count) ? instructions[pc] : null;
            int line = currentInstr?.LineNumber ?? 0;
            if (Debugger is not null && ex.Data["PgslDebugReported"] is not true)
            {
                Debugger.OnError(CreateDebugLocation(currentInstr, pc, ex.Message));
                ex.Data["PgslDebugReported"] = true;
            }
            // A callee's error already names its line; do not prefix the caller's line again.
            var vmEx = new Exception(ex.Message.StartsWith("Line ", StringComparison.Ordinal) ? ex.Message : $"Line {line}: {ex.Message}");
            vmEx.Data["Line"] = line;
            vmEx.Data["PgslDebugReported"] = true;
            vmEx.Data["PgslWrapped"] = true;
            throw vmEx;
        }
        finally
        {
            if (local)
                store.Count = sp;
            InstructionsExecuted += executed;
            UnwindWith(withDepth);
            _constants = previousConstants;
            _returnRequested = previousReturnRequested;
            _returnValue = previousReturnValue;
        }
    }

    private void ExecuteInstruction(Instruction instr, ref int pc)
    {
        switch (instr.Opcode)
        {
            case Opcode.PUSH_NULL:
                _stack.Push(default);
                break;

            case Opcode.PUSH:
                _stack.Push(VmValue.FromObject(instr.Operand));
                break;

            case Opcode.POP:
                if (_stack.Count > 0)
                    _stack.Pop();
                break;

            case Opcode.DUP:
                if (_stack.Count > 0)
                    _stack.Push(_stack.Peek());
                else
                    throw new InvalidOperationException("Cannot duplicate empty stack");
                break;

            case Opcode.SWAP:
                if (_stack.Count < 2)
                    throw new InvalidOperationException("Cannot swap with less than 2 values");
                var a = _stack.Pop();
                var b = _stack.Pop();
                _stack.Push(a);
                _stack.Push(b);
                break;

            case Opcode.LOAD_CONST:
                if (instr.Operand is int idx && idx >= 0 && idx < _constants.Count)
                    _stack.Push(VmValue.FromObject(_constants[idx]));
                else
                    throw new InvalidOperationException($"Invalid constant index: {instr.Operand}");
                break;

            case Opcode.LOAD_VAR:
                if (instr.Operand is string varName)
                {
                    if (varName.StartsWith(PgslParser.InstanceVariablePrefix, StringComparison.Ordinal))
                    {
                        _stack.Push(LoadInstanceVariable(varName));
                        break;
                    }
                    if (varName.StartsWith(PgslParser.FunctionLocalPrefix, StringComparison.Ordinal))
                    {
                        _stack.Push(TryGetVariable(FunctionLocalName(varName), out VmValue local) ? local : 0.0);
                        break;
                    }
                    // Prefer local variables first, then fall back to PGSL context properties.
                    // This ensures reads of x, y, sprite_index etc. come from the live context.
                    if (TryGetVariable(varName, out var value))
                        _stack.Push(value);
                    else
                        _stack.Push(ResolvePGSLPropertyOrZero(varName));
                }
                else
                    throw new InvalidOperationException($"Invalid LOAD_VAR operand: {instr.Operand}");
                break;

            case Opcode.STORE_VAR:
                if (instr.Operand is string storeName && _stack.Count > 0)
                {
                    var storeValue = _stack.Pop();
                    if (storeName.StartsWith(PgslParser.InstanceVariablePrefix, StringComparison.Ordinal))
                    {
                        StoreInstanceVariable(storeName, storeValue);
                        break;
                    }
                    if (storeName.StartsWith(PgslParser.FunctionLocalPrefix, StringComparison.Ordinal))
                    {
                        StoreVariable(FunctionLocalName(storeName), storeValue);
                        break;
                    }
                    // Current bytecode emits STORE_REG for built-in instance fields. For the much
                    // smaller declared-property set, use an explicit lookup rather than restoring
                    // the old exception-driven command probe on every ordinary variable assignment.
                    if (PgslRegisterFile.Slots.TryGetValue(storeName, out int storeSlot))
                    {
                        var context = _bridge.GetContext();
                        if (context != null)
                            PgslRegisterFile.Write(context, storeSlot, storeValue);
                    }
                    else if (!(_bridge is PgslEngineBridge typedBridge
                        ? typedBridge.TrySetTypedProperty(storeName, storeValue)
                        : _bridge.TrySetProperty(storeName, storeValue.ToObject())))
                    {
                        StoreVariable(storeName, storeValue);
                    }
                }
                else
                    throw new InvalidOperationException("Cannot store variable");
                break;

            case Opcode.ADD:
                ref VmValue left_add = ref _stack.CombineTop(out VmValue right_add);
                if (left_add.IsString || right_add.IsString)
                    left_add = left_add.ToString() + right_add.ToString();
                else
                    left_add = AsNumber(left_add) + AsNumber(right_add);
                break;

            case Opcode.SUB:
            case Opcode.MUL:
            case Opcode.DIV:
            case Opcode.MOD:
                NumericBinary(instr.Opcode);
                break;

            case Opcode.NEG:
                if (_stack.Count > 0)
                    _stack.Push(-AsNumber(_stack.Pop()));
                else
                    throw new InvalidOperationException("Cannot negate empty stack");
                break;

            case Opcode.AND:
                BinaryOp((a, b) => AsBool(a) && AsBool(b) ? 1 : 0);
                break;

            case Opcode.OR:
                BinaryOp((a, b) => AsBool(a) || AsBool(b) ? 1 : 0);
                break;

            case Opcode.NOT:
                if (_stack.Count > 0)
                    _stack.Push(AsBool(_stack.Pop()) ? 0 : 1);
                else
                    throw new InvalidOperationException("Cannot negate empty stack");
                break;

            case Opcode.BIT_AND:
                BinaryOp((a, b) => AsInt32(a) & AsInt32(b));
                break;

            case Opcode.BIT_OR:
                BinaryOp((a, b) => AsInt32(a) | AsInt32(b));
                break;

            case Opcode.BIT_XOR:
                BinaryOp((a, b) => AsInt32(a) ^ AsInt32(b));
                break;

            case Opcode.BIT_NOT:
                if (_stack.Count > 0)
                    _stack.Push(~AsInt32(_stack.Pop()));
                else
                    throw new InvalidOperationException("Cannot apply bitwise NOT to empty stack");
                break;

            case Opcode.SHL:
                BinaryOp((a, b) => AsInt32(a) << AsInt32(b));
                break;

            case Opcode.SHR:
                BinaryOp((a, b) => AsInt32(a) >> AsInt32(b));
                break;

            case Opcode.EQ:
                BinaryOp((a, b) => VMEquals(a, b) ? 1 : 0);
                break;

            case Opcode.NEQ:
                BinaryOp((a, b) => !VMEquals(a, b) ? 1 : 0);
                break;

            case Opcode.LT:
            case Opcode.LTE:
            case Opcode.GT:
            case Opcode.GTE:
                NumericBinary(instr.Opcode);
                break;

            case Opcode.JUMP:
                if (instr.Operand is int jumpOffset)
                    pc = jumpOffset - 1;
                break;

            case Opcode.JUMP_IF_FALSE:
                if (instr.Operand is int falseOffset && _stack.Count > 0)
                {
                    if (!AsBool(_stack.Pop()))
                        pc = falseOffset - 1;
                }
                break;

            case Opcode.JUMP_IF_TRUE:
                if (instr.Operand is int trueOffset && _stack.Count > 0)
                {
                    if (AsBool(_stack.Pop()))
                        pc = trueOffset - 1;
                }
                break;

            case Opcode.LOAD_REG:
            {
                // Fast-path read from PgslContext via compiled slot getter (no string lookup).
                int slot = (int)instr.Operand;
                var ctx = _bridge.GetContext();
                if (ctx == null || slot < 0 || slot >= PgslRegisterFile.NumberGetters.Length) _stack.Push(0.0);
                else if (slot == PgslRegisterFile.SlotSpriteIndex) _stack.Push(ctx.SpriteIndex ?? "");
                else _stack.Push(PgslRegisterFile.NumberGetters[slot](ctx));
                break;
            }

            case Opcode.STORE_REG:
            {
                // Fast-path write to PgslContext via compiled slot setter.
                int slot = (int)instr.Operand;
                if (_stack.Count == 0)
                    throw new InvalidOperationException("Stack underflow on STORE_REG");
                var storeVal = _stack.Pop();
                var ctx = _bridge.GetContext();
                if (ctx != null && slot >= 0 && slot < PgslRegisterFile.NumberSetters.Length)
                    PgslRegisterFile.Write(ctx, slot, storeVal);
                break;
            }

            case Opcode.LOAD_LOCAL:
                // Frame-local slot read — currently falls through to the string-keyed variable dict.
                // Full local-frame allocation is a future optimisation pass.
                if (instr.Operand is int localLoadSlot && _variables.TryGetValue(SlotName(LocalSlotNames, "@local", localLoadSlot), out var localVal))
                    _stack.Push(localVal);
                else
                    _stack.Push(0.0);
                break;

            case Opcode.STORE_LOCAL:
                if (instr.Operand is int localStoreSlot && _stack.Count > 0)
                    _variables[SlotName(LocalSlotNames, "@local", localStoreSlot)] = _stack.Pop();
                break;

            case Opcode.CALL_NATIVE:
                ExecuteNativeCall(instr);
                break;

            case Opcode.CALL:
                ExecuteCall(instr);
                break;

            case Opcode.RETURN:
                _returnValue = _stack.Count > 0 ? _stack.Pop() : default;
                _returnRequested = true;
                break;

            case Opcode.PRINT:
                if (_stack.Count > 0)
                {
                    var printValue = _stack.Pop();
                    VMLogger.Log(printValue.ToString());
                }
                break;

            case Opcode.WITH_START:
                if (_stack.Count > 0)
                {
                    object targetValue = _stack.Pop().ToObject();
                    // A bare name is an Object's name, unless a variable of that name holds an id.
                    if (instr.Operand is 1 && targetValue is string bareName
                        && TryGetVariable(bareName, out VmValue held) && held.ToObject() is { } heldValue
                        && !(heldValue is string heldText && heldText.Length == 0))
                        targetValue = heldValue;
                    string targetObj = targetValue is IFormattable formattable
                        ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                        : targetValue?.ToString();
                    var objects = _bridge.FindObjects(targetObj);
                    var state = new WithState
                    {
                        Enumerator = objects.GetEnumerator(),
                        PreviousContext = _bridge.GetContext()
                    };
                    _withStack.Push(state);
                }
                else
                {
                    throw new InvalidOperationException("WITH_START requires a target object string on stack");
                }
                break;

            case Opcode.WITH_NEXT:
                if (_withStack.Count > 0 && instr.Operand is int endOffset)
                {
                    var state = _withStack.Peek();
                    state.Current?.EndActiveContext();
                    state.Current = null;
                    bool found = false;
                    while (state.Enumerator.MoveNext())
                    {
                        var obj = state.Enumerator.Current;
                        if (obj is IPgslInstance inst)
                        {
                            inst.SetActiveContext();
                            state.Current = inst;
                            found = true;
                            break;
                        }
                    }
                    
                    if (!found)
                    {
                        pc = endOffset - 1;
                    }
                }
                break;

            case Opcode.WITH_END:
                if (_withStack.Count > 0)
                {
                    var state = _withStack.Pop();
                    state.Current?.EndActiveContext();
                    _bridge.SetContext(state.PreviousContext);
                    state.Enumerator.Dispose();
                }
                break;

            case Opcode.GET_INDEX:
                {
                    if (_stack.Count < 2) throw new InvalidOperationException("Stack underflow on GET_INDEX");
                    var indexVal = _stack.Pop();
                    var collection = _stack.Pop();
                    _stack.Push(VmValue.FromObject(GetIndexValue(collection.ToObject(), indexVal.ToObject())));
                }
                break;

            case Opcode.SET_INDEX:
                {
                    if (_stack.Count < 3) throw new InvalidOperationException("Stack underflow on SET_INDEX");
                    var setVal = _stack.Pop();
                    var setIdx = _stack.Pop();
                    var setColl = _stack.Pop();
                    SetIndexValue(setColl.ToObject(), setIdx.ToObject(), setVal.ToObject());
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown opcode: {instr.Opcode}");
        }
    }

    private object GetIndexValue(object collection, object index)
    {
        if (collection is PGList pgList)
            return Genesis.Runtime.Scripting.PgslCommands.ReadListEntry(pgList, AsNumber(index));
        if (collection is PGMap pgMap)
        {
            return pgMap.Get(index?.ToString() ?? "");
        }
        if (collection is IList<dynamic> list)
        {
            int i = (int)AsNumber(index);
            return (i >= 0 && i < list.Count) ? list[i] : null;
        }
        if (collection is IDictionary<string, dynamic> dict)
        {
            string key = index.ToString();
            return dict.ContainsKey(key) ? dict[key] : null;
        }
        return null;
    }

    private void SetIndexValue(object collection, object index, object value)
    {
        if (collection is PGList pgList)
        {
            Genesis.Runtime.Scripting.PgslCommands.WriteListEntry(pgList, AsNumber(index), value);
            return;
        }
        if (collection is PGMap pgMap)
        {
            pgMap.Set(index?.ToString() ?? "", value);
            return;
        }
        if (collection is IList<dynamic> list)
        {
            int i = (int)AsNumber(index);
            if (i >= 0 && i < list.Count) list[i] = value;
            else if (i == list.Count) list.Add(value);
        }
        else if (collection is IDictionary<string, dynamic> dict)
        {
            dict[index.ToString()] = value;
        }
    }

    private void BinaryOp(Func<VmValue, VmValue, VmValue> op)
    {
        ref VmValue left = ref _stack.CombineTop(out VmValue right);
        left = op(left, right);
    }

    private void NumericBinary(Opcode operation)
    {
        ref VmValue leftValue = ref _stack.CombineTop(out VmValue rightValue);
        double right = rightValue.Number, left = leftValue.Number;
        leftValue = operation switch
        {
            Opcode.SUB => left - right, Opcode.MUL => left * right, Opcode.DIV => left / right,
            Opcode.MOD => Math.Abs(right) < 1e-12 ? (VmValue)0 : left - Math.Floor(left / right) * right,
            Opcode.LT => left < right ? 1 : 0, Opcode.LTE => left <= right ? 1 : 0,
            Opcode.GT => left > right ? 1 : 0, Opcode.GTE => left >= right ? 1 : 0,
            _ => throw new InvalidOperationException("Not a numeric opcode.")
        };
    }

    private int AsInt32(object value) => (int)AsNumber(value);
    private static int AsInt32(VmValue value) => (int)value.Number;
    private static double AsNumber(VmValue value) => value.Number;
    private static bool AsBool(VmValue value) => value.Truth;

    private double AsNumber(object value)
    {
        return value switch
        {
            int i => i,
            double d => d,
            float f => f,
            _ => Convert.ToDouble(value)
        };
    }

    private bool AsBool(object value)
    {
        return value switch
        {
            bool b => b,
            int i => i != 0,
            double d => d != 0,
            _ => value != null
        };
    }

    private void ExecuteNativeCall(Instruction instr)
    {
        if (instr.Operand is not (int nativeId, int nativeArgCount))
            throw new InvalidOperationException("Invalid CALL_NATIVE instruction format");
        ExecuteNativeCall(nativeId, nativeArgCount);
    }

    private void NoteNativeCall(int nativeId)
    {
        if (Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.IsCollecting)
        {
            Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.Note(
                Genesis.Runtime.Scripting.PgslNoteKind.CommandCalled,
                _bridge.NativeName(nativeId) ?? $"native#{nativeId}",
                string.Empty);
        }
    }

    // Reusable arrays per argument count for typed command calls. A command that runs PGSL again
    // (and so reuses them) has already read its arguments when it starts.
    private double[][] _numberArguments = new double[16][];
    private object[][] _textArguments = new object[16][];

    private double[] NumberArguments(int count)
    {
        if (count >= _numberArguments.Length) Array.Resize(ref _numberArguments, count + 1);
        return _numberArguments[count] ??= new double[count];
    }

    private object[] TextArguments(int count)
    {
        if (count >= _textArguments.Length) Array.Resize(ref _textArguments, count + 1);
        return _textArguments[count] ??= new object[count];
    }

    private void ExecuteNativeCall(int nativeId, int nativeArgCount)
    {
        if (_stack.Count < nativeArgCount)
            throw new InvalidOperationException($"Not enough arguments for native call id={nativeId}");

        PgslEngineBridge.NativeCall call = _typedBridge?.GetNativeCall(nativeId, nativeArgCount);
        if (call?.Kinds != null && _stack.TopMatches(call.Kinds))
        {
            // Every argument is already what the command takes: the same command, called without
            // boxing numbers or converting each argument.
            double[] numbers = NumberArguments(nativeArgCount);
            object[] texts = call.HasText ? TextArguments(nativeArgCount) : null;
            _stack.PopArguments(call.Kinds, numbers, texts);
            NoteNativeCall(nativeId);
            VmValue value;
            try
            {
                if (call.NumberCall != null) value = call.NumberCall(numbers, texts);
                else if (call.BoolCall != null) value = call.BoolCall(numbers, texts);
                else if (call.VoidCall != null) { call.VoidCall(numbers, texts); value = default; }
                else value = VmValue.FromObject(call.ObjectCall(numbers, texts));
            }
            finally
            {
                if (texts != null) Array.Clear(texts);
            }
            _stack.Push(_bridge.IsNativeVoid(nativeId) ? default : value);
            return;
        }

        object[] args = AcquireArgumentArray(nativeArgCount);
        for (int i = nativeArgCount - 1; i >= 0; i--)
            args[i] = _stack.Pop().ToObject();

        NoteNativeCall(nativeId);

        try
        {
            object result = call != null ? _typedBridge.InvokeNative(call, args) : _bridge.InvokeNative(nativeId, args);
            // Every call leaves exactly one value, as a user function does: a command that returns
            // nothing leaves null. The statement that called it pops one value, and that must be its
            // own, not one its caller was still holding (a script called inside an expression runs on
            // its caller's stack).
            _stack.Push(_bridge.IsNativeVoid(nativeId) ? default : VmValue.FromObject(result));
        }
        finally
        {
            ReleaseArgumentArray(args);
        }
    }

    private void ExecuteCall(Instruction instr)
    {
        if (instr.Operand is not (string funcName, int argCount))
            throw new InvalidOperationException("Invalid CALL instruction format");
        ExecuteCall(funcName, argCount);
    }

    private void ExecuteCall(string funcName, int argCount)
    {
        if (_stack.Count < argCount)
            throw new InvalidOperationException($"Not enough arguments for {funcName}");

        // A function of a project Script (a library) is callable directly, the first call loading it.
        bool isFunction = _userFunctions.TryGetValue(funcName, out UserFunction userFunc);
        if (!isFunction && (LibraryFunctions is { } fixedLibrary
                ? fixedLibrary.TryGetValue(funcName, out UserFunction library)
                : Genesis.Runtime.Scripting.ScriptAssetRegistry.TryFindFunction(funcName, out library)))
        {
            _userFunctions[funcName] = library;
            userFunc = library;
            isFunction = true;
        }

        if (isFunction)
        {
            if (argCount != userFunc.Parameters.Count)
            {
                throw new InvalidOperationException($"Function '{funcName}' expects {userFunc.Parameters.Count} arguments, got {argCount}");
            }

            // Deep recursion ends in an error the game can report, never in a stack overflow that
            // takes the whole process down.
            if (_frameCount >= MaxCallDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                ThrowTooDeep(funcName, _frameCount);

            PgslProgram program = PgslProgram.ForFunction(userFunc);
            VariableFrame frame = AcquireFrame(program.Layout);

            try
            {
                int[] parameterSlots = program.Layout.ParameterSlots;
                for (int i = argCount - 1; i >= 0; i--)
                {
                    int slot = parameterSlots[i];
                    frame.Values[slot] = _stack.Pop();
                    frame.Present[slot] = true;
                }

                PushFrame(frame);
                _debugCallStack.Push(funcName);
                ExecutionResult callResult = ExecuteCore(
                    program,
                    userFunc.Bytecode,
                    userFunc.Constants,
                    clearVariables: false);

                // Preserve stack correctness for expression-position calls: an explicit
                // "return;" still contributes a null value instead of underflowing later ops.
                _stack.Push(callResult.Returned ? callResult.Value : default);
            }
            finally
            {
                if (_frameCount > 0 && ReferenceEquals(_frames[_frameCount - 1], frame))
                    PopFrame();
                if (_debugCallStack.Count > 0 && string.Equals(_debugCallStack.Peek(), funcName, StringComparison.Ordinal))
                    _debugCallStack.Pop();
                ReleaseFrame(frame);
            }
            return;
        }

        object[] args = AcquireArgumentArray(argCount);
        for (int i = argCount - 1; i >= 0; i--)
            args[i] = _stack.Pop().ToObject();

        try
        {
            object result = ExecuteCommand(funcName, args);
            _stack.Push(_bridge.IsVoid(funcName, argCount) ? default : VmValue.FromObject(result));
        }
        finally
        {
            ReleaseArgumentArray(args);
        }
    }

    // The budget is per event body and per function call: each call starts its own count.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowInstructionLimit(string function) =>
        throw new InvalidOperationException(
            $"Infinite loop detected: Maximum instruction limit ({MAX_INSTRUCTIONS}) exceeded"
            + (function != null ? $" in function '{function}'" : " in this event")
            + ". Each event and each function call may run that many instructions; split long work across frames.");

    // The message VmValueStack.CombineTop gives.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowBinaryUnderflow() =>
        throw new InvalidOperationException("Stack underflow for binary operation");

    // Kept out of ExecuteCall so its message does not enlarge every call's stack frame.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ThrowTooDeep(string function, int depth) =>
        throw new InvalidOperationException(
            "Too much recursion: '" + function + "' is " + depth + " calls deep. Use a loop with a list or stack for deep work.");

    private object ExecuteCommand(string name, object[] args)
    {
        if (_debugMode)
            Console.WriteLine($"Calling command: {name} with {args.Length} args");

        Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.Note(
            Genesis.Runtime.Scripting.PgslNoteKind.CommandCalled, name, string.Empty);
        return _bridge.Invoke(name, args);
    }

    private PgslDebugLocation CreateDebugLocation(
        Instruction instruction,
        int programCounter,
        string errorMessage = "")
    {
        // Names differ by case (a function's `mbx` beside the instance's `mbX`), so the debugger
        // lists them as they are.
        Dictionary<string, object> variables = _variables.ToDictionary(pair => pair.Key, pair => pair.Value.ToObject(), StringComparer.Ordinal);
        for (int i = 0; i < _frameCount; i++)
        {
            foreach ((string name, VmValue value) in _frames[i].Entries()) variables[name] = value.ToObject();
        }
        PgslContext context = _bridge.GetContext();
        if (context is not null)
        {
            variables["x"] = context.X;
            variables["y"] = context.Y;
            variables["z"] = context.Z;
            variables["speed"] = context.Speed;
            variables["direction"] = context.Direction;
            variables["visible"] = context.Visible;
        }

        List<string> callStack = [];
        if (!string.IsNullOrWhiteSpace(DebugEventName)) callStack.Add(DebugEventName);
        callStack.AddRange(_debugCallStack.Reverse());
        string function = _debugCallStack.Count > 0
            ? _debugCallStack.Peek()
            : DebugEventName;
        return new PgslDebugLocation(
            DebugSourceName,
            DebugEventName,
            function ?? string.Empty,
            instruction?.LineNumber ?? 0,
            programCounter,
            callStack,
            variables,
            errorMessage ?? string.Empty);
    }

    // A name is looked for in the innermost call's frame, then each caller's, then the instance's
    // variables. Every one of them compares names with case: a function's `mbx` is not the
    // instance's `mbX` (only the built-in instance variables, compiled to registers, ignore case).
    private bool TryGetVariable(string name, out VmValue value) =>
        TryGetVariable(_frameCount > 0 ? PgslSymbols.Find(name) : -1, name, _frameCount - 1, out value);

    private bool TryGetVariable(int symbol, string name, int fromFrame, out VmValue value)
    {
        for (int i = fromFrame; i >= 0; i--)
        {
            if (_frames[i].TryGet(symbol, name, out value))
                return true;
        }
        return _variables.TryGetValue(name, out value);
    }

    private void StoreVariable(string name, VmValue value)
    {
        if (_frameCount > 0)
            _frames[_frameCount - 1].Set(PgslSymbols.Find(name), name, value);
        else
            _variables[name] = value;
    }

    /// <summary>LOAD_VAR of a decoded name; <paramref name="frame"/> is the running function's own frame, or null.</summary>
    private void LoadVariable(VarRef variable, VariableFrame frame)
    {
        if (variable.Kind == VarKind.Instance)
        {
            _stack.Push(LoadInstanceVariableByName(variable.Name));
            return;
        }
        bool found;
        VmValue value;
        if (frame != null && variable.Slot >= 0)
        {
            if (frame.Present[variable.Slot])
            {
                _stack.Push(frame.Values[variable.Slot]);
                return;
            }
            // Not set in this call: the callers' frames, then the instance.
            found = TryGetVariable(variable.Symbol, variable.Name, _frameCount - 2, out value);
        }
        else
        {
            found = TryGetVariable(variable.Symbol, variable.Name, _frameCount - 1, out value);
        }
        if (found)
            _stack.Push(value);
        else if (variable.Kind == VarKind.FunctionLocal)
            _stack.Push(0.0);
        else
            // Prefer local variables first, then fall back to PGSL context properties.
            _stack.Push(ResolvePGSLPropertyOrZero(variable.Name));
    }

    /// <summary>STORE_VAR of a decoded name.</summary>
    private void StoreVariable(VarRef variable, VariableFrame frame, VmValue value)
    {
        switch (variable.Kind)
        {
            case VarKind.Instance:
                StoreInstanceVariableByName(variable.Name, value);
                return;
            case VarKind.FunctionLocal:
                break;
            default:
                // Current bytecode emits STORE_REG for built-in instance fields; a declared
                // property is set through the bridge; anything else is a variable.
                if (variable.RegisterSlot >= 0)
                {
                    var context = _bridge.GetContext();
                    if (context != null)
                        PgslRegisterFile.Write(context, variable.RegisterSlot, value);
                    return;
                }
                if (TrySetDeclaredProperty(variable, value))
                    return;
                break;
        }
        if (frame != null && variable.Slot >= 0)
        {
            frame.Values[variable.Slot] = value;
            frame.Present[variable.Slot] = true;
        }
        else if (_frameCount > 0)
            _frames[_frameCount - 1].Set(variable.Symbol, variable.Name, value);
        else
            _variables[variable.Name] = value;
    }

    private bool TrySetDeclaredProperty(VarRef variable, VmValue value)
    {
        if (_typedBridge == null)
            return _bridge.TrySetProperty(variable.Name, value.ToObject());
        // Whether a name is a declared property only changes when the command table is rebuilt.
        if (variable.NotPropertyIn is PropertyTable table && ReferenceEquals(table.Bridge, _typedBridge)
            && table.Generation == _typedBridge.Generation)
            return false;
        if (_typedBridge.TrySetTypedProperty(variable.Name, value))
            return true;
        variable.NotPropertyIn = new PropertyTable(_typedBridge, _typedBridge.Generation);
        return false;
    }

    private VariableFrame AcquireFrame(FrameLayout layout)
    {
        VariableFrame frame = _framePool.Count > 0 ? _framePool.Pop() : new VariableFrame();
        frame.Bind(layout);
        return frame;
    }

    private void ReleaseFrame(VariableFrame frame)
    {
        frame.Reset();
        _framePool.Push(frame);
    }

    private void PushFrame(VariableFrame frame)
    {
        if (_frameCount == _frames.Length) Array.Resize(ref _frames, _frameCount * 2);
        _frames[_frameCount++] = frame;
    }

    private VariableFrame PopFrame()
    {
        VariableFrame frame = _frames[--_frameCount];
        _frames[_frameCount] = null;
        return frame;
    }

    private object[] AcquireArgumentArray(int count)
    {
        if (count == 0) return Array.Empty<object>();
        if (_argumentPools.TryGetValue(count, out Stack<object[]> pool) && pool.Count > 0)
            return pool.Pop();
        return new object[count];
    }

    private void ReleaseArgumentArray(object[] arguments)
    {
        if (arguments.Length == 0) return;
        Array.Clear(arguments, 0, arguments.Length);
        if (!_argumentPools.TryGetValue(arguments.Length, out Stack<object[]> pool))
        {
            pool = new Stack<object[]>();
            _argumentPools.Add(arguments.Length, pool);
        }
        // Keep the pool bounded for recursive scripts with unusual arities.
        if (pool.Count < 8) pool.Push(arguments);
    }

    private static string[] CreateSlotNames(string prefix, int count)
    {
        string[] names = new string[count];
        for (int i = 0; i < count; i++) names[i] = prefix + i;
        return names;
    }

    private static string SlotName(string[] cache, string prefix, int slot) =>
        (uint)slot < (uint)cache.Length ? cache[slot] : prefix + slot;

    private VmValue ResolvePGSLPropertyOrZero(string name)
    {
        if (ScriptingDebugSettings.VmUseRegisterFile)
        {
            var ctx = _bridge.GetContext();
            if (ctx != null && PgslRegisterFile.Slots.TryGetValue(name, out int slot))
                return PgslRegisterFile.Read(ctx, slot);
        }

        try { return VmValue.FromObject(_bridge.Invoke(name, Array.Empty<object>())); }
        // In a worker job a name with no value is an error, not 0: nothing else can have set it.
        catch (Exception ex) when (ex is not Genesis.Runtime.Scripting.PgslWorkerJobException)
        {
            bool unknown = ex.Message.Contains("Unknown command", StringComparison.OrdinalIgnoreCase);
            if (!unknown)
                VMLogger.LogDiag1D($"LOAD_VAR '{name}' fallback 0: {ex.Message}");

            // Substituting 0 keeps a shipped game running past a typo, but a tool must be able to
            // see that it happened — otherwise a script whose logic never fired reports success.
            Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.Note(
                unknown
                    ? Genesis.Runtime.Scripting.PgslNoteKind.UnresolvedRead
                    : Genesis.Runtime.Scripting.PgslNoteKind.ReadFailed,
                name,
                unknown ? "read before anything set it; used 0" : ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Attempts to write a value back to the live PgslContext via the bridge.
    /// Returns true if the name matched a known PGSL property (x, y, sprite_index, etc.)
    /// and the write succeeded. Returns false for user-defined variables.
    /// 
    /// This is the critical fix: without this, VM assignments like "x = x + 32" only
    /// update the VM's local dictionary. The PgslContext never changes, so the object
    /// never moves and DrawSelf() always draws at the original position.
    /// </summary>
    private bool TrySetPGSLProperty(string name, object value)
    {
        if (ScriptingDebugSettings.VmUseRegisterFile)
        {
            var ctx = _bridge.GetContext();
            if (PgslRegisterFile.TrySet(ctx, name, value))
                return true;
        }

        try
        {
            // Invoke with one argument — the bridge treats this as a property set
            object[] arguments = AcquireArgumentArray(1);
            arguments[0] = value;
            try
            {
                _bridge.Invoke(name, arguments);
                return true;
            }
            finally
            {
                ReleaseArgumentArray(arguments);
            }
        }
        catch (Exception ex)
        {
            // Only fallback to local variable if the property literally doesn't exist
            if (ex.Message.Contains("Unknown command"))
                return false;
            
            // Otherwise it's a real error (e.g. invalid type assignment)
            throw;
        }
    }

    private static bool VMEquals(VmValue a, VmValue b)
    {
        if (a.IsNull && b.IsNull) return true;
        if (a.IsNull || b.IsNull) return false;

        if (a.IsNumeric && b.IsNumeric)
        {
            return Math.Abs(AsNumber(a) - AsNumber(b)) < 1e-9;
        }

        if (a.IsBoolean && b.IsNumeric)
        {
            return a.Truth == (AsNumber(b) != 0);
        }
        if (b.IsBoolean && a.IsNumeric)
        {
            return b.Truth == (AsNumber(a) != 0);
        }

        return Equals(a.ToObject(), b.ToObject());
    }

    private bool IsNumeric(object value)
    {
        return value is int || value is double || value is float || value is long || 
               value is byte || value is short || value is decimal || value is uint || 
               value is ulong || value is ushort || value is sbyte;
    }

    public Dictionary<string, object> GetVariables() => _variables.ToDictionary(pair => pair.Key, pair => pair.Value.ToObject());

    /// <summary>
    /// Mutates one value in this live VM without recompiling or clearing its state. Built-in
    /// instance registers are written through to the active <see cref="PgslContext"/>; user
    /// variables retain the type established by the running script.
    /// </summary>
    public bool TrySetLiveVariable(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (PgslRegisterFile.Slots.TryGetValue(name, out int slot))
        {
            PgslContext context = _bridge.GetContext();
            if (context is null || slot < 0 || slot >= PgslRegisterFile.NumberSetters.Length)
                return false;

            try
            {
                PgslRegisterFile.Write(context, slot, VmValue.FromObject(value));
                return true;
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        if (!_variables.TryGetValue(name, out VmValue current) || current.IsNull)
            return false;

        try
        {
            _variables[name] = VmValue.FromObject(ConvertLike(current.ToObject(), value));
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Reads a user variable or built-in instance register from the live VM.</summary>
    public bool TryGetLiveVariable(string name, out object value)
    {
        if (PgslRegisterFile.Slots.TryGetValue(name, out int slot))
        {
            PgslContext context = _bridge.GetContext();
            if (context is not null && slot >= 0 && slot < PgslRegisterFile.NumberGetters.Length)
            {
                value = PgslRegisterFile.Read(context, slot).ToObject();
                return true;
            }
        }

        bool found = _variables.TryGetValue(name, out VmValue typed);
        value = typed.ToObject(); return found;
    }

    private static object ConvertLike(object current, object value)
    {
        if (current is bool)
        {
            if (value is string text && bool.TryParse(text, out bool parsed)) return parsed;
            return value is bool flag ? flag : Convert.ToDouble(value) != 0;
        }
        if (current is string) return Convert.ToString(value) ?? string.Empty;
        if (current is int) return Convert.ToInt32(value);
        if (current is long) return Convert.ToInt64(value);
        if (current is float) return Convert.ToSingle(value);
        if (current is decimal) return Convert.ToDecimal(value);
        if (current is double) return Convert.ToDouble(value);
        return value;
    }
}
