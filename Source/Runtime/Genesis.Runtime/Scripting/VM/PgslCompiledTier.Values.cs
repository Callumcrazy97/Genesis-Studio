#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// The second tier for a function that works with text or other values, not numbers only (a map
/// key made with StringOf, a part of a split text, VariableSet by name): every variable and stack
/// place holds a <see cref="VmValue"/> as the interpreter's do, each operation does what the
/// interpreter's does with them (the same helpers), and each command is called the interpreter's
/// way. It saves the interpreter's dispatch, decoding and stack, not the values' checks, and runs
/// only where every command it calls is one that cannot run a script (data, text, maths,
/// variables, drawing, or a numbers-only command), as the numbers-only tier.
/// GENESIS_PGSL_TIER2_VALUES=0 turns it off (GENESIS_PGSL_TIER2=0 turns both off).
/// </summary>
internal static partial class PgslCompiledTier
{
    public delegate VmValue CompiledValues(VmValue[] arguments, PgslVm vm);

    internal static bool ValuesEnabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("GENESIS_PGSL_TIER2_VALUES"), "0", StringComparison.Ordinal);

    [ThreadStatic] private static VmValue[][] _valueScratch;

    public static VmValue[] ValueScratch(int count)
    {
        _valueScratch ??= new VmValue[33][];
        if (count >= _valueScratch.Length) return new VmValue[count];
        return _valueScratch[count] ??= new VmValue[count];
    }

    // ── What each operation does with values: the interpreter's own rules (PgslVm.ExecuteCore). ──

    public static VmValue Add(VmValue left, VmValue right) =>
        left.IsString || right.IsString ? (VmValue)(left.ToString() + right.ToString()) : left.Number + right.Number;
    public static VmValue Sub(VmValue left, VmValue right) => left.Number - right.Number;
    public static VmValue Mul(VmValue left, VmValue right) => left.Number * right.Number;
    public static VmValue Div(VmValue left, VmValue right) => left.Number / right.Number;
    public static VmValue Mod(VmValue left, VmValue right)
    {
        double b = right.Number, a = left.Number;
        return Math.Abs(b) < 1e-12 ? (VmValue)0 : a - Math.Floor(a / b) * b;
    }
    public static VmValue Lt(VmValue left, VmValue right) => left.Number < right.Number ? 1 : 0;
    public static VmValue Lte(VmValue left, VmValue right) => left.Number <= right.Number ? 1 : 0;
    public static VmValue Gt(VmValue left, VmValue right) => left.Number > right.Number ? 1 : 0;
    public static VmValue Gte(VmValue left, VmValue right) => left.Number >= right.Number ? 1 : 0;
    public static VmValue Eq(VmValue left, VmValue right) => PgslVm.VMEquals(left, right) ? 1 : 0;
    public static VmValue Neq(VmValue left, VmValue right) => !PgslVm.VMEquals(left, right) ? 1 : 0;
    public static VmValue And(VmValue left, VmValue right) => left.Truth && right.Truth ? 1 : 0;
    public static VmValue Or(VmValue left, VmValue right) => left.Truth || right.Truth ? 1 : 0;
    public static VmValue BitAnd(VmValue left, VmValue right) => (int)left.Number & (int)right.Number;
    public static VmValue BitOr(VmValue left, VmValue right) => (int)left.Number | (int)right.Number;
    public static VmValue BitXor(VmValue left, VmValue right) => (int)left.Number ^ (int)right.Number;
    public static VmValue Shl(VmValue left, VmValue right) => (int)left.Number << (int)right.Number;
    public static VmValue Shr(VmValue left, VmValue right) => (int)left.Number >> (int)right.Number;
    public static VmValue Not(VmValue value) => value.Truth ? 0 : 1;
    public static VmValue Neg(VmValue value) => -value.Number;
    public static VmValue BitNot(VmValue value) => ~(int)value.Number;
    public static bool Truth(VmValue value) => value.Truth;

    /// <summary>
    /// A call from a function with values to one compiled with numbers too: its numbers-only form
    /// when every argument and every name it reads from outside is a number now (as a call from the
    /// interpreter would run it), else its form with values.
    /// </summary>
    public static VmValue CallNumbersOrValues(UserFunction function, CompiledValues values, VmValue[] arguments, int count, PgslVm vm)
    {
        // This VM's own bridge's form: a worker's refuses the commands a job may not call.
        Entry callee = vm.NumbersEntry(function);
        Compiled numbers = callee.Run;
        if (numbers != null && vm.FreeReadsAreNumbers(callee.FreeReads))
        {
            double[] given = Scratch(count);
            int i = 0;
            for (; i < count && arguments[i].IsUnboxedNumber; i++) given[i] = arguments[i].UnboxedNumber;
            if (i == count)
            {
                NoValue = false;
                double result = numbers(given, vm);
                return NoValue ? default : (VmValue)result;
            }
        }
        return values(arguments, vm);
    }

    private static MethodInfo Op(string name) => typeof(PgslCompiledTier).GetMethod(name, BindingFlags.Public | BindingFlags.Static);
    private static readonly MethodInfo ValueScratchMethod = Op(nameof(ValueScratch));
    private static readonly MethodInfo TruthMethod = Op(nameof(Truth));
    private static readonly MethodInfo CallNumbersOrValuesMethod = Op(nameof(CallNumbersOrValues));
    private static readonly Dictionary<Opcode, MethodInfo> BinaryOps = new()
    {
        [Opcode.ADD] = Op(nameof(Add)), [Opcode.SUB] = Op(nameof(Sub)), [Opcode.MUL] = Op(nameof(Mul)), [Opcode.DIV] = Op(nameof(Div)),
        [Opcode.MOD] = Op(nameof(Mod)), [Opcode.LT] = Op(nameof(Lt)), [Opcode.LTE] = Op(nameof(Lte)), [Opcode.GT] = Op(nameof(Gt)),
        [Opcode.GTE] = Op(nameof(Gte)), [Opcode.EQ] = Op(nameof(Eq)), [Opcode.NEQ] = Op(nameof(Neq)), [Opcode.AND] = Op(nameof(And)),
        [Opcode.OR] = Op(nameof(Or)), [Opcode.BIT_AND] = Op(nameof(BitAnd)), [Opcode.BIT_OR] = Op(nameof(BitOr)),
        [Opcode.BIT_XOR] = Op(nameof(BitXor)), [Opcode.SHL] = Op(nameof(Shl)), [Opcode.SHR] = Op(nameof(Shr)),
    };
    private static readonly Dictionary<Opcode, MethodInfo> UnaryOps = new()
    {
        [Opcode.NOT] = Op(nameof(Not)), [Opcode.NEG] = Op(nameof(Neg)), [Opcode.BIT_NOT] = Op(nameof(BitNot)),
    };
    private static readonly MethodInfo ReadFreeValueMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.ReadFreeValue), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriteInstanceValueMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.WriteInstanceValue), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ReadRegisterValueMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.ReadRegisterValue), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriteRegisterValueMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.WriteRegisterValue), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo CallNativeValuesMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.CallNativeValues), BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// A function compiled with numbers only, compiled with values too, for a function with values
    /// that calls it (made once, kept on its entry).
    /// </summary>
    private static CompiledValues ValuesOf(PgslEngineBridge bridge, UserFunction function, Entry entry, Func<string, UserFunction> resolve,
        ConcurrentDictionary<UserFunction, Entry> table, Dictionary<UserFunction, Holder> building)
    {
        if (entry.RunValues != null) return entry.RunValues;
        var holder = new Holder();
        building[function] = holder;
        try
        {
            CompiledValues values = CompileValues(bridge, function, resolve, building, table, out _, out _);
            holder.RunValues = values;
            if (values != null) entry.RunValues = values;
            return values;
        }
        catch (Exception) { return null; }
        finally { building.Remove(function); }
    }

    /// <summary>A function compiled with values (or why not), kept on the function for every bridge.</summary>
    internal sealed class ValuesCompile(CompiledValues run, VarRef[] freeReads, string reason)
    {
        public readonly CompiledValues Run = run;
        public readonly VarRef[] FreeReads = freeReads;
        public readonly string Reason = reason;
    }

    // Compiled once for all bridges: the 26 worker bridges each compiling a large function again cost
    // more than running it interpreted. A refusal is kept only from the game's bridge (a worker's
    // refuses commands a job may not call, which the game's calls).
    private static CompiledValues CompileValues(PgslEngineBridge bridge, UserFunction function, Func<string, UserFunction> resolve,
        Dictionary<UserFunction, Holder> building, ConcurrentDictionary<UserFunction, Entry> table, out string reason, out VarRef[] freeReads)
    {
        if (function.Values is { } known && (known.Run != null || !bridge.IsWorker))
        {
            reason = known.Reason;
            freeReads = known.FreeReads;
            return known.Run;
        }
        CompiledValues run = CompileValuesNow(bridge, function, resolve, building, table, out reason, out freeReads);
        if (run != null || !bridge.IsWorker) function.Values = new ValuesCompile(run, freeReads, reason);
        return run;
    }

    private static CompiledValues CompileValuesNow(PgslEngineBridge bridge, UserFunction function, Func<string, UserFunction> resolve,
        Dictionary<UserFunction, Holder> building, ConcurrentDictionary<UserFunction, Entry> table, out string reason, out VarRef[] freeReads)
    {
        reason = null;
        freeReads = null;
        PgslProgram program = PgslProgram.ForFunction(function);
        VmOp[] ops = program.Ops;
        FrameLayout layout = program.Layout;
        int[] parameterSlots = layout.ParameterSlots;
        var isParameter = new bool[Math.Max(1, layout.Count)];
        foreach (int slot in parameterSlots) isParameter[slot] = true;

        // 0. Which names are the function's own (as the numbers-only tier decides).
        var assigned = new bool[Math.Max(1, layout.Count)];
        foreach (VmOp each in ops)
            if (each.Code == Opcode.STORE_VAR && each.Ref is VarRef stored && stored.Slot >= 0 && stored.Kind != VarKind.Instance && stored.RegisterSlot < 0)
                assigned[stored.Slot] = true;
        bool Own(VarRef v) => v.Slot >= 0 && v.Kind != VarKind.Instance
            && (v.Kind == VarKind.FunctionLocal || isParameter[v.Slot] || (assigned[v.Slot] && v.RegisterSlot < 0 && !bridge.IsDeclaredProperty(v.Name)));
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        if (function.Parameters != null) foreach (string parameter in function.Parameters) if (parameter != null) ownNames.Add(parameter);
        foreach (VmOp each in ops)
            if (each.Code is Opcode.LOAD_VAR or Opcode.STORE_VAR && each.Ref is VarRef named && Own(named)) ownNames.Add(named.Name);
        var free = new List<VarRef>();
        var freeKeys = new HashSet<(VarKind, string)>();
        void AddFree(VarRef v) { if (freeKeys.Add((v.Kind == VarKind.Instance ? VarKind.Instance : VarKind.Plain, v.Name))) free.Add(v); }

        // 1. Every instruction one this tier knows, with the stack as deep on every path to it. Text
        // constants are followed (on straight paths) only to check what a variable command names.
        int n = ops.Length;
        int[] depth = new int[n + 1];
        Array.Fill(depth, -1);
        var textsAt = new VmValue[n + 1][];
        var callees = new Expression[n];
        var calleeFunctions = new UserFunction[n];
        var work = new Stack<int>();
        int maxDepth = 0;
        bool Reach(int pc, int d, VmValue[] texts)
        {
            if (pc < 0 || pc > n) return false;
            if (depth[pc] == -1) { depth[pc] = d; textsAt[pc] = texts; work.Push(pc); return true; }
            if (depth[pc] != d) return false;
            // Two paths meet: what either knew of its constants no longer holds, there or after.
            if (textsAt[pc] != null) { textsAt[pc] = null; work.Push(pc); }
            return true;
        }
        if (!Reach(0, 0, null)) { reason = "empty"; return null; }
        while (work.Count > 0)
        {
            int pc = work.Pop();
            if (pc == n) continue;
            int d = depth[pc];
            VmValue[] texts = textsAt[pc];
            ref VmOp op = ref ops[pc];
            int need = 0, change = 0;
            int jump = -1;
            bool falls = true;
            switch (op.Code)
            {
                case Opcode.PUSH: case Opcode.LOAD_CONST: change = 1; break;
                case Opcode.LOAD_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    if (!Own(variable)) AddFree(variable);
                    change = 1; break;
                }
                case Opcode.STORE_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    if (!Own(variable) && variable.Kind != VarKind.Instance && variable.RegisterSlot < 0)
                    { reason = $"writes '{variable.Name}', not its own variable"; return null; }
                    need = 1; change = -1; break;
                }
                case Opcode.LOAD_REG: case Opcode.PUSH_NULL: change = 1; break;
                case Opcode.STORE_REG: case Opcode.POP: need = 1; change = -1; break;
                case Opcode.DUP: need = 1; change = 1; break;
                case Opcode.SWAP: need = 2; break;
                case Opcode.ADD: case Opcode.SUB: case Opcode.MUL: case Opcode.DIV: case Opcode.MOD:
                case Opcode.LT: case Opcode.LTE: case Opcode.GT: case Opcode.GTE: case Opcode.EQ: case Opcode.NEQ:
                case Opcode.AND: case Opcode.OR: case Opcode.BIT_AND: case Opcode.BIT_OR: case Opcode.BIT_XOR: case Opcode.SHL: case Opcode.SHR:
                    need = 2; change = -1; break;
                case Opcode.NEG: case Opcode.NOT: case Opcode.BIT_NOT: need = 1; break;
                case Opcode.JUMP: jump = op.A; falls = false; break;
                case Opcode.JUMP_IF_FALSE: case Opcode.JUMP_IF_TRUE: need = 1; change = -1; jump = op.A; break;
                case Opcode.RETURN: falls = false; break;
                case Opcode.CALL_NATIVE:
                {
                    PgslEngineBridge.NativeCall call = bridge.GetNativeCall(op.A, op.B);
                    if (!CalledWithValues(bridge, call, op.A, op.B, texts, d, ownNames, out reason)) return null;
                    need = op.B; change = 1 - op.B; break;
                }
                case Opcode.CALL:
                {
                    string name = op.Ref as string;
                    UserFunction target = name == null ? null : resolve(name);
                    if (target == null || (target.Parameters?.Count ?? -1) != op.B) { reason = $"calls '{name}', which is not a script function taking {op.B}"; return null; }
                    if (building.TryGetValue(target, out Holder holder))
                    {
                        // Itself: read from its holder when called, as it is still being made.
                        if (!ReferenceEquals(target, function)) { reason = $"calls '{name}', which calls it back"; return null; }
                        callees[pc] = Expression.Field(Expression.Constant(holder), nameof(Holder.RunValues));
                    }
                    else
                    {
                        Entry other = Build(bridge, target, resolve, building, table);
                        if (other.Run == null && other.RunValues == null) { reason = $"calls '{name}', which stays interpreted ({other.Reason})"; return null; }
                        CompiledValues values = other.RunValues ?? ValuesOf(bridge, target, other, resolve, table, building);
                        if (values == null) { reason = $"calls '{name}', which cannot be compiled with values"; return null; }
                        // The callee would see this function's own names through its frame: refused where it reads one.
                        foreach (VarRef read in other.FreeReads)
                        {
                            if (read.Kind != VarKind.Instance && ownNames.Contains(read.Name)) { reason = $"calls '{name}', which reads '{read.Name}', a variable of this function"; return null; }
                            AddFree(read);
                        }
                        calleeFunctions[pc] = other.Run != null ? target : null;
                        callees[pc] = Expression.Constant(values);
                    }
                    need = op.B; change = 1 - op.B; break;
                }
                default:
                    reason = $"uses {(op.Code == PgslProgram.Slow ? "an instruction run the general way" : op.Code.ToString())}";
                    return null;
            }
            if (d < need) { reason = "stack too shallow"; return null; }
            int after = d + change;
            VmValue[] nextTexts = op.Code is Opcode.PUSH or Opcode.LOAD_CONST && op.Constant.IsString ? WithText(texts, d, op.Constant) : Truncate(texts, d - need);
            maxDepth = Math.Max(maxDepth, Math.Max(after, d));
            if (falls && !Reach(pc + 1, after, nextTexts)) { reason = "stack depth differs between paths"; return null; }
            if (jump >= 0 && !Reach(jump, after, nextTexts)) { reason = "stack depth differs between paths"; return null; }
        }

        // 2. A variable of its own must be set before it is read on every path (the interpreter would
        // otherwise look for the name in its callers or the instance).
        int slots = Math.Max(1, layout.Count);
        var assignedIn = new bool[n + 1][];
        var queue = new Queue<int>();
        var entryState = new bool[slots];
        foreach (int slot in parameterSlots) entryState[slot] = true;
        assignedIn[0] = entryState; queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int pc = queue.Dequeue();
            if (pc >= n || depth[pc] < 0) continue;
            bool[] state = (bool[])assignedIn[pc].Clone();
            ref VmOp op = ref ops[pc];
            if (op.Code == Opcode.LOAD_VAR && Own((VarRef)op.Ref) && !state[((VarRef)op.Ref).Slot]) { reason = $"may read '{((VarRef)op.Ref).Name}' before setting it"; return null; }
            if (op.Code == Opcode.STORE_VAR && Own((VarRef)op.Ref)) state[((VarRef)op.Ref).Slot] = true;
            void Flow(int next)
            {
                if (next > n || next < 0) return;
                if (assignedIn[next] == null) { assignedIn[next] = state; queue.Enqueue(next); return; }
                bool changed = false;
                for (int s = 0; s < slots; s++) if (assignedIn[next][s] && !state[s]) { assignedIn[next][s] = false; changed = true; }
                if (changed) queue.Enqueue(next);
            }
            switch (op.Code)
            {
                case Opcode.JUMP: Flow(op.A); break;
                case Opcode.RETURN: break;
                case Opcode.JUMP_IF_FALSE: case Opcode.JUMP_IF_TRUE: Flow(pc + 1); Flow(op.A); break;
                default: Flow(pc + 1); break;
            }
        }

        // 3. The code: each variable and each stack depth a VmValue.
        ParameterExpression arguments = Expression.Parameter(typeof(VmValue[]), "arguments");
        ParameterExpression vm = Expression.Parameter(typeof(PgslVm), "vm");
        var locals = new ParameterExpression[slots];
        for (int s = 0; s < slots; s++) locals[s] = Expression.Variable(typeof(VmValue), "v" + s);
        var stack = new ParameterExpression[maxDepth + 1];
        for (int s = 0; s <= maxDepth; s++) stack[s] = Expression.Variable(typeof(VmValue), "s" + s);
        ParameterExpression spent = Expression.Variable(typeof(long), "spent");
        ParameterExpression check = Expression.Variable(typeof(long), "check");
        var labels = new LabelTarget[n + 1];
        for (int i = 0; i <= n; i++) labels[i] = Expression.Label("pc" + i);
        LabelTarget done = Expression.Label(typeof(VmValue), "done");
        string functionName = function.Name ?? "";
        var body = new List<Expression>();
        for (int i = 0; i < parameterSlots.Length; i++)
            body.Add(Expression.Assign(locals[parameterSlots[i]], Expression.ArrayIndex(arguments, Expression.Constant(i))));
        body.Add(Expression.Assign(check, Expression.Call(FirstCheckpointMethod, vm)));
        Expression Return(Expression value) => Expression.Block(Expression.Call(SpentMethod, vm, spent), Expression.Return(done, value));
        Expression Nothing() => Expression.Default(typeof(VmValue));

        for (int pc = 0; pc < n; pc++)
        {
            body.Add(Expression.Label(labels[pc]));
            int d = depth[pc];
            if (d < 0) continue;
            ref VmOp op = ref ops[pc];
            ParameterExpression Top(int below) => stack[d - 1 - below];
            switch (op.Code)
            {
                case Opcode.PUSH: case Opcode.LOAD_CONST:
                    body.Add(Expression.Assign(stack[d], Expression.Constant(op.Constant, typeof(VmValue)))); break;
                case Opcode.LOAD_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    body.Add(Expression.Assign(stack[d], Own(variable) ? locals[variable.Slot]
                        : Expression.Call(vm, ReadFreeValueMethod, Expression.Constant(variable))));
                    break;
                }
                case Opcode.STORE_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    if (Own(variable)) body.Add(Expression.Assign(locals[variable.Slot], Top(0)));
                    else if (variable.Kind == VarKind.Instance) body.Add(Expression.Call(vm, WriteInstanceValueMethod, Expression.Constant(variable.Name), Top(0)));
                    else body.Add(Expression.Call(vm, WriteRegisterValueMethod, Expression.Constant(variable.RegisterSlot), Top(0)));
                    break;
                }
                case Opcode.LOAD_REG: body.Add(Expression.Assign(stack[d], Expression.Call(vm, ReadRegisterValueMethod, Expression.Constant(op.A)))); break;
                case Opcode.PUSH_NULL: body.Add(Expression.Assign(stack[d], Nothing())); break;
                case Opcode.STORE_REG: body.Add(Expression.Call(vm, WriteRegisterValueMethod, Expression.Constant(op.A), Top(0))); break;
                case Opcode.POP: break;
                case Opcode.DUP: body.Add(Expression.Assign(stack[d], Top(0))); break;
                case Opcode.SWAP:
                    body.Add(Expression.Assign(stack[d], Top(0)));
                    body.Add(Expression.Assign(Top(0), Top(1)));
                    body.Add(Expression.Assign(Top(1), stack[d])); break;
                case Opcode.NEG: case Opcode.NOT: case Opcode.BIT_NOT:
                    body.Add(Expression.Assign(Top(0), Expression.Call(UnaryOps[op.Code], Top(0)))); break;
                case Opcode.JUMP:
                    body.Add(Charge(pc, op.A));
                    body.Add(Expression.Goto(labels[op.A])); break;
                case Opcode.JUMP_IF_FALSE:
                case Opcode.JUMP_IF_TRUE:
                {
                    Expression truth = Expression.Call(TruthMethod, Top(0));
                    Expression test = op.Code == Opcode.JUMP_IF_FALSE ? Expression.Not(truth) : truth;
                    body.Add(Expression.IfThen(test, Expression.Block(Charge(pc, op.A), Expression.Goto(labels[op.A]))));
                    break;
                }
                case Opcode.RETURN:
                    body.Add(Return(d > 0 ? Top(0) : Nothing())); break;
                case Opcode.CALL_NATIVE:
                case Opcode.CALL:
                {
                    int count = op.B;
                    ParameterExpression args = Expression.Variable(typeof(VmValue[]), "a");
                    var call = new List<Expression> { Expression.Assign(args, Expression.Call(ValueScratchMethod, Expression.Constant(count))) };
                    for (int i = 0; i < count; i++)
                        call.Add(Expression.Assign(Expression.ArrayAccess(args, Expression.Constant(i)), stack[d - count + i]));
                    Expression result;
                    if (op.Code == Opcode.CALL_NATIVE)
                        result = Expression.Call(vm, CallNativeValuesMethod, Expression.Constant(op.A), Expression.Constant(count), args);
                    else if (calleeFunctions[pc] is UserFunction numbers)
                        result = Expression.Call(CallNumbersOrValuesMethod, Expression.Constant(numbers), callees[pc], args, Expression.Constant(count), vm);
                    else result = Expression.Invoke(callees[pc], args, vm);
                    call.Add(Expression.Assign(stack[d - count], result));
                    body.Add(Expression.Block([args], call));
                    break;
                }
                default:
                    if (BinaryOps.TryGetValue(op.Code, out MethodInfo binary))
                        body.Add(Expression.Assign(Top(1), Expression.Call(binary, Top(1), Top(0))));
                    break;
            }
        }
        body.Add(Expression.Label(labels[n]));
        body.Add(Return(Nothing()));
        body.Add(Expression.Label(done, Nothing()));

        var variables = new List<ParameterExpression>(locals) { spent, check };
        variables.AddRange(stack);
        Expression guarded = Expression.Block(typeof(VmValue),
            Expression.Call(EnterMethod, Expression.Constant(functionName)),
            Expression.TryFinally(Expression.Block(typeof(VmValue), variables, body), Expression.Call(ExitMethod)));
        freeReads = [.. free];
        return Expression.Lambda<CompiledValues>(guarded, functionName, [arguments, vm]).Compile();

        // A jump back counts the instructions of the loop it closes, as the interpreter would.
        Expression Charge(int from, int to)
        {
            if (to > from) return Expression.Empty();
            return Expression.IfThen(
                Expression.GreaterThan(Expression.AddAssign(spent, Expression.Constant((long)(from - to + 1))), check),
                Expression.Assign(check, Expression.Call(CheckpointMethod, vm, spent, Expression.Constant(functionName))));
        }
    }

    /// <summary>
    /// Whether a function with values may call a command: one the numbers-only tier calls, or one of
    /// the categories that cannot run a script. A variable command must name its variable with text
    /// written in the code, and not one of the function's own (it has no frame for the command to see).
    /// </summary>
    private static bool CalledWithValues(PgslEngineBridge bridge, PgslEngineBridge.NativeCall call, int id, int count,
        VmValue[] texts, int depth, HashSet<string> ownNames, out string reason)
    {
        reason = null;
        string name = bridge.NativeName(id);
        string category = bridge.NativeCategory(id);
        bool listed = name != null && ValuesMayCall.Contains(name)
            || (category != null && InterpretersWayCategories.Contains(category) && (name == null || !MayRunEvents.Contains(name)));
        if (call == null || (!listed && !NumbersOnly(call))) { reason = $"calls {name}, which may run a script"; return false; }
        if (category == null || !VariableCategories.Contains(category)) return true;
        for (int i = 0; i < count; i++)
        {
            if (call.Kinds == null || i >= call.Kinds.Length || call.Kinds[i] != PgslEngineBridge.ArgumentKind.Text) continue;
            int at = depth - count + i;
            if (texts == null || at < 0 || at >= texts.Length || !texts[at].IsString) { reason = $"names a variable to {name} with text it works out"; return false; }
            string named = texts[at].ToObject() as string ?? "";
            foreach (string own in ownNames)
                if (string.Equals(own, named, StringComparison.OrdinalIgnoreCase)) { reason = $"names its own variable '{named}' to {name}"; return false; }
        }
        return true;
    }
}
