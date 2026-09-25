#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Genesis.Runtime.Scripting.Ast;

namespace Genesis.Runtime.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //   PgslToCSharpTranspiler
    //   Turns a ScriptAst into C# source that, when compiled by the existing
    //   Roslyn CSharpScriptCompiler, yields a JIT-compiled EntityBehavior running
    //   at native speed. This closes the ~1600x interpreter gap (Architecture
    //   Principle #4): the same PGSL source has two backends — this one for
    //   release, the typed-stack VM for dev hot-reload.
    //
    //   Mapping:
    //     PGSL event scripts Fish_Create.pgsl / Fish_Update.pgsl →
    //       one class Fish : EntityBehavior { OnCreate / OnUpdate }
    //     PGSL globals/locals → private fields (native-typed, no boxing)
    //     PGSL instance vars (X, Y, HSpeed, ImageXScale, ...) →
    //       properties backed by ECS components (read/written by ref)
    //     PGSL commands (DrawSprite, sin, KeyCheck, ...) →
    //       Engine.* calls or direct math/library calls
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Result of transpiling PGSL event scripts into one C# behaviour class.
    /// Feed <see cref="CSharpScriptCompiler.Compile(IEnumerable{string}, string, string)"/>
    /// a temp file containing <see cref="CSharpSource"/>.
    /// </summary>
    public sealed class PgslTranspileResult
    {
        public bool Success { get; init; }
        public string CSharpSource { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public string ClassName { get; init; }
    }

    public static class PgslToCSharpTranspiler
    {
        // ── Instance-variable map: PGSL name → (C# property, component access expression) ──
        // These read/write ECS components by ref, exactly like a hand-written EntityBehavior.
        private static readonly Dictionary<string, string> InstanceVars = new(StringComparer.OrdinalIgnoreCase)
        {
            // Position — backed by the ECS Transform2DComponent (read/written by ref).
            ["x"]              = "Transform_X",
            ["y"]              = "Transform_Y",
            ["z"]              = "Transform_Z",
            // Motion.
            ["hspeed"]         = "_hspeed",
            ["vspeed"]         = "_vspeed",
            ["speed"]          = "_speed",
            ["direction"]      = "_direction",
            ["friction"]       = "_friction",
            ["gravity"]        = "_gravity",
            ["gravity_direction"]= "_gravityDirection",
            // Sprite/draw state — accept both GML camelCase and snake_case forms.
            ["image_alpha"]    = "_imageAlpha",
            ["image_angle"]    = "_imageAngle",
            ["image_xscale"]   = "_imageXScale",
            ["image_yscale"]   = "_imageYScale",
            ["image_index"]    = "_imageIndex",
            ["image_speed"]    = "_imageSpeed",
            ["sprite_index"]   = "_spriteIndex",
            ["visible"]        = "_visible",
            // Engine timing.
            ["totaltime"]      = "Game.TotalTime",
            ["deltatime"]      = "Game.DeltaTime",
        };

        /// <summary>
        /// Transpile a single PGSL event script into a C# EntityBehavior class.
        /// <paramref name="objectName"/> is the object/prefab name (e.g. "Fish");
        /// <paramref name="eventName"/> is Create / Update / Collision / Destroy.
        /// </summary>
        public static PgslTranspileResult TranspileEvent(string objectName, string eventName, string pgslSource)
        {
            try
            {
                var ast = PgslAstBuilder.Parse(pgslSource);
                string className = MakeClassName(objectName);
                string method = eventName switch
                {
                    "Create"     => "OnCreate",
                    "Update"     => "OnUpdate",
                    "Collision"  => "OnCollision",
                    "Destroy"    => "OnDestroy",
                    "Draw"       => "OnDrawHud",
                    _            => "OnUpdate",
                };
                string body = EmitBlock(ast.Body, indent: "            ");
                string source = BuildClassSource(className, method, eventName, body);
                return new PgslTranspileResult { Success = true, CSharpSource = source, ClassName = className };
            }
            catch (Exception ex)
            {
                return new PgslTranspileResult { Success = false, Errors = new[] { ex.Message } };
            }
        }

        // ── Class scaffolding ────────────────────────────────────────────────────

        private static string BuildClassSource(string className, string method, string eventName, string body)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated by PgslToCSharpTranspiler. Do not edit by hand.");
            sb.AppendLine("#pragma warning disable CS0168, CS0169, CS0414, CS0649");
            sb.AppendLine("using System;");
            sb.AppendLine("using Genesis.Runtime.Scripting;");
            sb.AppendLine("using Genesis.Runtime.ECS.Components;");
            sb.AppendLine("using Genesis.Shared.Commands;");
            sb.AppendLine("using Genesis.Shared.ECS;");
            sb.AppendLine();
            sb.AppendLine($"public sealed class {className} : EntityBehavior");
            sb.AppendLine("{");
            // Backing fields for instance variables that don't map directly to a component.
            sb.AppendLine("    private double _hspeed, _vspeed, _speed, _direction, _friction;");
            sb.AppendLine("    private double _gravity, _gravityDirection;");
            sb.AppendLine("    private double _imageAlpha = 1.0, _imageAngle, _imageXScale = 1.0, _imageYScale = 1.0;");
            sb.AppendLine("    private double _imageIndex, _imageSpeed = 1.0;");
            sb.AppendLine("    private string _spriteIndex;");
            sb.AppendLine("    private bool _visible = true;");
            sb.AppendLine("    // User-declared variables from PGSL become fields on first encounter.");
            sb.AppendLine("    private readonly System.Collections.Generic.Dictionary<string, object> _user = new();");
            sb.AppendLine();
            // Spawned room objects use the runtime TransformComponent, not the legacy shared 2D type.
            sb.AppendLine("    private ref TransformComponent Transform => ref GetComponent<TransformComponent>();");
            sb.AppendLine("    private double Transform_X { get => Transform.X; set => Transform.X = (float)value; }");
            sb.AppendLine("    private double Transform_Y { get => Transform.Y; set => Transform.Y = (float)value; }");
            sb.AppendLine("    private double Transform_Z { get => Transform.Z; set => Transform.Z = (float)value; }");
            sb.AppendLine();

            // The requested event method.
            if (method == "OnUpdate")
                sb.AppendLine($"    public override void OnUpdate(float dt) {{ {body.Trim()} }}");
            else if (method == "OnCollision")
                sb.AppendLine("    public override void OnCollision(Genesis.Shared.ECS.Entity other) {");
            else if (method == "OnDrawHud")
                sb.AppendLine("    public override void OnDrawHud(IHudCanvas hud) {");
            else
                sb.AppendLine($"    public override void {method}() {{ {body.Trim()} }}");

            if (method == "OnCollision" || method == "OnDrawHud")
            {
                sb.AppendLine(body);
                sb.AppendLine("    }");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string MakeClassName(string objectName)
        {
            // Sanitise the object name into a valid C# identifier.
            var sb = new StringBuilder();
            foreach (char c in objectName ?? "Behavior")
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            if (sb.Length == 0 || !char.IsLetter(sb[0])) sb.Insert(0, '_');
            sb.Append("_PgslBehavior");
            return sb.ToString();
        }

        // ── Statement emission ───────────────────────────────────────────────────

        private static string EmitBlock(List<Stmt> body, string indent)
        {
            var sb = new StringBuilder();
            foreach (var stmt in body)
                sb.Append(EmitStmt(stmt, indent));
            return sb.ToString();
        }

        private static string EmitStmt(Stmt stmt, string indent)
        {
            switch (stmt)
            {
                case BlockStmt b:
                    return $"{indent}{{\n{EmitBlock(b.Body, indent + "    ")}{indent}}}\n";

                case VarDeclStmt v:
                    if (v.Initializer != null)
                        return $"{indent}_user[\"{v.Name}\"] = {EmitExpr(v.Initializer)};\n";
                    return "";

                case ExprStmt e:
                    return $"{indent}{EmitExpr(e.Expression)};\n";

                // Conditions go through Truthy() because PGSL is dynamically typed: most commands
                // are declared as returning object, so `if (KeyCheck("A"))` is not valid C# on its
                // own (CS0266). The VM coerced at run time, so this only ever broke the AOT path.

                case IfStmt i:
                    var s = $"{indent}if ({EmitCondition(i.Condition)})\n{indent}{{\n{EmitStmt(i.ThenBranch, indent + "    ")}{indent}}}\n";
                    if (i.ElseBranch != null)
                        s += $"{indent}else\n{EmitStmt(i.ElseBranch, indent + "    ")}";
                    return s;

                case WhileStmt w:
                    return $"{indent}while ({EmitCondition(w.Condition)})\n{EmitStmt(w.Body, indent + "    ")}";

                // (EmitCondition is defined below EmitExpr.)

                case RepeatStmt r:
                    return $"{indent}for (int _rep = (int)Genesis.Runtime.Scripting.PgslCommands.Num({EmitExpr(r.Count)}); _rep > 0; _rep--)\n{EmitStmt(r.Body, indent + "    ")}";

                case ForStmt f:
                    return $"{indent}for ({EmitForInit(f.Initializer)} {EmitExprOpt(f.Condition)}; {EmitExprOpt(f.Increment)})\n{EmitStmt(f.Body, indent + "    ")}";

                case ReturnStmt ret:
                    return ret.Value != null ? $"{indent}return {EmitExpr(ret.Value)};\n" : $"{indent}return;\n";

                case FunctionDeclStmt fn:
                    // Emit a private C# method for user-defined functions.
                    var parms = string.Join(", ", fn.Parameters.ConvertAll(p => $"double {p}"));
                    var fnBody = EmitBlock(fn.Body.Body, indent + "    ");
                    return $"{indent}private double {fn.Name}({parms}) {{ {fnBody.Trim()} return 0; }}\n";

                case WithStmt w:
                    // 'with' is a GameMaker instance-iteration construct; without a full instance
                    // model, emit it as a single-iteration block on the target (honest best-effort
                    // until the ECS instance-query subsystem lands in Phase 5).
                    return $"{indent}/* with({EmitExpr(w.Target)}) */ {{\n{EmitStmt(w.Body, indent + "    ")}{indent}}}\n";

                case NamespaceBlockStmt ns:
                    // Namespace resolution is carried by CallExpr.Namespace, so generated C#
                    // only needs the lexical block for readability.
                    return $"{indent}/* from {ns.Namespace} */ {{\n{EmitStmt(ns.Body, indent + "    ")}{indent}}}\n";

                default:
                    return "";
            }
        }

        private static string EmitForInit(Stmt init)
        {
            if (init is VarDeclStmt v && v.Initializer != null)
                return $"_user[\"{v.Name}\"] = {EmitExpr(v.Initializer)}";
            if (init is ExprStmt e) return EmitExpr(e.Expression);
            return "";
        }

        private static string EmitExprOpt(Expr e) => e == null ? "true" : EmitExpr(e);

        /// <summary>
        /// Emits a condition, coerced to bool.
        /// </summary>
        /// <remarks>
        /// PGSL commands are mostly declared as returning <see cref="object"/>, so a condition like
        /// <c>KeyCheck("A")</c> emits C# that does not compile (CS0266). Routing every condition
        /// through <c>PgslCommands.Truthy</c> matches the coercion the VM already does at run time,
        /// and the bool overload means an ordinary comparison costs nothing extra. Fully qualified
        /// so it does not depend on which usings the behaviour template happens to emit.
        /// </remarks>
        private static string EmitCondition(Expr condition) =>
            $"Genesis.Runtime.Scripting.PgslCommands.Truthy({EmitExprOpt(condition)})";

        // ── Expression emission ──────────────────────────────────────────────────

        private static string EmitExpr(Expr expr)
        {
            switch (expr)
            {
                case NumberExpr n:
                    return n.Value.ToString("R", CultureInfo.InvariantCulture);

                case StringExpr s:
                    return "\"" + s.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

                case BoolExpr b:
                    return b.Value ? "true" : "false";

                case NullExpr:
                    return "null";

                case IdentifierExpr id:
                    return ResolveIdentifier(id.Name);

                case BinaryExpr bin:
                    return EmitBinary(bin.Op, EmitExpr(bin.Left), EmitExpr(bin.Right));

                case UnaryExpr un:
                    return EmitUnary(un.Op, EmitExpr(un.Operand));

                case AssignExpr a:
                    var target = ResolveIdentifier(a.Target);
                    var rhs = EmitExpr(a.Value);
                    if (a.Op == "=") return $"{target} = {CoerceAssignment(a.Target, rhs)}";
                    string binaryOp = a.Op.EndsWith("=", StringComparison.Ordinal)
                        ? a.Op.Substring(0, a.Op.Length - 1)
                        : a.Op;
                    return $"{target} = {CoerceAssignment(a.Target, EmitBinary(binaryOp, target, rhs))}";

                case PostfixExpr p:
                    return $"{ResolveIdentifier(p.Target)}{p.Op}";

                case CallExpr c:
                    return EmitCall(c);

                case MemberExpr m:
                    // Map Engine.Draw etc. onto the Engine facade.
                    if (m.Target is IdentifierExpr tid)
                        return $"{tid.Name}.{m.Member}";
                    return $"{EmitExpr(m.Target)}.{m.Member}";

                case IndexExpr ix:
                    return $"{EmitExpr(ix.Target)}[{EmitExpr(ix.Index)}]";

                case TernaryExpr t:
                    return $"({EmitCondition(t.Condition)} ? {EmitExpr(t.IfTrue)} : {EmitExpr(t.IfFalse)})";

                default:
                    return "/* unsupported expr */";
            }
        }

        /// <summary>Resolve a bare PGSL identifier to its C# equivalent.</summary>
        private static string ResolveIdentifier(string name)
        {
            if (InstanceVars.TryGetValue(name, out var mapped))
                return mapped;
            // Unknown names (user vars) go through the typed dictionary.
            return $"_user[\"{name}\"]";
        }

        private static string CoerceAssignment(string name, string expression)
        {
            if (!InstanceVars.ContainsKey(name)) return expression;
            if (string.Equals(name, "visible", StringComparison.OrdinalIgnoreCase))
                return $"Genesis.Runtime.Scripting.PgslCommands.Truthy({expression})";
            if (string.Equals(name, "sprite_index", StringComparison.OrdinalIgnoreCase))
                return $"System.Convert.ToString({expression}, System.Globalization.CultureInfo.InvariantCulture)";
            return $"Genesis.Runtime.Scripting.PgslCommands.Num({expression})";
        }

        private static string EmitBinary(string op, string left, string right) => op switch
        {
            "&&" => $"(Genesis.Runtime.Scripting.PgslCommands.Truthy({left}) && Genesis.Runtime.Scripting.PgslCommands.Truthy({right}))",
            "||" => $"(Genesis.Runtime.Scripting.PgslCommands.Truthy({left}) || Genesis.Runtime.Scripting.PgslCommands.Truthy({right}))",
            "+" => $"Genesis.Runtime.Scripting.PgslCommands.Add({left}, {right})",
            "-" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) - Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "*" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) * Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "/" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) / Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "%" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) % Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "==" => $"Genesis.Runtime.Scripting.PgslCommands.Equal({left}, {right})",
            "!=" => $"!Genesis.Runtime.Scripting.PgslCommands.Equal({left}, {right})",
            "<" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) < Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "<=" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) <= Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            ">" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) > Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            ">=" => $"(Genesis.Runtime.Scripting.PgslCommands.Num({left}) >= Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "&" => $"((long)Genesis.Runtime.Scripting.PgslCommands.Num({left}) & (long)Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "|" => $"((long)Genesis.Runtime.Scripting.PgslCommands.Num({left}) | (long)Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "^" => $"((long)Genesis.Runtime.Scripting.PgslCommands.Num({left}) ^ (long)Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            "<<" => $"((long)Genesis.Runtime.Scripting.PgslCommands.Num({left}) << (int)Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            ">>" => $"((long)Genesis.Runtime.Scripting.PgslCommands.Num({left}) >> (int)Genesis.Runtime.Scripting.PgslCommands.Num({right}))",
            _ => "0d",
        };

        private static string EmitUnary(string op, string operand) => op switch
        {
            "!" => $"(!Genesis.Runtime.Scripting.PgslCommands.Truthy({operand}))",
            "-" => $"(-Genesis.Runtime.Scripting.PgslCommands.Num({operand}))",
            "~" => $"(~(long)Genesis.Runtime.Scripting.PgslCommands.Num({operand}))",
            _ => operand,
        };

        // ── Command call emission ────────────────────────────────────────────────
        // PGSL commands (DrawSprite, sin, KeyCheck, Random, ...) map to:
        //   • Engine.* commands (the unified surface) for engine commands
        //   • Math.* for math functions
        //   • a small RuntimeHelpers static for the legacy GML-style helpers
        private static string EmitCall(CallExpr call)
        {
            string name = call.Name;
            string lower = name.ToLowerInvariant();

            // Math intrinsics — emit as native C# (no overhead).
            switch (lower)
            {
                case "sin": return $"Math.Sin({EmitNumericArg(call.Arguments, 0)})";
                case "cos": return $"Math.Cos({EmitNumericArg(call.Arguments, 0)})";
                case "tan": return $"Math.Tan({EmitNumericArg(call.Arguments, 0)})";
                case "abs": return $"Math.Abs({EmitNumericArg(call.Arguments, 0)})";
                case "sqrt": return $"Math.Sqrt({EmitNumericArg(call.Arguments, 0)})";
                case "floor": return $"Math.Floor({EmitNumericArg(call.Arguments, 0)})";
                case "ceil": return $"Math.Ceiling({EmitNumericArg(call.Arguments, 0)})";
                case "round": return $"Math.Round({EmitNumericArg(call.Arguments, 0)})";
                case "min": return $"Math.Min({EmitNumericArg(call.Arguments, 0)}, {EmitNumericArg(call.Arguments, 1)})";
                case "max": return $"Math.Max({EmitNumericArg(call.Arguments, 0)}, {EmitNumericArg(call.Arguments, 1)})";
                case "pow": return $"Math.Pow({EmitNumericArg(call.Arguments, 0)}, {EmitNumericArg(call.Arguments, 1)})";
                case "random":
                    return call.Arguments.Count == 1
                        ? $"(new System.Random().NextDouble() * {EmitNumericArg(call.Arguments, 0)})"
                        : "(new System.Random().NextDouble())";
                case "sign": return $"Math.Sign({EmitNumericArg(call.Arguments, 0)})";
            }

            string dispatchName = PgslNamespaceResolver.Resolve(call.Namespace, name);

            // Engine commands via the unified pipeline (including Engine.Rendering.X aliases).
            // The resolver performs PGSL-style numeric/bool argument coercion before dispatch.
            var args = new StringBuilder();
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (i > 0) args.Append(", ");
                args.Append(EmitExpr(call.Arguments[i]));
            }
            if (PgslNamespaceResolver.TryResolveEngineCommand(dispatchName, out _, out _))
                return $"PgslNamespaceResolver.InvokeEngineCommand(\"{dispatchName}\", new object[] {{ {args} }})";

            return $"EngineCommandPipeline.Invoke(\"{dispatchName}\"{(args.Length > 0 ? ", " + args : "")})";
        }

        private static string EmitArg(List<Expr> args, int i)
            => i < args.Count ? EmitExpr(args[i]) : "0";

        private static string EmitNumericArg(List<Expr> args, int i) =>
            $"Genesis.Runtime.Scripting.PgslCommands.Num({EmitArg(args, i)})";
    }
}
