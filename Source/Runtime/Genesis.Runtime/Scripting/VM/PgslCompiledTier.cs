#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>
/// The VM's second tier: a script function called often whose work is numbers only (its own
/// variables and parameters, arithmetic, comparisons, jumps, typed commands such as DsGridGet or
/// noise, and calls to other such functions) is compiled to a .NET delegate, so it runs as machine
/// code rather than one interpreted instruction at a time. What it computes is exactly what the
/// interpreter computes; anything else (text, instance variables, lists of values, a debugger
/// attached) stays interpreted. The per-call instruction limit and a job's budget still apply:
/// instructions are counted at every jump back, as the interpreter counts them in a loop.
/// GENESIS_PGSL_TIER2=0 turns it off.
/// </summary>
internal static class PgslCompiledTier
{
    public delegate double Compiled(double[] arguments, PgslVm vm);

    /// <summary>Calls before a function is compiled.</summary>
    internal const int HotCalls = 40;

    internal static bool Enabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("GENESIS_PGSL_TIER2"), "0", StringComparison.Ordinal);

    internal sealed class Entry
    {
        public Compiled Run;
        public int Parameters;
        /// <summary>Why it stays interpreted (for checks and the profile).</summary>
        public string Reason;
        /// <summary>
        /// Names it (or a function it calls) reads but does not hold: each must be a number when a
        /// call starts, or that call is interpreted. Within a compiled call they change only through
        /// compiled code, which writes numbers.
        /// </summary>
        public VarRef[] FreeReads = [];
    }

    // Per bridge: a worker job's bridge refuses its own commands, so its functions compile against it.
    private static readonly ConditionalWeakTable<PgslEngineBridge, ConcurrentDictionary<UserFunction, Entry>> Cache = new();

    /// <summary>Set by a compiled function that returned no value (the interpreter's null).</summary>
    [ThreadStatic] internal static bool NoValue;
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static double[][] _scratch;

    internal static long CompiledCount;

    /// <summary>The compiled form of a function for this bridge (made on the first request), or one saying why not.</summary>
    internal static Entry Get(PgslEngineBridge bridge, UserFunction function, Func<string, UserFunction> resolve)
    {
        ConcurrentDictionary<UserFunction, Entry> table = Cache.GetOrCreateValue(bridge);
        if (table.TryGetValue(function, out Entry entry)) return entry;
        var building = new Dictionary<UserFunction, Holder>();
        entry = Build(bridge, function, resolve, building, table);
        return entry;
    }

    private sealed class Holder { public Compiled Run; }

    /// <summary>A function's entry for one bridge, kept on the function (one reference, so no thread sees half of it).</summary>
    internal sealed class Cached(PgslEngineBridge bridge, Entry entry)
    {
        public readonly PgslEngineBridge Bridge = bridge;
        public readonly Entry Entry = entry;
    }

    // Why each function looked at stays interpreted, by name (null once compiled), for the profile report.
    private static readonly ConcurrentDictionary<string, string> Reasons = new(StringComparer.Ordinal);

    /// <summary>Why a function stays interpreted; empty when compiled, null when never looked at (called fewer than <see cref="HotCalls"/> times).</summary>
    internal static string ReasonFor(string function) =>
        function != null && Reasons.TryGetValue(function, out string reason) ? reason ?? string.Empty : null;

    private static Entry Build(PgslEngineBridge bridge, UserFunction function, Func<string, UserFunction> resolve,
        Dictionary<UserFunction, Holder> building, ConcurrentDictionary<UserFunction, Entry> table)
    {
        if (table.TryGetValue(function, out Entry known)) return known;
        var holder = new Holder();
        building[function] = holder;
        Entry entry = new() { Parameters = function.Parameters?.Count ?? 0 };
        try
        {
            entry.Run = Compile(bridge, function, resolve, building, table, out string reason, out VarRef[] freeReads);
            entry.Reason = reason;
            entry.FreeReads = freeReads ?? [];
        }
        catch (Exception error)
        {
            entry.Run = null;
            entry.Reason = "could not compile: " + error.Message;
        }
        holder.Run = entry.Run;
        building.Remove(function);
        table[function] = entry;
        if (function.Name is { } named && Reasons.Count < 8192) Reasons[named] = entry.Run != null ? null : entry.Reason;
        if (entry.Run != null) System.Threading.Interlocked.Increment(ref CompiledCount);
        return entry;
    }

    // ── Run-time helpers the compiled code calls ────────────────────────────────

    public static double[] Scratch(int count)
    {
        _scratch ??= new double[33][];
        if (count >= _scratch.Length) return new double[count];
        return _scratch[count] ??= new double[count];
    }

    public static void Enter(string name)
    {
        if (++_depth > PgslVm.MaxCallDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            _depth--;
            throw new InvalidOperationException($"Function '{name}' called itself too deeply (more than {PgslVm.MaxCallDepth} calls).");
        }
    }

    public static void Exit() => _depth--;

    public static long FirstCheckpoint(PgslVm vm) => vm.JobBudget is null ? PgslVm.CallInstructionLimit : PgslJobBudget.Slice;

    /// <summary>A loop passed its checkpoint: past the per-call limit it stops, as the interpreter does; a job checks its budget.</summary>
    public static long Checkpoint(PgslVm vm, long spent, string name)
    {
        if (vm.JobBudget is null)
        {
            int limit = PgslVm.CallInstructionLimit;
            if (spent > limit) vm.ThrowInstructionLimitFor(name);
            return limit;
        }
        vm.JobBudget.Check(vm.InstructionsExecuted + spent);
        return spent + PgslJobBudget.Slice;
    }

    public static void Spent(PgslVm vm, long spent) => vm.AddInstructions(spent);

    public static double FloorMod(double a, double b) => Math.Abs(b) < 1e-12 ? 0 : a - (Math.Floor(a / b) * b);

    public static double Bool(bool value) => value ? 1 : 0;

    // ── The compiler ────────────────────────────────────────────────────────────

    private static readonly MethodInfo ScratchMethod = typeof(PgslCompiledTier).GetMethod(nameof(Scratch));
    private static readonly MethodInfo EnterMethod = typeof(PgslCompiledTier).GetMethod(nameof(Enter));
    private static readonly MethodInfo ExitMethod = typeof(PgslCompiledTier).GetMethod(nameof(Exit));
    private static readonly MethodInfo FirstCheckpointMethod = typeof(PgslCompiledTier).GetMethod(nameof(FirstCheckpoint));
    private static readonly MethodInfo CheckpointMethod = typeof(PgslCompiledTier).GetMethod(nameof(Checkpoint));
    private static readonly MethodInfo SpentMethod = typeof(PgslCompiledTier).GetMethod(nameof(Spent));
    private static readonly MethodInfo FloorModMethod = typeof(PgslCompiledTier).GetMethod(nameof(FloorMod));
    private static readonly MethodInfo FloorMethod = typeof(Math).GetMethod(nameof(Math.Floor), [typeof(double)]);
    private static readonly MethodInfo AbsMethod = typeof(Math).GetMethod(nameof(Math.Abs), [typeof(double)]);
    private static readonly FieldInfo NoValueField = typeof(PgslCompiledTier).GetField(nameof(NoValue), BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo ReadFreeMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.ReadFreeNumber), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriteInstanceMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.WriteInstanceNumber), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ReadRegisterMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.ReadRegisterNumber), BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo WriteRegisterMethod = typeof(PgslVm).GetMethod(nameof(PgslVm.WriteRegisterNumber), BindingFlags.Instance | BindingFlags.NonPublic);

    private static Compiled Compile(PgslEngineBridge bridge, UserFunction function, Func<string, UserFunction> resolve,
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

        // 0. Which names are the function's own. The interpreter keeps a parameter, a `var` and a plain
        // name the function assigns (not a register or a property) in the call's frame; every other
        // name it reads is looked for in its callers' frames, the instance, then a property or register.
        var assigned = new bool[Math.Max(1, layout.Count)];
        foreach (VmOp each in ops)
            if (each.Code == Opcode.STORE_VAR && each.Ref is VarRef stored && stored.Slot >= 0 && stored.Kind != VarKind.Instance && stored.RegisterSlot < 0)
                assigned[stored.Slot] = true;
        bool Own(VarRef v) => v.Slot >= 0 && v.Kind != VarKind.Instance
            && (v.Kind == VarKind.FunctionLocal || isParameter[v.Slot] || (assigned[v.Slot] && v.RegisterSlot < 0 && !bridge.IsDeclaredProperty(v.Name)));
        // The names a function it calls must not read: a callee sees its caller's frame.
        var ownNames = new HashSet<string>(StringComparer.Ordinal);
        if (function.Parameters != null) foreach (string parameter in function.Parameters) if (parameter != null) ownNames.Add(parameter);
        foreach (VmOp each in ops)
            if (each.Code is Opcode.LOAD_VAR or Opcode.STORE_VAR && each.Ref is VarRef named && Own(named)) ownNames.Add(named.Name);
        var free = new List<VarRef>();
        var freeKeys = new HashSet<(VarKind, string)>();
        void AddFree(VarRef v) { if (freeKeys.Add((v.Kind == VarKind.Instance ? VarKind.Instance : VarKind.Plain, v.Name))) free.Add(v); }

        // 1. Every instruction must be one the tier knows, with the stack as deep on every path to it.
        int n = ops.Length;
        int[] depth = new int[n + 1];
        Array.Fill(depth, -1);
        var natives = new PgslEngineBridge.NativeCall[n];
        var callees = new Holder[n];
        var calleeParameters = new int[n];
        var work = new Stack<int>();
        int maxDepth = 0;
        bool Reach(int pc, int d)
        {
            if (pc < 0 || pc > n) return false;
            if (depth[pc] == -1) { depth[pc] = d; work.Push(pc); return true; }
            return depth[pc] == d;
        }
        if (!Reach(0, 0)) { reason = "empty"; return null; }
        while (work.Count > 0)
        {
            int pc = work.Pop();
            if (pc == n) continue;
            int d = depth[pc];
            ref VmOp op = ref ops[pc];
            int need = 0, change = 0;
            int jump = -1;
            bool falls = true;
            switch (op.Code)
            {
                case Opcode.PUSH: case Opcode.LOAD_CONST:
                    if (!op.Constant.IsUnboxedNumber) { reason = "a constant that is not a number"; return null; }
                    change = 1; break;
                case Opcode.LOAD_VAR:
                {
                    // Its own, or a name read from outside as the interpreter would (checked at each call's start).
                    VarRef variable = (VarRef)op.Ref;
                    if (!Own(variable)) AddFree(variable);
                    change = 1; break;
                }
                case Opcode.STORE_VAR:
                {
                    // Its own, an instance variable or a register; a property or a name with no slot stays interpreted.
                    VarRef variable = (VarRef)op.Ref;
                    if (!Own(variable) && variable.Kind != VarKind.Instance && variable.RegisterSlot < 0)
                    { reason = $"writes '{variable.Name}', not its own variable"; return null; }
                    need = 1; change = -1; break;
                }
                case Opcode.LOAD_REG:
                    if (op.A == PgslRegisterFile.SlotSpriteIndex) { reason = "reads sprite_index (text)"; return null; }
                    change = 1; break;
                // `return;` (and a function's end): nothing comes back, as the interpreter's null.
                case Opcode.PUSH_NULL:
                    if (pc + 1 >= n || ops[pc + 1].Code != Opcode.RETURN) { reason = "uses a null value"; return null; }
                    change = 1; break;
                case Opcode.STORE_REG: need = 1; change = -1; break;
                case Opcode.POP: need = 1; change = -1; break;
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
                    if (!NumbersOnly(call)) { reason = $"calls {bridge.NativeName(op.A)}, which takes more than numbers"; return null; }
                    natives[pc] = call;
                    need = op.B; change = 1 - op.B; break;
                }
                case Opcode.CALL:
                {
                    string name = op.Ref as string;
                    UserFunction target = name == null ? null : resolve(name);
                    if (target == null || (target.Parameters?.Count ?? -1) != op.B) { reason = $"calls '{name}', which is not a script function taking {op.B}"; return null; }
                    if (!building.TryGetValue(target, out Holder holder))
                    {
                        Entry other = Build(bridge, target, resolve, building, table);
                        if (other.Run == null) { reason = $"calls '{name}', which stays interpreted ({other.Reason})"; return null; }
                        // The callee would see this function's own names through its frame: refused where it reads one.
                        foreach (VarRef read in other.FreeReads)
                        {
                            if (read.Kind != VarKind.Instance && ownNames.Contains(read.Name)) { reason = $"calls '{name}', which reads '{read.Name}', a variable of this function"; return null; }
                            AddFree(read);
                        }
                        holder = new Holder { Run = other.Run };
                    }
                    else if (!ReferenceEquals(target, function)) { reason = $"calls '{name}', which calls it back"; return null; }
                    callees[pc] = holder;
                    calleeParameters[pc] = op.B;
                    need = op.B; change = 1 - op.B; break;
                }
                default:
                    reason = $"uses {(op.Code == PgslProgram.Slow ? "an instruction run the general way" : op.Code.ToString())}";
                    return null;
            }
            if (d < need) { reason = "stack too shallow"; return null; }
            int after = d + change;
            maxDepth = Math.Max(maxDepth, Math.Max(after, d));
            if (falls && !Reach(pc + 1, after)) { reason = "stack depth differs between paths"; return null; }
            if (jump >= 0 && !Reach(jump, after)) { reason = "stack depth differs between paths"; return null; }
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

        // 3. The code: each variable and each stack depth is a double.
        ParameterExpression arguments = Expression.Parameter(typeof(double[]), "arguments");
        ParameterExpression vm = Expression.Parameter(typeof(PgslVm), "vm");
        var locals = new ParameterExpression[slots];
        for (int s = 0; s < slots; s++) locals[s] = Expression.Variable(typeof(double), "v" + s);
        var stack = new ParameterExpression[maxDepth + 1];
        for (int s = 0; s <= maxDepth; s++) stack[s] = Expression.Variable(typeof(double), "s" + s);
        ParameterExpression spent = Expression.Variable(typeof(long), "spent");
        ParameterExpression check = Expression.Variable(typeof(long), "check");
        var labels = new LabelTarget[n + 1];
        for (int i = 0; i <= n; i++) labels[i] = Expression.Label("pc" + i);
        LabelTarget done = Expression.Label(typeof(double), "done");
        string functionName = function.Name ?? "";
        var body = new List<Expression>();
        for (int i = 0; i < parameterSlots.Length; i++)
            body.Add(Expression.Assign(locals[parameterSlots[i]], Expression.ArrayIndex(arguments, Expression.Constant(i))));
        body.Add(Expression.Assign(check, Expression.Call(FirstCheckpointMethod, vm)));
        Expression Num(Expression truth) => Expression.Condition(truth, Expression.Constant(1.0), Expression.Constant(0.0));
        Expression Int(Expression value) => Expression.Convert(value, typeof(int));
        Expression Return(Expression value, bool noValue) => Expression.Block(
            Expression.Assign(Expression.Field(null, NoValueField), Expression.Constant(noValue)),
            Expression.Call(SpentMethod, vm, spent),
            Expression.Return(done, value));

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
                    body.Add(Expression.Assign(stack[d], Expression.Constant(op.Constant.UnboxedNumber))); break;
                case Opcode.LOAD_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    body.Add(Expression.Assign(stack[d], Own(variable) ? locals[variable.Slot]
                        : Expression.Call(vm, ReadFreeMethod, Expression.Constant(variable))));
                    break;
                }
                case Opcode.STORE_VAR:
                {
                    VarRef variable = (VarRef)op.Ref;
                    if (Own(variable)) body.Add(Expression.Assign(locals[variable.Slot], Top(0)));
                    else if (variable.Kind == VarKind.Instance) body.Add(Expression.Call(vm, WriteInstanceMethod, Expression.Constant(variable.Name), Top(0)));
                    else body.Add(Expression.Call(vm, WriteRegisterMethod, Expression.Constant(variable.RegisterSlot), Top(0)));
                    break;
                }
                case Opcode.LOAD_REG: body.Add(Expression.Assign(stack[d], Expression.Call(vm, ReadRegisterMethod, Expression.Constant(op.A)))); break;
                case Opcode.PUSH_NULL: body.Add(Return(Expression.Constant(0.0), true)); break;
                case Opcode.STORE_REG: body.Add(Expression.Call(vm, WriteRegisterMethod, Expression.Constant(op.A), Top(0))); break;
                case Opcode.POP: break;
                case Opcode.DUP: body.Add(Expression.Assign(stack[d], Top(0))); break;
                case Opcode.SWAP:
                    body.Add(Expression.Assign(stack[d], Top(0)));
                    body.Add(Expression.Assign(Top(0), Top(1)));
                    body.Add(Expression.Assign(Top(1), stack[d])); break;
                case Opcode.ADD: body.Add(Expression.Assign(Top(1), Expression.Add(Top(1), Top(0)))); break;
                case Opcode.SUB: body.Add(Expression.Assign(Top(1), Expression.Subtract(Top(1), Top(0)))); break;
                case Opcode.MUL: body.Add(Expression.Assign(Top(1), Expression.Multiply(Top(1), Top(0)))); break;
                case Opcode.DIV: body.Add(Expression.Assign(Top(1), Expression.Divide(Top(1), Top(0)))); break;
                case Opcode.MOD: body.Add(Expression.Assign(Top(1), Expression.Call(FloorModMethod, Top(1), Top(0)))); break;
                case Opcode.LT: body.Add(Expression.Assign(Top(1), Num(Expression.LessThan(Top(1), Top(0))))); break;
                case Opcode.LTE: body.Add(Expression.Assign(Top(1), Num(Expression.LessThanOrEqual(Top(1), Top(0))))); break;
                case Opcode.GT: body.Add(Expression.Assign(Top(1), Num(Expression.GreaterThan(Top(1), Top(0))))); break;
                case Opcode.GTE: body.Add(Expression.Assign(Top(1), Num(Expression.GreaterThanOrEqual(Top(1), Top(0))))); break;
                case Opcode.EQ: body.Add(Expression.Assign(Top(1), Num(Expression.LessThan(Expression.Call(AbsMethod, Expression.Subtract(Top(1), Top(0))), Expression.Constant(1e-9))))); break;
                case Opcode.NEQ: body.Add(Expression.Assign(Top(1), Num(Expression.Not(Expression.LessThan(Expression.Call(AbsMethod, Expression.Subtract(Top(1), Top(0))), Expression.Constant(1e-9)))))); break;
                case Opcode.AND: body.Add(Expression.Assign(Top(1), Num(Expression.AndAlso(Expression.NotEqual(Top(1), Expression.Constant(0.0)), Expression.NotEqual(Top(0), Expression.Constant(0.0)))))); break;
                case Opcode.OR: body.Add(Expression.Assign(Top(1), Num(Expression.OrElse(Expression.NotEqual(Top(1), Expression.Constant(0.0)), Expression.NotEqual(Top(0), Expression.Constant(0.0)))))); break;
                case Opcode.BIT_AND: body.Add(Expression.Assign(Top(1), Expression.Convert(Expression.And(Int(Top(1)), Int(Top(0))), typeof(double)))); break;
                case Opcode.BIT_OR: body.Add(Expression.Assign(Top(1), Expression.Convert(Expression.Or(Int(Top(1)), Int(Top(0))), typeof(double)))); break;
                case Opcode.BIT_XOR: body.Add(Expression.Assign(Top(1), Expression.Convert(Expression.ExclusiveOr(Int(Top(1)), Int(Top(0))), typeof(double)))); break;
                case Opcode.SHL: body.Add(Expression.Assign(Top(1), Expression.Convert(Expression.LeftShift(Int(Top(1)), Int(Top(0))), typeof(double)))); break;
                case Opcode.SHR: body.Add(Expression.Assign(Top(1), Expression.Convert(Expression.RightShift(Int(Top(1)), Int(Top(0))), typeof(double)))); break;
                case Opcode.NEG: body.Add(Expression.Assign(Top(0), Expression.Negate(Top(0)))); break;
                case Opcode.NOT: body.Add(Expression.Assign(Top(0), Num(Expression.Equal(Top(0), Expression.Constant(0.0))))); break;
                case Opcode.BIT_NOT: body.Add(Expression.Assign(Top(0), Expression.Convert(Expression.Not(Int(Top(0))), typeof(double)))); break;
                case Opcode.JUMP:
                    body.Add(Charge(pc, op.A));
                    body.Add(Expression.Goto(labels[op.A])); break;
                case Opcode.JUMP_IF_FALSE:
                case Opcode.JUMP_IF_TRUE:
                {
                    Expression test = op.Code == Opcode.JUMP_IF_FALSE ? Expression.Equal(Top(0), Expression.Constant(0.0)) : Expression.NotEqual(Top(0), Expression.Constant(0.0));
                    body.Add(Expression.IfThen(test, Expression.Block(Charge(pc, op.A), Expression.Goto(labels[op.A]))));
                    break;
                }
                case Opcode.RETURN:
                    body.Add(d > 0 ? Return(Top(0), false) : Return(Expression.Constant(0.0), true)); break;
                case Opcode.CALL_NATIVE: body.Add(Expression.Assign(stack[d - op.B], NativeCallExpression(natives[pc], stack, d, op.B))); break;
                case Opcode.CALL:
                {
                    int count = calleeParameters[pc];
                    ParameterExpression args = Expression.Variable(typeof(double[]), "a");
                    var call = new List<Expression> { Expression.Assign(args, Expression.Call(ScratchMethod, Expression.Constant(count))) };
                    for (int i = 0; i < count; i++)
                        call.Add(Expression.Assign(Expression.ArrayAccess(args, Expression.Constant(i)), stack[d - count + i]));
                    // The callee is read from its holder when called: it may be this function, still being made.
                    Holder holder = callees[pc];
                    Expression run = Expression.Field(Expression.Constant(holder), nameof(Holder.Run));
                    call.Add(Expression.Assign(stack[d - count], Expression.Invoke(run, args, vm)));
                    body.Add(Expression.Block([args], call));
                    break;
                }
            }
        }
        body.Add(Expression.Label(labels[n]));
        body.Add(Return(Expression.Constant(0.0), true));
        body.Add(Expression.Label(done, Expression.Constant(0.0)));

        var variables = new List<ParameterExpression>(locals) { spent, check };
        variables.AddRange(stack);
        Expression guarded = Expression.Block(typeof(double),
            Expression.Call(EnterMethod, Expression.Constant(functionName)),
            Expression.TryFinally(Expression.Block(typeof(double), variables, body), Expression.Call(ExitMethod)));
        freeReads = [.. free];
        return Expression.Lambda<Compiled>(guarded, functionName, [arguments, vm]).Compile();

        // A jump back counts the instructions of the loop it closes, as the interpreter would.
        Expression Charge(int from, int to)
        {
            if (to > from) return Expression.Empty();
            return Expression.IfThen(
                Expression.GreaterThan(Expression.AddAssign(spent, Expression.Constant((long)(from - to + 1))), check),
                Expression.Assign(check, Expression.Call(CheckpointMethod, vm, spent, Expression.Constant(functionName))));
        }
    }

    // A command whose arguments are numbers (doubles, floats, whole numbers, true/false) and whose
    // result is a number, true/false or nothing.
    private static bool NumbersOnly(PgslEngineBridge.NativeCall call)
    {
        if (call == null || call.IsEngine) return false;
        if (call.Shape > PgslEngineBridge.DirectShape.None) return true;
        if (call.Kinds == null || call.HasText) return false;
        foreach (PgslEngineBridge.ArgumentKind kind in call.Kinds)
            if (kind is not (PgslEngineBridge.ArgumentKind.Number or PgslEngineBridge.ArgumentKind.Single or PgslEngineBridge.ArgumentKind.Integer or PgslEngineBridge.ArgumentKind.Truth))
                return false;
        return call.NumberCall != null || call.BoolCall != null || call.VoidCall != null;
    }

    private static Expression NativeCallExpression(PgslEngineBridge.NativeCall call, ParameterExpression[] stack, int depth, int count)
    {
        Expression Arg(int i) => stack[depth - count + i];
        if (call.Shape > PgslEngineBridge.DirectShape.None)
        {
            Delegate direct = call.Direct;
            MethodInfo invoke = direct.GetType().GetMethod("Invoke");
            var args = new Expression[count];
            for (int i = 0; i < count; i++) args[i] = Arg(i);
            Expression result = Expression.Call(Expression.Constant(direct), invoke, args);
            // A command that leaves nothing leaves the interpreter's null, which reads as 0.
            return invoke.ReturnType == typeof(void) || call.PushesNothing
                ? Expression.Block(result, Expression.Constant(0.0))
                : result;
        }
        ParameterExpression numbers = Expression.Variable(typeof(double[]), "numbers");
        var block = new List<Expression> { Expression.Assign(numbers, Expression.Call(ScratchMethod, Expression.Constant(count))) };
        for (int i = 0; i < count; i++) block.Add(Expression.Assign(Expression.ArrayAccess(numbers, Expression.Constant(i)), Arg(i)));
        Expression none = Expression.Constant(null, typeof(object[]));
        if (call.NumberCall != null)
            block.Add(call.PushesNothing
                ? Expression.Block(Expression.Invoke(Expression.Constant(call.NumberCall), numbers, none), Expression.Constant(0.0))
                : Expression.Invoke(Expression.Constant(call.NumberCall), numbers, none));
        else if (call.BoolCall != null)
            block.Add(call.PushesNothing
                ? Expression.Block(Expression.Invoke(Expression.Constant(call.BoolCall), numbers, none), Expression.Constant(0.0))
                : Expression.Condition(Expression.Invoke(Expression.Constant(call.BoolCall), numbers, none), Expression.Constant(1.0), Expression.Constant(0.0)));
        else
            block.Add(Expression.Block(Expression.Invoke(Expression.Constant(call.VoidCall), numbers, none), Expression.Constant(0.0)));
        return Expression.Block(typeof(double), [numbers], block);
    }
}
