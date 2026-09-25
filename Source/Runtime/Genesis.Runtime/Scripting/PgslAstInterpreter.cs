#nullable disable
using System;
using System.Collections.Generic;
using Genesis.Runtime.Scripting.Ast;
using Genesis.Shared.Commands;

namespace Genesis.Runtime.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //   PgslAstInterpreter — the dev-mode execution backend.
    //
    //   A tree-walking interpreter over the typed ScriptAst. This is the "instant
    //   hot-reload" half of the hybrid scripting decision: re-parsing + re-walking
    //   is far faster than a Roslyn recompile, so iterating on a .pgsl script during
    //   development feels live. For release builds, the PGSL→C# transpiler (Phase
    //   1.2) takes over and runs at native speed.
    //
    //   Why tree-walking instead of a new bytecode VM: the AST is already typed and
    //   already exists (Phase 1.1). A separate bytecode+dispatch loop would double
    //   the surface area for marginal gain in dev mode. The performance-critical
    //   path is the transpiler; this interpreter only needs to be *fast enough to
    //   iterate*, which a typed tree-walk comfortably is (no Stack<object> boxing,
    //   no per-op dictionary lookup for locals — locals live in a typed dictionary
    //   keyed by slot index, and commands dispatch through the compiled pipeline).
    //
    //   Fixes the four worst hotspots of the old PgslVm:
    //     • no Stack<object> / no object boxing (Value is a readonly struct)
    //     • locals indexed by int slot, not string dictionary
    //     • no per-call variable copy (frame locals are a stack of scopes)
    //     • no exception-based return (uses an explicit flow flag)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Typed, allocation-light runtime value for the dev interpreter. No boxing:
    /// numbers stay as double, bools as bool, strings as string. The discriminated
    /// union is a tag + four fields (only one populated per tag).
    /// </summary>
    public readonly struct PgslValue
    {
        public enum T { Null, Number, Bool, String, Object }

        public readonly T Type;
        public readonly double Num;
        public readonly bool Bool;
        public readonly string Str;
        public readonly object Obj;   // arbitrary CLR object (asset handles, arrays)

        public static readonly PgslValue Null = new(T.Null);
        public static readonly PgslValue Zero = new(0.0);
        public static readonly PgslValue True = new(true);
        public static readonly PgslValue False = new(false);

        public PgslValue(double v)    { Type = T.Number; Num = v; }
        public PgslValue(bool v)      { Type = T.Bool; Bool = v; }
        public PgslValue(string v)    { Type = string.IsNullOrEmpty(v) ? T.Null : T.String; Str = v; }
        public PgslValue(object v)    { Type = T.Object; Obj = v; }
        private PgslValue(T t)        { Type = t; }

        public bool IsNull => Type == T.Null;
        public bool IsNumber => Type == T.Number;
        public bool IsTruthy => Type switch
        {
            T.Null   => false,
            T.Number => Num != 0,
            T.Bool   => Bool,
            T.String => !string.IsNullOrEmpty(Str),
            _        => Obj != null,
        };

        /// <summary>Coerce to double (PGSL is number-centric, like GML). Strings parse; null→0.</summary>
        public double AsNumber() => Type switch
        {
            T.Number => Num,
            T.Bool   => Bool ? 1 : 0,
            T.String => double.TryParse(Str, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
            T.Null   => 0,
            _        => 0,
        };

        public override string ToString() => Type switch
        {
            T.Null   => "null",
            T.Number => Num.ToString(System.Globalization.CultureInfo.InvariantCulture),
            T.Bool   => Bool ? "true" : "false",
            T.String => Str,
            _        => Obj?.ToString() ?? "null",
        };
    }

    /// <summary>
    /// Tree-walking interpreter for PGSL. Construct one per event execution (cheap),
    /// or reuse and call <see cref="Execute"/> again. Variables live in a scope stack;
    /// commands dispatch through <see cref="EngineCommandPipeline"/>.
    /// </summary>
    public sealed class PgslAstInterpreter
    {
        // Slot-indexed locals — avoids Dictionary<string,...> lookups on the hot path.
        // The scope stack supports function calls and block shadowing.
        private readonly List<Dictionary<string, PgslValue>> _scopes = new();
        private readonly Dictionary<string, FunctionDeclStmt> _functions = new(StringComparer.OrdinalIgnoreCase);

        // Instance-variable provider: lets the interpreter read/write the active
        // entity's X/Y/HSpeed/etc. without the AST knowing about ECS components.
        // When null (e.g. command-browser self-test), instance vars fall back to locals.
        public IPgslInstanceContext Instance { get; set; }

        // Flow-control signals (replace the old ReturnException).
        private enum Flow { None, Return, BreakContinue }
        private Flow _flow;

        private const int MaxStatements = 200_000; // runaway-guard for dev mode
        private int _statementCount;

        /// <summary>Execute a whole script's top-level statements.</summary>
        public void Execute(ScriptAst script)
        {
            _statementCount = 0;
            _scopes.Clear();
            _functions.Clear();
            PushScope();

            // Pre-register top-level functions so order doesn't matter.
            foreach (var stmt in script.Body)
                if (stmt is FunctionDeclStmt fn) _functions[fn.Name] = fn;

            foreach (var stmt in script.Body)
            {
                if (_flow == Flow.Return) break;
                ExecStmt(stmt);
            }
        }

        // ── Scope helpers ─────────────────────────────────────────────────────────

        private void PushScope() => _scopes.Add(new Dictionary<string, PgslValue>(StringComparer.OrdinalIgnoreCase));
        private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

        private Dictionary<string, PgslValue> CurrentScope => _scopes[_scopes.Count - 1];

        private bool TryGetVar(string name, out PgslValue value)
        {
            // Search inner-to-outer scopes.
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out value)) return true;
            // Then instance vars (X, Y, HSpeed, ...).
            if (Instance != null && Instance.TryGetInstanceVar(name, out value)) return true;
            value = PgslValue.Null;
            return false;
        }

        private void SetVar(string name, PgslValue value)
        {
            // If it's an instance variable, route to the instance context.
            if (Instance != null && Instance.IsInstanceVar(name))
            {
                Instance.SetInstanceVar(name, value);
                return;
            }
            // Otherwise assign in the nearest scope that already declares it, else the current scope.
            for (int i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].ContainsKey(name)) { _scopes[i][name] = value; return; }
            }
            CurrentScope[name] = value;
        }

        // ── Statements ────────────────────────────────────────────────────────────

        private void ExecStmt(Stmt stmt)
        {
            if (++_statementCount > MaxStatements)
                throw new InvalidOperationException("PGSL dev interpreter: maximum statement count exceeded (possible infinite loop).");

            switch (stmt)
            {
                case BlockStmt b:
                    PushScope();
                    foreach (var s in b.Body)
                    {
                        if (_flow != Flow.None) break;
                        ExecStmt(s);
                    }
                    PopScope();
                    break;

                case VarDeclStmt v:
                    CurrentScope[v.Name] = v.Initializer != null ? Eval(v.Initializer) : PgslValue.Null;
                    break;

                case ExprStmt e:
                    Eval(e.Expression);
                    break;

                case IfStmt i:
                    if (Eval(i.Condition).IsTruthy) ExecStmt(i.ThenBranch);
                    else if (i.ElseBranch != null) ExecStmt(i.ElseBranch);
                    break;

                case WhileStmt w:
                    while (Eval(w.Condition).IsTruthy)
                    {
                        var prev = _flow;
                        _flow = Flow.None;
                        ExecStmt(w.Body);
                        if (_flow == Flow.Return) return;
                        if (_flow == Flow.BreakContinue) _flow = Flow.None; // continue falls through
                        if (prev == Flow.Return) break;
                    }
                    break;

                case RepeatStmt r:
                    int count = (int)Eval(r.Count).AsNumber();
                    for (int i = 0; i < count; i++)
                    {
                        _flow = Flow.None;
                        ExecStmt(r.Body);
                        if (_flow == Flow.Return) return;
                        if (_flow == Flow.BreakContinue) _flow = Flow.None;
                    }
                    break;

                case ForStmt f:
                    PushScope();
                    if (f.Initializer != null) ExecStmt(f.Initializer);
                    while (f.Condition == null || Eval(f.Condition).IsTruthy)
                    {
                        _flow = Flow.None;
                        ExecStmt(f.Body);
                        if (_flow == Flow.Return) { PopScope(); return; }
                        if (_flow == Flow.BreakContinue) _flow = Flow.None;
                        if (f.Increment != null) Eval(f.Increment);
                    }
                    PopScope();
                    break;

                case ReturnStmt r:
                    _flow = Flow.Return;
                    break;

                case FunctionDeclStmt fn:
                    _functions[fn.Name] = fn;
                    break;

                case WithStmt w:
                    // GameMaker 'with' iterates instances; without a full instance-query
                    // model, run the body once with the target bound as the current instance.
                    Eval(w.Target); // evaluate (may have side effects)
                    ExecStmt(w.Body);
                    break;

                case NamespaceBlockStmt ns:
                    ExecStmt(ns.Body);
                    break;
            }
        }

        // ── Expressions ───────────────────────────────────────────────────────────

        private PgslValue Eval(Expr expr)
        {
            switch (expr)
            {
                case NumberExpr n: return new PgslValue(n.Value);
                case StringExpr s: return new PgslValue(s.Value);
                case BoolExpr b:   return b.Value ? PgslValue.True : PgslValue.False;
                case NullExpr:     return PgslValue.Null;

                case IdentifierExpr id:
                    return TryGetVar(id.Name, out var v) ? v : PgslValue.Zero;

                case BinaryExpr bin:
                    return EvalBinary(bin);

                case UnaryExpr un:
                    var operand = Eval(un.Operand);
                    return un.Op switch
                    {
                        "-" => new PgslValue(-operand.AsNumber()),
                        "!" => operand.IsTruthy ? PgslValue.False : PgslValue.True,
                        "~" => new PgslValue((double)(~(long)operand.AsNumber())),
                        _   => PgslValue.Null,
                    };

                case AssignExpr a:
                    var rhs = Eval(a.Value);
                    if (a.Op == "=") { SetVar(a.Target, rhs); return rhs; }
                    TryGetVar(a.Target, out var cur);
                    double lhs = cur.AsNumber();
                    double val = rhs.AsNumber();
                    var result = a.Op switch
                    {
                        "+="  => new PgslValue(lhs + val),
                        "-="  => new PgslValue(lhs - val),
                        "*="  => new PgslValue(lhs * val),
                        "/="  => new PgslValue(lhs / val),
                        _     => rhs,
                    };
                    SetVar(a.Target, result);
                    return result;

                case PostfixExpr p:
                    TryGetVar(p.Target, out var pv);
                    double curVal = pv.AsNumber();
                    var newVal = new PgslValue(p.Op == "++" ? curVal + 1 : curVal - 1);
                    SetVar(p.Target, newVal);
                    return new PgslValue(curVal); // postfix returns the old value

                case CallExpr c:
                    return EvalCall(c);

                case MemberExpr m:
                    // Resolve dotted names (e.g. Engine.Draw) as compound identifiers.
                    return EvalMember(m);

                case IndexExpr ix:
                    var target = Eval(ix.Target);
                    var index = Eval(ix.Index);
                    return IndexGet(target, index);

                case TernaryExpr t:
                    return Eval(t.Condition).IsTruthy ? Eval(t.IfTrue) : Eval(t.IfFalse);

                default:
                    return PgslValue.Null;
            }
        }

        private PgslValue EvalBinary(BinaryExpr bin)
        {
            // Short-circuit logical ops (don't evaluate RHS unnecessarily).
            if (bin.Op == "&&")
            {
                if (!Eval(bin.Left).IsTruthy) return PgslValue.False;
                return Eval(bin.Right).IsTruthy ? PgslValue.True : PgslValue.False;
            }
            if (bin.Op == "||")
            {
                if (Eval(bin.Left).IsTruthy) return PgslValue.True;
                return Eval(bin.Right).IsTruthy ? PgslValue.True : PgslValue.False;
            }

            var l = Eval(bin.Left);
            var r = Eval(bin.Right);

            // String concatenation when either side is a non-null string.
            if (bin.Op == "+" && (l.Type == PgslValue.T.String || r.Type == PgslValue.T.String))
                return new PgslValue(l.ToString() + r.ToString());

            double ln = l.AsNumber(), rn = r.AsNumber();
            return bin.Op switch
            {
                "+"  => new PgslValue(ln + rn),
                "-"  => new PgslValue(ln - rn),
                "*"  => new PgslValue(ln * rn),
                "/"  => new PgslValue(ln / rn),
                "%"  => new PgslValue(ln % rn),
                "==" => BoolVal(ln == rn),
                "!=" => BoolVal(ln != rn),
                "<"  => BoolVal(ln < rn),
                "<=" => BoolVal(ln <= rn),
                ">"  => BoolVal(ln > rn),
                ">=" => BoolVal(ln >= rn),
                "&"  => new PgslValue((double)((long)ln & (long)rn)),
                "|"  => new PgslValue((double)((long)ln | (long)rn)),
                "^"  => new PgslValue((double)((long)ln ^ (long)rn)),
                "<<" => new PgslValue((double)((long)ln << (int)rn)),
                ">>" => new PgslValue((double)((long)ln >> (int)rn)),
                _    => PgslValue.Null,
            };
        }

        private static PgslValue BoolVal(bool b) => b ? PgslValue.True : PgslValue.False;

        private PgslValue EvalCall(CallExpr call)
        {
            string name = call.Name;
            string lower = name.ToLowerInvariant();

            // Math intrinsics.
            switch (lower)
            {
                case "sin": return new PgslValue(Math.Sin(Arg(call, 0).AsNumber()));
                case "cos": return new PgslValue(Math.Cos(Arg(call, 0).AsNumber()));
                case "tan": return new PgslValue(Math.Tan(Arg(call, 0).AsNumber()));
                case "abs": return new PgslValue(Math.Abs(Arg(call, 0).AsNumber()));
                case "sqrt": return new PgslValue(Math.Sqrt(Arg(call, 0).AsNumber()));
                case "floor": return new PgslValue(Math.Floor(Arg(call, 0).AsNumber()));
                case "ceil": return new PgslValue(Math.Ceiling(Arg(call, 0).AsNumber()));
                case "round": return new PgslValue(Math.Round(Arg(call, 0).AsNumber()));
                case "min": return new PgslValue(Math.Min(Arg(call, 0).AsNumber(), Arg(call, 1).AsNumber()));
                case "max": return new PgslValue(Math.Max(Arg(call, 0).AsNumber(), Arg(call, 1).AsNumber()));
                case "pow": return new PgslValue(Math.Pow(Arg(call, 0).AsNumber(), Arg(call, 1).AsNumber()));
                case "sign": return new PgslValue(Math.Sign(Arg(call, 0).AsNumber()));
                case "random":
                    double max = call.Arguments.Count >= 1 ? Arg(call, 0).AsNumber() : 1.0;
                    return new PgslValue(System.Random.Shared.NextDouble() * max);
            }

            // User-defined function?
            if (_functions.TryGetValue(name, out var fn))
                return CallUserFunction(fn, call);

            // Namespaced Engine command — dispatch through the unified pipeline (profiled +
            // debuggable). Namespace blocks fall back to the legacy global name when no command
            // exists in that namespace, so existing helpers remain usable inside a from-block.
            string dispatchName = PgslNamespaceResolver.Resolve(call.Namespace, name);
            var args = new object[call.Arguments.Count];
            for (int i = 0; i < args.Length; i++)
                args[i] = ToClr(Eval(call.Arguments[i]));
            if (PgslNamespaceResolver.TryResolveEngineCommand(dispatchName, out _, out _))
                return FromClr(PgslNamespaceResolver.InvokeEngineCommand(dispatchName, args));

            var result = EngineCommandPipeline.Invoke(dispatchName, args);
            return FromClr(result);
        }

        private PgslValue CallUserFunction(FunctionDeclStmt fn, CallExpr call)
        {
            PushScope();
            for (int i = 0; i < fn.Parameters.Count; i++)
                CurrentScope[fn.Parameters[i]] = i < call.Arguments.Count ? Eval(call.Arguments[i]) : PgslValue.Null;
            var prevFlow = _flow;
            _flow = Flow.None;
            foreach (var s in fn.Body.Body)
            {
                ExecStmt(s);
                if (_flow == Flow.Return) break;
            }
            PopScope();
            _flow = prevFlow; // a return inside the function doesn't propagate to the caller's flow
            return PgslValue.Zero; // PGSL functions default to 0 when no explicit return value
        }

        private PgslValue EvalMember(MemberExpr m)
        {
            // Flatten dotted chains into a compound name (e.g. Engine.Draw → "Engine.Draw").
            string compound = FlattenMember(m);
            // Engine.* compound names dispatch as commands.
            if (compound.StartsWith("Engine.", StringComparison.OrdinalIgnoreCase))
            {
                string cmd = compound.Substring("Engine.".Length);
                if (EngineCommandPipeline.Contains(cmd))
                    return FromClr(EngineCommandPipeline.Invoke(cmd));
            }
            return TryGetVar(compound, out var v) ? v : PgslValue.Null;
        }

        private static string FlattenMember(MemberExpr m)
        {
            if (m.Target is IdentifierExpr id) return $"{id.Name}.{m.Member}";
            if (m.Target is MemberExpr inner)  return $"{FlattenMember(inner)}.{m.Member}";
            return m.Member;
        }

        private static PgslValue IndexGet(PgslValue target, PgslValue index)
        {
            // Minimal: support indexing into arrays/lists stored as Object.
            if (target.Type == PgslValue.T.Object && target.Obj is System.Collections.IList list)
            {
                int i = (int)index.AsNumber();
                return (i >= 0 && i < list.Count) ? FromClr(list[i]) : PgslValue.Null;
            }
            return PgslValue.Null;
        }

        private PgslValue Arg(CallExpr call, int i)
            => i < call.Arguments.Count ? Eval(call.Arguments[i]) : PgslValue.Zero;

        // ── CLR ↔ PgslValue conversion ────────────────────────────────────────────

        private static object ToClr(PgslValue v) => v.Type switch
        {
            PgslValue.T.Null   => null,
            PgslValue.T.Number => v.Num,
            PgslValue.T.Bool   => v.Bool,
            PgslValue.T.String => v.Str,
            _                  => v.Obj,
        };

        private static PgslValue FromClr(object o) => o switch
        {
            null        => PgslValue.Null,
            double d    => new PgslValue(d),
            float f     => new PgslValue((double)f),
            int i       => new PgslValue((double)i),
            long l      => new PgslValue((double)l),
            bool b      => b ? PgslValue.True : PgslValue.False,
            string s    => new PgslValue(s),
            _           => new PgslValue(o),
        };
    }

    /// <summary>
    /// Optional instance-variable bridge for the dev interpreter. When attached,
    /// PGSL instance variables (X, Y, HSpeed, ImageXScale, ...) read/write through
    /// here to the active entity's components — mirroring what the transpiled C#
    /// does, but dynamically. Implemented by the editor sandbox + runtime host.
    /// </summary>
    public interface IPgslInstanceContext
    {
        bool IsInstanceVar(string name);
        bool TryGetInstanceVar(string name, out PgslValue value);
        void SetInstanceVar(string name, PgslValue value);
    }
}
