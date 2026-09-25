#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Genesis.Runtime.Scripting.PG;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting.VM;

public class PgslVm
{
    private class WithState
    {
        public IEnumerator<object> Enumerator { get; set; }
        public PgslContext PreviousContext { get; set; }
    }

    private readonly Stack<object> _stack = new();
    private readonly Dictionary<string, object> _variables = new();
    private readonly Stack<Dictionary<string, object>> _variableFrames = new();
    private readonly Stack<Dictionary<string, object>> _variableFramePool = new();
    private readonly Dictionary<int, Stack<object[]>> _argumentPools = new();
    private IReadOnlyList<object> _constants = Array.Empty<object>();
    private readonly Dictionary<string, UserFunction> _userFunctions = new();
    private readonly Stack<WithState> _withStack = new();
    private readonly Stack<string> _debugCallStack = new();
    private readonly IPgslEngineBridge _bridge;
    private bool _debugMode = false;
    private int _instructionCounter = 0;
    private bool _returnRequested;
    private object _returnValue;
    private const int MAX_INSTRUCTIONS = 100000; // Hard limit per event to prevent hangs
    private static readonly string[] LocalSlotNames = CreateSlotNames("@local", 256);
    private static readonly string[] ArgumentNames = CreateSlotNames("argument", 64);

    private readonly record struct ExecutionResult(bool Returned, object Value);

    public IPgslEngineBridge Bridge => _bridge;
    public PgslDebugController Debugger { get; set; }
    public string DebugSourceName { get; set; } = string.Empty;
    public string DebugEventName { get; set; } = string.Empty;

    public PgslVm(IPgslEngineBridge bridge, bool debugMode = false)
    {
        _bridge = bridge;
        _debugMode = debugMode;
    }

    public void SetScriptArguments(object[] args)
    {
      if (args == null) return;
      for (int i = 0; i < args.Length; i++)
        _variables[SlotName(ArgumentNames, "argument", i)] = args[i];
      _variables["argument_count"] = args.Length;
    }

    /// <summary>Sets a persistent script variable by name for PGSL visual-action commands.</summary>
    public void SetVariable(string name, object value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _variables[name.Trim()] = value;
    }

    /// <summary>Reads a persistent script variable without exposing the VM's mutable dictionary.</summary>
    public bool TryReadVariable(string name, out object value) =>
        TryGetVariable(name?.Trim() ?? string.Empty, out value);

    public void LoadUserFunctions(Dictionary<string, UserFunction> userFunctions)
    {
        foreach (var func in userFunctions)
        {
            _userFunctions[func.Key] = func.Value;
        }
    }

    public void Execute(List<Instruction> instructions, List<object> constants, bool clearVariables = true)
    {
        try
        {
            ExecutionResult result = ExecuteCore(instructions, constants, clearVariables);
            if (result.Returned)
                throw new ReturnException(result.Value);
        }
        finally
        {
            Debugger?.OnCompleted();
        }
    }

