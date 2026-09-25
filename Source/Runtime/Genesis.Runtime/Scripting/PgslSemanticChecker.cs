#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Genesis.Runtime.Scripting.Ast;
using Genesis.Shared.Commands;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting
{
    /// <summary>One diagnostic from the semantic checker.</summary>
    public sealed class PgslDiagnostic
    {
        public enum Kind { Error, Warning, Info }

        public Kind Severity { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
        public string Message { get; init; }

        public override string ToString()
        {
            string severity = Severity == Kind.Error ? "error" : Severity == Kind.Warning ? "warning" : "info";
            return $"{severity} PGSL{Line}:{Column}: {Message}";
        }
    }

    /// <summary>Controls whether compatibility diagnostics warn or block Play/Build.</summary>
    public sealed class PgslSemanticOptions
    {
        /// <summary>
        /// In strict mode, constructs that can silently resolve to zero/no-op at runtime are errors.
        /// The live editor deliberately leaves these as warnings so existing files can still be edited.
        /// </summary>
        public bool Strict { get; init; }

        /// <summary>
        /// Names of project script assets that may be called like functions. Their argument lists are
        /// dynamic, so existence is checked here while argument binding remains a runtime concern.
        /// </summary>
        public IReadOnlyCollection<string> ExternalFunctions { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Variables written by sibling events on the same object (for example Create initializes
        /// score and DrawGui reads it). PGSL object variables persist across event executions.
        /// </summary>
        public IReadOnlyCollection<string> KnownVariables { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Validates a <see cref="ScriptAst"/> without executing it. Command names, implementation status,
    /// and arity come directly from the attributes on the runtime command hosts, so validation cannot
    /// drift from the VM's callable surface.
    /// </summary>
    public static class PgslSemanticChecker
    {
        private readonly record struct Arity(int Minimum, int Maximum, bool Implemented, string Signature)
        {
            public bool Accepts(int count) => count >= Minimum && count <= Maximum;
        }

        private static readonly Dictionary<string, Arity> MathIntrinsics = new(StringComparer.OrdinalIgnoreCase)
        {
            ["sin"] = new(1, 1, true, "sin(value)"),
            ["cos"] = new(1, 1, true, "cos(value)"),
            ["tan"] = new(1, 1, true, "tan(value)"),
            ["abs"] = new(1, 1, true, "abs(value)"),
            ["sqrt"] = new(1, 1, true, "sqrt(value)"),
            ["floor"] = new(1, 1, true, "floor(value)"),
            ["ceil"] = new(1, 1, true, "ceil(value)"),
            ["round"] = new(1, 1, true, "round(value)"),
            ["min"] = new(2, 2, true, "min(a, b)"),
            ["max"] = new(2, 2, true, "max(a, b)"),
            ["pow"] = new(2, 2, true, "pow(value, exponent)"),
            ["random"] = new(0, 1, true, "random(max?)"),
            ["sign"] = new(1, 1, true, "sign(value)"),
        };

        private static readonly HashSet<string> InstanceVars = BuildInstanceVariables();
        private static readonly Lazy<Dictionary<string, List<Arity>>> PgslContracts =
            new(BuildPgslContracts, isThreadSafe: true);
        private static readonly Lazy<Dictionary<string, List<Arity>>> EngineContracts =
            new(BuildEngineContracts, isThreadSafe: true);

        /// <summary>Check a script and return all semantic diagnostics.</summary>
        public static List<PgslDiagnostic> Check(ScriptAst ast, PgslSemanticOptions options = null)
        {
            var diagnostics = new List<PgslDiagnostic>();
            if (ast == null) return diagnostics;

            options ??= new PgslSemanticOptions();
            var declared = new HashSet<string>(options.KnownVariables ?? Array.Empty<string>(), StringComparer.Ordinal);
            var functions = new Dictionary<string, int>(StringComparer.Ordinal);
            var externalFunctions = new HashSet<string>(options.ExternalFunctions ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (Stmt statement in ast.Body)
            {
                if (statement is FunctionDeclStmt function && !string.IsNullOrEmpty(function.Name))
                    functions[function.Name] = function.Parameters.Count;
            }

            foreach (Stmt statement in ast.Body)
                CheckStmt(statement, declared, functions, externalFunctions, options, diagnostics);

            return diagnostics;
        }

        /// <summary>Collect names written by an event so sibling events can share object state.</summary>
        internal static void CollectWrittenVariables(ScriptAst ast, ISet<string> names)
        {
            if (ast == null || names == null) return;
            foreach (Stmt statement in ast.Body) CollectWrites(statement, names);
        }

        private static void CollectWrites(Stmt statement, ISet<string> names)
        {
            switch (statement)
            {
                case BlockStmt block:
                    foreach (Stmt child in block.Body) CollectWrites(child, names);
                    break;
                case VarDeclStmt variable:
                    if (!string.IsNullOrEmpty(variable.Name)) names.Add(variable.Name);
                    CollectWrites(variable.Initializer, names);
                    break;
                case ExprStmt expression:
                    CollectWrites(expression.Expression, names);
                    break;
                case IfStmt conditional:
                    CollectWrites(conditional.Condition, names);
                    CollectWrites(conditional.ThenBranch, names);
                    CollectWrites(conditional.ElseBranch, names);
                    break;
                case WhileStmt loop:
                    CollectWrites(loop.Condition, names);
                    CollectWrites(loop.Body, names);
                    break;
                case RepeatStmt repeat:
                    CollectWrites(repeat.Count, names);
                    CollectWrites(repeat.Body, names);
                    break;
                case ForStmt loop:
                    CollectWrites(loop.Initializer, names);
                    CollectWrites(loop.Condition, names);
                    CollectWrites(loop.Increment, names);
                    CollectWrites(loop.Body, names);
                    break;
                case ReturnStmt returned:
                    CollectWrites(returned.Value, names);
                    break;
                case WithStmt with:
                    CollectWrites(with.Target, names);
                    CollectWrites(with.Body, names);
                    break;
                case NamespaceBlockStmt ns:
                    CollectWrites(ns.Body, names);
                    break;
                // Function variables are scoped and restored by the VM, so they are not object state.
                case FunctionDeclStmt:
                    break;
            }
        }

        private static void CollectWrites(Expr expression, ISet<string> names)
        {
            switch (expression)
            {
                case null:
                    return;
                case AssignExpr assignment:
                    if (!string.IsNullOrEmpty(assignment.Target)) names.Add(assignment.Target);
                    CollectWrites(assignment.Value, names);
                    break;
                case PostfixExpr postfix:
                    if (!string.IsNullOrEmpty(postfix.Target)) names.Add(postfix.Target);
                    break;
                case BinaryExpr binary:
                    CollectWrites(binary.Left, names);
                    CollectWrites(binary.Right, names);
                    break;
                case UnaryExpr unary:
                    CollectWrites(unary.Operand, names);
                    break;
                case CallExpr call:
                    foreach (Expr argument in call.Arguments) CollectWrites(argument, names);
                    break;
                case MemberExpr member:
                    CollectWrites(member.Target, names);
                    break;
                case IndexExpr index:
                    CollectWrites(index.Target, names);
                    CollectWrites(index.Index, names);
                    break;
                case TernaryExpr ternary:
                    CollectWrites(ternary.Condition, names);
                    CollectWrites(ternary.IfTrue, names);
                    CollectWrites(ternary.IfFalse, names);
                    break;
            }
        }

        private static void CheckStmt(
            Stmt statement,
            HashSet<string> declared,
            Dictionary<string, int> functions,
            HashSet<string> externalFunctions,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            switch (statement)
            {
                case BlockStmt block:
                    foreach (Stmt child in block.Body)
                        CheckStmt(child, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case VarDeclStmt variable:
                    if (!string.IsNullOrEmpty(variable.Name))
                    {
                        ReportShadowing(variable, options, diagnostics);
                        declared.Add(variable.Name);
                    }
                    if (variable.Initializer != null)
                        CheckExpr(variable.Initializer, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case ExprStmt expression:
                    CheckExpr(expression.Expression, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case IfStmt conditional:
                    CheckExpr(conditional.Condition, declared, functions, externalFunctions, options, diagnostics);
                    CheckStmt(conditional.ThenBranch, declared, functions, externalFunctions, options, diagnostics);
                    if (conditional.ElseBranch != null)
                        CheckStmt(conditional.ElseBranch, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case WhileStmt loop:
                    CheckExpr(loop.Condition, declared, functions, externalFunctions, options, diagnostics);
                    CheckStmt(loop.Body, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case RepeatStmt repeat:
                    CheckExpr(repeat.Count, declared, functions, externalFunctions, options, diagnostics);
                    CheckStmt(repeat.Body, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case ForStmt loop:
                    if (loop.Initializer != null)
                        CheckStmt(loop.Initializer, declared, functions, externalFunctions, options, diagnostics);
                    if (loop.Condition != null)
                        CheckExpr(loop.Condition, declared, functions, externalFunctions, options, diagnostics);
                    if (loop.Increment != null)
                        CheckExpr(loop.Increment, declared, functions, externalFunctions, options, diagnostics);
                    CheckStmt(loop.Body, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case ReturnStmt returned:
                    if (returned.Value != null)
                        CheckExpr(returned.Value, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case FunctionDeclStmt function:
                {
                    var functionScope = new HashSet<string>(declared, StringComparer.Ordinal);
                    foreach (string parameter in function.Parameters)
                        functionScope.Add(parameter);
                    if (function.Body != null)
                    {
                        foreach (Stmt child in function.Body.Body)
                            CheckStmt(child, functionScope, functions, externalFunctions, options, diagnostics);
                    }
                    break;
                }

                case WithStmt with:
                    CheckExpr(with.Target, declared, functions, externalFunctions, options, diagnostics);
                    CheckStmt(with.Body, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case NamespaceBlockStmt ns:
                    if (ns.Body != null)
                        CheckStmt(ns.Body, declared, functions, externalFunctions, options, diagnostics);
                    break;
            }
        }

        private static void CheckExpr(
            Expr expression,
            HashSet<string> declared,
            Dictionary<string, int> functions,
            HashSet<string> externalFunctions,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            switch (expression)
            {
                case BinaryExpr binary:
                    CheckExpr(binary.Left, declared, functions, externalFunctions, options, diagnostics);
                    CheckExpr(binary.Right, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case UnaryExpr unary:
                    CheckExpr(unary.Operand, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case AssignExpr assignment:
                    if (!string.IsNullOrEmpty(assignment.Target)) declared.Add(assignment.Target);
                    CheckExpr(assignment.Value, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case PostfixExpr postfix:
                    if (!string.IsNullOrEmpty(postfix.Target)) declared.Add(postfix.Target);
                    break;

                case CallExpr call:
                    CheckCall(call, functions, externalFunctions, options, diagnostics);
                    foreach (Expr argument in call.Arguments)
                        CheckExpr(argument, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case MemberExpr member:
                    CheckExpr(member.Target, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case IndexExpr index:
                    CheckExpr(index.Target, declared, functions, externalFunctions, options, diagnostics);
                    CheckExpr(index.Index, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case TernaryExpr ternary:
                    CheckExpr(ternary.Condition, declared, functions, externalFunctions, options, diagnostics);
                    CheckExpr(ternary.IfTrue, declared, functions, externalFunctions, options, diagnostics);
                    CheckExpr(ternary.IfFalse, declared, functions, externalFunctions, options, diagnostics);
                    break;

                case IdentifierExpr identifier when !IsKnownName(identifier.Name, declared, functions):
                    diagnostics.Add(new PgslDiagnostic
                    {
                        Severity = CompatibilitySeverity(options),
                        Line = identifier.Line,
                        Column = identifier.Column,
                        Message = $"Undeclared variable '{identifier.Name}'. It resolves to zero at runtime.",
                    });
                    break;
            }
        }

        private static void CheckCall(
            CallExpr call,
            Dictionary<string, int> functions,
            HashSet<string> externalFunctions,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            if (string.IsNullOrEmpty(call.Name)) return;

            if (MathIntrinsics.TryGetValue(call.Name, out Arity intrinsic))
            {
                ReportArity(call, new[] { intrinsic }, options, diagnostics);
                return;
            }

            if (functions.TryGetValue(call.Name, out int functionArity))
            {
                ReportArity(call, new[] { new Arity(functionArity, functionArity, true, call.Name) }, options, diagnostics);
                return;
            }

            if (externalFunctions.Contains(call.Name)
                || (call.Name.StartsWith("scr_", StringComparison.OrdinalIgnoreCase)
                    && externalFunctions.Contains(call.Name[4..])))
            {
                return;
            }

            foreach (string candidate in CommandCandidates(call))
            {
                if (PgslContracts.Value.TryGetValue(candidate, out List<Arity> pgslOverloads))
                {
                    ReportCommandContract(call, pgslOverloads, options, diagnostics);
                    return;
                }
                if (EngineContracts.Value.TryGetValue(candidate, out List<Arity> engineOverloads))
                {
                    ReportCommandContract(call, engineOverloads, options, diagnostics);
                    return;
                }
            }

            diagnostics.Add(new PgslDiagnostic
            {
                Severity = CompatibilitySeverity(options),
                Line = call.Line,
                Column = call.Column,
                Message = $"Unknown command '{call.Name}'. It will fail instead of performing gameplay work at runtime.",
            });
        }

        private static IEnumerable<string> CommandCandidates(CallExpr call)
        {
            if (call.Name.Contains('.'))
            {
                yield return call.Name;
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(call.Namespace))
                yield return call.Namespace.Trim().Trim('.') + "." + call.Name;
            yield return call.Name;
        }

        private static void ReportCommandContract(
            CallExpr call,
            IReadOnlyList<Arity> overloads,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            int count = call.Arguments.Count;
            List<Arity> matching = overloads.Where(overload => overload.Accepts(count)).ToList();
            if (matching.Count == 0)
            {
                ReportArity(call, overloads, options, diagnostics);
                return;
            }

            if (matching.Any(overload => overload.Implemented)) return;

            string signature = matching.Select(overload => overload.Signature)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            diagnostics.Add(new PgslDiagnostic
            {
                Severity = CompatibilitySeverity(options),
                Line = call.Line,
                Column = call.Column,
                Message = $"Command '{call.Name}' is declared but not implemented; calling it has no gameplay effect."
                    + (string.IsNullOrWhiteSpace(signature) ? string.Empty : $" Signature: {signature}."),
            });
        }

        private static void ReportArity(
            CallExpr call,
            IReadOnlyList<Arity> overloads,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            int count = call.Arguments.Count;
            if (overloads.Any(overload => overload.Accepts(count))) return;

            string expected = string.Join(" or ", overloads
                .Select(FormatArity)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
            string signatures = string.Join("; ", overloads
                .Select(overload => overload.Signature)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));

            diagnostics.Add(new PgslDiagnostic
            {
                Severity = CompatibilitySeverity(options),
                Line = call.Line,
                Column = call.Column,
                Message = $"Command '{call.Name}' expects {expected} argument(s), but received {count}."
                    + (signatures.Length == 0 ? string.Empty : $" Signature: {signatures}."),
            });
        }

        private static string FormatArity(Arity arity)
        {
            if (arity.Maximum == int.MaxValue) return $"at least {arity.Minimum}";
            if (arity.Minimum == arity.Maximum) return arity.Minimum.ToString();
            return $"{arity.Minimum}-{arity.Maximum}";
        }

        private static void ReportShadowing(
            VarDeclStmt declaration,
            PgslSemanticOptions options,
            List<PgslDiagnostic> diagnostics)
        {
            if (!InstanceVars.Contains(declaration.Name)) return;

            diagnostics.Add(new PgslDiagnostic
            {
                Severity = CompatibilitySeverity(options),
                Line = declaration.Line,
                Column = declaration.Column,
                Message = $"'var {declaration.Name}' shadows the built-in instance variable '{declaration.Name}'. "
                    + $"The declaration is discarded and assignments to it will not take effect; rename the local "
                    + $"(for example '{Suggest(declaration.Name)}').",
            });
        }

        private static PgslDiagnostic.Kind CompatibilitySeverity(PgslSemanticOptions options) =>
            options?.Strict == true ? PgslDiagnostic.Kind.Error : PgslDiagnostic.Kind.Warning;

        private static bool IsKnownName(
            string name,
            HashSet<string> declared,
            Dictionary<string, int> functions)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return InstanceVars.Contains(name) || declared.Contains(name) || functions.ContainsKey(name);
        }

        private static HashSet<string> BuildInstanceVariables()
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                "self", "other", "Other", "id", "instance_id", "argument_count",
                "totaltime", "deltatime", "TotalTime", "DeltaTime",
                "mouse_x", "mouse_y", "mouse_world_x", "mouse_world_y",
                "fps", "Fps",
            };

            foreach (PropertyInfo property in typeof(PgslContext).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                names.Add(property.Name);
                names.Add(property.Name.ToLowerInvariant());
                names.Add(ToSnakeCase(property.Name));
            }

            foreach (PropertyInfo property in typeof(PgslCommands).GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                names.Add(property.Name);
                names.Add(property.Name.ToLowerInvariant());
                names.Add(ToSnakeCase(property.Name));
            }

            for (int i = 0; i < 32; i++) names.Add("argument" + i);
            return names;
        }

        private static string ToSnakeCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var characters = new List<char>(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char current = name[i];
                if (i > 0 && char.IsUpper(current) && char.IsLower(name[i - 1])) characters.Add('_');
                characters.Add(char.ToLowerInvariant(current));
            }
            return new string(characters.ToArray());
        }

        private static string Suggest(string name) => name switch
        {
            "x" or "X" => "startX",
            "y" or "Y" => "startY",
            "z" or "Z" => "startZ",
            "speed" or "Speed" => "moveSpeed",
            "gravity" or "Gravity" => "gravityStep",
            "direction" or "Direction" => "heading",
            "friction" or "Friction" => "drag",
            "visible" or "Visible" => "isVisible",
            _ => "my" + char.ToUpperInvariant(name[0]) + name[1..],
        };

        private static Dictionary<string, List<Arity>> BuildPgslContracts()
        {
            var contracts = new Dictionary<string, List<Arity>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (MethodInfo method in typeof(PgslCommands).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    foreach (PgslCommandAttribute attribute in method.GetCustomAttributes<PgslCommandAttribute>())
                    {
                        Arity arity = MethodArity(method, attribute.IsImplemented, attribute.Signature);
                        AddContract(contracts, attribute.Name, arity);
                        if (!string.IsNullOrWhiteSpace(attribute.Namespace))
                            AddContract(contracts, attribute.Namespace.Trim().Trim('.') + "." + attribute.Name, arity);
                    }
                }

                foreach (PropertyInfo property in typeof(PgslCommands).GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    foreach (PgslCommandAttribute attribute in property.GetCustomAttributes<PgslCommandAttribute>())
                    {
                        int maximum = property.CanWrite ? 1 : 0;
                        var arity = new Arity(0, maximum, attribute.IsImplemented, attribute.Signature);
                        AddContract(contracts, attribute.Name, arity);
                        if (!string.IsNullOrWhiteSpace(attribute.Namespace))
                            AddContract(contracts, attribute.Namespace.Trim().Trim('.') + "." + attribute.Name, arity);
                    }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("[PGSL validation] Could not reflect PgslCommands: " + exception.Message);
            }
            return contracts;
        }

        private static Dictionary<string, List<Arity>> BuildEngineContracts()
        {
            var contracts = new Dictionary<string, List<Arity>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (MethodInfo method in typeof(Engine).GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    EngineCommandAttribute attribute = method.GetCustomAttribute<EngineCommandAttribute>();
                    if (attribute == null) continue;
                    string name = ExtractEngineName(attribute.Signature, method.Name);
                    Arity arity = MethodArity(method, attribute.IsImplemented, attribute.Signature);
                    AddContract(contracts, name, arity);
                    AddContract(contracts, "Engine." + name, arity); // legacy fully-qualified form
                    string commandNamespace = PgslNamespaceResolver.NamespaceForEngineCategory(attribute.Category);
                    if (commandNamespace.Length > 0)
                        AddContract(contracts, commandNamespace + "." + name, arity);
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("[PGSL validation] Could not reflect Engine commands: " + exception.Message);
            }
            return contracts;
        }

        private static Arity MethodArity(MethodInfo method, bool implemented, string signature)
        {
            ParameterInfo[] parameters = method.GetParameters();
            int minimum = parameters.Count(parameter => !parameter.IsOptional
                && parameter.GetCustomAttribute<ParamArrayAttribute>() == null);
            bool variadic = parameters.Any(parameter => parameter.GetCustomAttribute<ParamArrayAttribute>() != null);
            int maximum = variadic ? int.MaxValue : parameters.Length;
            return new Arity(minimum, maximum, implemented, signature);
        }

        private static void AddContract(
            Dictionary<string, List<Arity>> contracts,
            string name,
            Arity arity)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (!contracts.TryGetValue(name, out List<Arity> overloads))
            {
                overloads = new List<Arity>();
                contracts[name] = overloads;
            }
            if (!overloads.Contains(arity)) overloads.Add(arity);
        }

        private static string ExtractEngineName(string signature, string fallback)
        {
            if (string.IsNullOrWhiteSpace(signature)) return fallback;
            int dot = signature.IndexOf('.');
            int parenthesis = signature.IndexOf('(');
            if (dot >= 0 && parenthesis > dot)
                return signature.Substring(dot + 1, parenthesis - dot - 1).Trim();
            if (parenthesis > 0) return signature[..parenthesis].Trim();
            return signature.Trim();
        }
    }
}