    private ExecutionResult ExecuteCore(
        List<Instruction> instructions,
        List<object> constants,
        bool clearVariables)
    {
        int previousInstructionCounter = _instructionCounter;
        bool previousReturnRequested = _returnRequested;
        object previousReturnValue = _returnValue;

        IReadOnlyList<object> previousConstants = _constants;
        _instructionCounter = 0;
        _returnRequested = false;
        _returnValue = null;
        _constants = constants;
        if (clearVariables)
        {
            _variables.Clear();
            _stack.Clear();
            while (_variableFrames.Count > 0)
                ReleaseVariableFrame(_variableFrames.Pop());
        }

        int pc = 0;

        try
        {
            while (pc < instructions.Count)
            {
                if (++_instructionCounter > MAX_INSTRUCTIONS)
                {
                    throw new InvalidOperationException($"Infinite loop detected: Maximum instruction limit ({MAX_INSTRUCTIONS}) exceeded.");
                }

                var instr = instructions[pc];

                if (Debugger is not null)
                {
                    Debugger.BeforeInstruction(this, CreateDebugLocation(instr, pc));
                }

                if (_debugMode)
                    Console.WriteLine($"[PC:{pc}] {instr}");

                ExecuteInstruction(instr, ref pc);
                pc++;

                if (_returnRequested)
                    break;
            }

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
        catch (Exception ex)
        {
            var currentInstr = (pc < instructions.Count) ? instructions[pc] : null;
            int line = currentInstr?.LineNumber ?? 0;
            if (Debugger is not null && ex.Data["PgslDebugReported"] is not true)
            {
                Debugger.OnError(CreateDebugLocation(currentInstr, pc, ex.Message));
                ex.Data["PgslDebugReported"] = true;
            }
            var vmEx = new Exception($"Line {line}: {ex.Message}");
            vmEx.Data["Line"] = line;
            vmEx.Data["PgslDebugReported"] = true;
            throw vmEx;
        }
        finally
        {
            _constants = previousConstants;
            _instructionCounter = previousInstructionCounter;
            _returnRequested = previousReturnRequested;
            _returnValue = previousReturnValue;
        }
    }

    private void ExecuteInstruction(Instruction instr, ref int pc)
    {
        switch (instr.Opcode)
        {
            case Opcode.PUSH_NULL:
                _stack.Push(null);
                break;

            case Opcode.PUSH:
                _stack.Push(instr.Operand);
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
                    _stack.Push(_constants[idx]);
                else
                    throw new InvalidOperationException($"Invalid constant index: {instr.Operand}");
                break;

            case Opcode.LOAD_VAR:
                if (instr.Operand is string varName)
                {
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
                    // Current bytecode emits STORE_REG for built-in instance fields. For the much
                    // smaller declared-property set, use an explicit lookup rather than restoring
                    // the old exception-driven command probe on every ordinary variable assignment.
                    if (PgslRegisterFile.Slots.TryGetValue(storeName, out int storeSlot))
                    {
                        var context = _bridge.GetContext();
                        if (context != null)
                            PgslRegisterFile.SlotSetters[storeSlot](context, storeValue);
                    }
                    else if (!_bridge.TrySetProperty(storeName, storeValue))
                    {
                        StoreVariable(storeName, storeValue);
                    }
                }
                else
                    throw new InvalidOperationException("Cannot store variable");
                break;

            case Opcode.ADD:
                if (_stack.Count < 2) throw new InvalidOperationException("Stack underflow on ADD");
                var right_add = _stack.Pop();
                var left_add = _stack.Pop();
                if (left_add is string || right_add is string)
                    _stack.Push(left_add?.ToString() + right_add?.ToString());
                else
                    _stack.Push(AsNumber(left_add) + AsNumber(right_add));
                break;

            case Opcode.SUB:
                BinaryOp((a, b) => AsNumber(a) - AsNumber(b));
                break;

            case Opcode.MUL:
                BinaryOp((a, b) => AsNumber(a) * AsNumber(b));
                break;

            case Opcode.DIV:
                BinaryOp((a, b) => AsNumber(a) / AsNumber(b));
                break;

            case Opcode.MOD:
                BinaryOp((a, b) =>
                {
                    double divisor = AsNumber(b);
                    if (Math.Abs(divisor) < 1e-12) return 0;
                    double dividend = AsNumber(a);
                    return dividend - Math.Floor(dividend / divisor) * divisor;
                });
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
                BinaryOp((a, b) => AsNumber(a) < AsNumber(b) ? 1 : 0);
                break;

            case Opcode.LTE:
                BinaryOp((a, b) => AsNumber(a) <= AsNumber(b) ? 1 : 0);
                break;

            case Opcode.GT:
                BinaryOp((a, b) => AsNumber(a) > AsNumber(b) ? 1 : 0);
                break;

            case Opcode.GTE:
                BinaryOp((a, b) => AsNumber(a) >= AsNumber(b) ? 1 : 0);
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
                _stack.Push(slot >= 0 && slot < PgslRegisterFile.SlotGetters.Length && ctx != null
                    ? PgslRegisterFile.SlotGetters[slot](ctx)
                    : 0.0);
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
                if (ctx != null && slot >= 0 && slot < PgslRegisterFile.SlotSetters.Length)
                    PgslRegisterFile.SlotSetters[slot](ctx, storeVal);
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
                _returnValue = _stack.Count > 0 ? _stack.Pop() : null;
                _returnRequested = true;
                break;

            case Opcode.PRINT:
                if (_stack.Count > 0)
                {
                    var printValue = _stack.Pop();
                    VMLogger.Log(printValue?.ToString() ?? "");
                }
                break;

            case Opcode.WITH_START:
                if (_stack.Count > 0)
                {
                    var targetObj = _stack.Pop()?.ToString();
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
                    bool found = false;
                    while (state.Enumerator.MoveNext())
                    {
                        var obj = state.Enumerator.Current;
                        if (obj is IPgslInstance inst)
                        {
                            inst.SetActiveContext();
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
                    _bridge.SetContext(state.PreviousContext);
                    state.Enumerator.Dispose();
                }
                break;

            case Opcode.GET_INDEX:
                {
                    if (_stack.Count < 2) throw new InvalidOperationException("Stack underflow on GET_INDEX");
                    var indexVal = _stack.Pop();
                    var collection = _stack.Pop();
                    _stack.Push(GetIndexValue(collection, indexVal));
                }
                break;

            case Opcode.SET_INDEX:
                {
                    if (_stack.Count < 3) throw new InvalidOperationException("Stack underflow on SET_INDEX");
                    var setVal = _stack.Pop();
                    var setIdx = _stack.Pop();
                    var setColl = _stack.Pop();
                    SetIndexValue(setColl, setIdx, setVal);
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown opcode: {instr.Opcode}");
        }
    }

    private object GetIndexValue(object collection, object index)
    {
        if (collection is PGList pgList)
        {
            int i = AsInt32(index);
            return pgList.Get(i);
        }
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
            pgList.Set(AsInt32(index), value);
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

    private void BinaryOp(Func<object, object, object> op)
    {
        if (_stack.Count < 2)
            throw new InvalidOperationException("Stack underflow for binary operation");

        var b = _stack.Pop();
        var a = _stack.Pop();
        _stack.Push(op(a, b));
    }

    private int AsInt32(object value) => (int)AsNumber(value);

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

        if (_stack.Count < nativeArgCount)
            throw new InvalidOperationException($"Not enough arguments for native call id={nativeId}");

        object[] args = AcquireArgumentArray(nativeArgCount);
        for (int i = nativeArgCount - 1; i >= 0; i--)
            args[i] = _stack.Pop();

        if (Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.IsCollecting)
        {
            Genesis.Runtime.Scripting.PgslRuntimeDiagnostics.Note(
                Genesis.Runtime.Scripting.PgslNoteKind.CommandCalled,
                _bridge.NativeName(nativeId) ?? $"native#{nativeId}",
                string.Empty);
        }

        try
        {
            object result = _bridge.InvokeNative(nativeId, args);
            if (!_bridge.IsNativeVoid(nativeId))
                _stack.Push(result);
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

        if (_stack.Count < argCount)
            throw new InvalidOperationException($"Not enough arguments for {funcName}");

        if (_userFunctions.TryGetValue(funcName, out UserFunction userFunc))
        {
            if (argCount != userFunc.Parameters.Count)
            {
                throw new InvalidOperationException($"Function '{funcName}' expects {userFunc.Parameters.Count} arguments, got {argCount}");
            }

            Dictionary<string, object> frame = AcquireVariableFrame();

            try
            {
                for (int i = argCount - 1; i >= 0; i--)
                {
                    frame[userFunc.Parameters[i]] = _stack.Pop();
                }

                _variableFrames.Push(frame);
                _debugCallStack.Push(funcName);
                ExecutionResult callResult = ExecuteCore(
                    userFunc.Bytecode,
                    userFunc.Constants,
                    clearVariables: false);

                // Preserve stack correctness for expression-position calls: an explicit
                // "return;" still contributes a null value instead of underflowing later ops.
                _stack.Push(callResult.Returned ? callResult.Value : null);
            }
            finally
            {
                if (_variableFrames.Count > 0 && ReferenceEquals(_variableFrames.Peek(), frame))
                    _variableFrames.Pop();
                if (_debugCallStack.Count > 0 && string.Equals(_debugCallStack.Peek(), funcName, StringComparison.Ordinal))
                    _debugCallStack.Pop();
                ReleaseVariableFrame(frame);
            }
            return;
        }

        object[] args = AcquireArgumentArray(argCount);
        for (int i = argCount - 1; i >= 0; i--)
            args[i] = _stack.Pop();

        try
        {
            object result = ExecuteCommand(funcName, args);
            if (!_bridge.IsVoid(funcName, argCount))
                _stack.Push(result);
        }
        finally
        {
            ReleaseArgumentArray(args);
        }
    }

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
        Dictionary<string, object> variables = new(_variables, StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object> frame in _variableFrames.Reverse())
        {
            foreach ((string name, object value) in frame) variables[name] = value;
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

    private bool TryGetVariable(string name, out object value)
    {
        foreach (Dictionary<string, object> frame in _variableFrames)
        {
            if (frame.TryGetValue(name, out value))
                return true;
        }
        return _variables.TryGetValue(name, out value);
    }

    private void StoreVariable(string name, object value)
    {
        if (_variableFrames.Count > 0)
            _variableFrames.Peek()[name] = value;
        else
            _variables[name] = value;
    }

    private Dictionary<string, object> AcquireVariableFrame() =>
        _variableFramePool.Count > 0
            ? _variableFramePool.Pop()
            : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

    private void ReleaseVariableFrame(Dictionary<string, object> frame)
    {
        frame.Clear();
        _variableFramePool.Push(frame);
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

    private object ResolvePGSLPropertyOrZero(string name)
    {
        if (ScriptingDebugSettings.VmUseRegisterFile)
        {
            var ctx = _bridge.GetContext();
            if (PgslRegisterFile.TryGet(ctx, name, out var regVal))
                return regVal;
        }

        try { return _bridge.Invoke(name, Array.Empty<object>()); }
        catch (Exception ex)
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

    private bool VMEquals(object a, object b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        if (IsNumeric(a) && IsNumeric(b))
        {
            return Math.Abs(AsNumber(a) - AsNumber(b)) < 1e-9;
        }

        if (a is bool ba && IsNumeric(b))
        {
            return ba == (AsNumber(b) != 0);
        }
        if (b is bool bb && IsNumeric(a))
        {
            return bb == (AsNumber(a) != 0);
        }

        return Equals(a, b);
    }

    private bool IsNumeric(object value)
    {
        return value is int || value is double || value is float || value is long || 
               value is byte || value is short || value is decimal || value is uint || 
               value is ulong || value is ushort || value is sbyte;
    }

    public Dictionary<string, object> GetVariables() => new(_variables);

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
            if (context is null || slot < 0 || slot >= PgslRegisterFile.SlotSetters.Length)
                return false;

            try
            {
                PgslRegisterFile.SlotSetters[slot](context, value);
                return true;
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        if (!_variables.TryGetValue(name, out object current) || current is null)
            return false;

        try
        {
            _variables[name] = ConvertLike(current, value);
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
            if (context is not null && slot >= 0 && slot < PgslRegisterFile.SlotGetters.Length)
            {
                value = PgslRegisterFile.SlotGetters[slot](context);
                return true;
            }
        }

        return _variables.TryGetValue(name, out value);
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
