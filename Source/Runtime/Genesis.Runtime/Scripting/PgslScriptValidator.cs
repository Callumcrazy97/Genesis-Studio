#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Genesis.Runtime.Scripting.Ast;

namespace Genesis.Runtime.Scripting
{
    /// <summary>Result of validating one or more PGSL scripts.</summary>
    public sealed class PgslValidationReport
    {
        public bool Success { get; init; }
        public int ScriptCount { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public string Summary { get; init; }
    }

    /// <summary>Shared editor and Play/Build validation entry point.</summary>
    public static class PgslScriptValidator
    {
        /// <summary>
        /// Validate all authored PGSL under Scripts, Objects, and Assets. The legacy
        /// <paramref name="compileTranspiled"/> argument is retained for API compatibility; F5 performs
        /// the actual transpile/Roslyn pass immediately after strict validation succeeds.
        /// </summary>
        public static PgslValidationReport ValidateProject(
            string projectPath,
            bool compileTranspiled = false,
            bool strict = false)
        {
            _ = compileTranspiled;
            var errors = new List<string>();
            var warnings = new List<string>();
            List<string> files = EnumerateProjectScripts(projectPath);

            var externalFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var objectVariables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // An object's events share one VM, so a function one event defines (usually Create) is
            // callable from its other events, as the game runs it.
            var objectFunctions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var fileVariables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            // Names the project sets anywhere (any script's writes, and any quoted name such as
            // VariableSet("plx", ...) or GlobalSet("score", ...)). A library sets its caller's
            // variables and one object can read what another script set, so a read of one of these
            // is not a typo; a name the project never sets or names at all still is.
            var projectNames = new HashSet<string>(StringComparer.Ordinal);
            // Every function the library Scripts declare, to warn when two share a name: a call by name
            // reaches only one of them, so calls meant for the other quietly reach the wrong function.
            var libraryDeclarations = new List<(string File, FunctionDeclStmt Function)>();
            // Every function any script declares, and each Script resource with code of its own
            // (not only functions), to warn when a function takes a Script's name: a call by that
            // name runs the function, so the Script's own code never runs from it.
            var allDeclarations = new List<(string File, FunctionDeclStmt Function)>();
            var scriptsWithCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                bool objectEvent = PgslPlayCompiler.TryObjectEventFile(file, out _, out _);
                string stem = null;
                if (!objectEvent)
                {
                    stem = ResourceNames.Name(projectPath, file, ResourceType.Script);
                    if (!string.IsNullOrWhiteSpace(stem))
                    {
                        externalFunctions.Add(stem);
                        externalFunctions.Add("scr_" + stem);
                    }
                }

                var writes = new HashSet<string>(StringComparer.Ordinal);
                var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string text = File.ReadAllText(file);
                    foreach (System.Text.RegularExpressions.Match quoted in QuotedName.Matches(text))
                        projectNames.Add(quoted.Groups[1].Value);
                    CollectSourceWrites(text, writes, functions,
                        declared: function =>
                        {
                            allDeclarations.Add((file, function));
                            if (!objectEvent) libraryDeclarations.Add((file, function));
                        },
                        hasCode: () => { if (!string.IsNullOrWhiteSpace(stem)) scriptsWithCode[stem] = file; });
                    CollectSourceWrites(text, projectNames, new HashSet<string>(StringComparer.OrdinalIgnoreCase), includeFunctionBodies: true);
                }
                catch { /* the validation pass below reports the actual read/parse failure */ }
                projectNames.UnionWith(writes);
                // A library Script's functions are callable from any object.
                if (!objectEvent) externalFunctions.UnionWith(functions);

                if (objectEvent)
                {
                    string folder = Path.GetDirectoryName(file) ?? file;
                    if (!objectVariables.TryGetValue(folder, out HashSet<string> shared))
                    {
                        shared = new HashSet<string>(StringComparer.Ordinal);
                        objectVariables[folder] = shared;
                    }
                    shared.UnionWith(writes);
                    if (!objectFunctions.TryGetValue(folder, out HashSet<string> sharedFunctions))
                    {
                        sharedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        objectFunctions[folder] = sharedFunctions;
                    }
                    sharedFunctions.UnionWith(functions);
                }
                else
                {
                    fileVariables[file] = writes;
                }
            }

            foreach (string file in files)
            {
                string label = SafeRelativePath(projectPath, file);
                string source;
                try
                {
                    source = File.ReadAllText(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{label}: could not read ({exception.Message})");
                    continue;
                }

                IReadOnlyCollection<string> knownVariables;
                IReadOnlyCollection<string> callable = externalFunctions;
                if (PgslPlayCompiler.TryObjectEventFile(file, out _, out _))
                {
                    string folder = Path.GetDirectoryName(file) ?? file;
                    knownVariables = objectVariables.TryGetValue(folder, out HashSet<string> shared)
                        ? shared
                        : Array.Empty<string>();
                    if (objectFunctions.TryGetValue(folder, out HashSet<string> sharedFunctions) && sharedFunctions.Count > 0)
                    {
                        var merged = new HashSet<string>(externalFunctions, StringComparer.OrdinalIgnoreCase);
                        merged.UnionWith(sharedFunctions);
                        callable = merged;
                    }
                }
                else
                {
                    knownVariables = fileVariables.TryGetValue(file, out HashSet<string> local)
                        ? local
                        : Array.Empty<string>();
                }

                var known = new HashSet<string>(projectNames, StringComparer.Ordinal);
                known.UnionWith(knownVariables);
                var options = new PgslSemanticOptions
                {
                    Strict = strict,
                    ExternalFunctions = callable,
                    KnownVariables = known,
                };

                Dictionary<string, string> eventBodies = PgslPlayCompiler.SplitEventBlocks(source);
                if (eventBodies.Count > 0)
                {
                    foreach ((string eventName, string body) in eventBodies)
                        ValidateOne($"{label} [{eventName}]", body, options, errors, warnings);
                }
                else if (!PgslPlayCompiler.HasEventBlocks(source))
                {
                    ValidateOne(label, source, options, errors, warnings);
                }
            }

            WarnDuplicateLibraryFunctions(projectPath, libraryDeclarations, warnings);
            WarnFunctionsNamedLikeScripts(projectPath, allDeclarations, scriptsWithCode, warnings);

            bool success = errors.Count == 0;
            var summary = new StringBuilder();
            summary.Append($"Validated {files.Count} PGSL script(s). ");
            summary.Append(success ? "No errors." : $"{errors.Count} error(s).");
            if (warnings.Count > 0) summary.Append($" {warnings.Count} warning(s).");

            return new PgslValidationReport
            {
                Success = success,
                ScriptCount = files.Count,
                Errors = errors,
                Warnings = warnings,
                Summary = summary.ToString(),
            };
        }

        /// <summary>
        /// Validate one PGSL source. Live editor callers use the default compatibility mode; Play/Build
        /// and negative tests pass <paramref name="strict"/> to make silent fallbacks hard failures.
        /// </summary>
        public static PgslValidationReport ValidateSource(
            string source,
            string label = "script",
            bool strict = false,
            IReadOnlyCollection<string> externalFunctions = null)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            ValidateOne(label, source, new PgslSemanticOptions
            {
                Strict = strict,
                ExternalFunctions = externalFunctions ?? Array.Empty<string>(),
            }, errors, warnings);

            return new PgslValidationReport
            {
                Success = errors.Count == 0,
                ScriptCount = 1,
                Errors = errors,
                Warnings = warnings,
                Summary = errors.Count == 0
                    ? $"{label}: OK{(warnings.Count > 0 ? $" ({warnings.Count} warning(s))" : string.Empty)}."
                    : $"{label}: {errors.Count} error(s).",
            };
        }

        private static readonly System.Text.RegularExpressions.Regex QuotedName =
            new("\"([A-Za-z_][A-Za-z0-9_]*)\"", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static bool CompilesForGame(string source)
        {
            try
            {
                // VMEngine.Compile prepares the source the way the game does before compiling it.
                return !string.IsNullOrWhiteSpace(source) && Genesis.Runtime.Scripting.VM.VMEngine.Compile(source) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<string> EnumerateProjectScripts(string projectPath)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
                return new List<string>();

            foreach (string directory in new[]
            {
                Path.Combine(projectPath, "Scripts"),
                Path.Combine(projectPath, "Objects"),
                Path.Combine(projectPath, "Assets"),
            })
            {
                if (!Directory.Exists(directory)) continue;
                foreach (string file in Directory.EnumerateFiles(directory, "*.pgsl", SearchOption.AllDirectories))
                    files.Add(Path.GetFullPath(file));
            }

            return files.OrderBy(file => file, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string SafeRelativePath(string projectPath, string file)
        {
            try { return Path.GetRelativePath(projectPath, file); }
            catch { return file; }
        }

        private static void CollectSourceWrites(string source, ISet<string> writes, ISet<string> functions,
            bool includeFunctionBodies = false, Action<FunctionDeclStmt> declared = null, Action hasCode = null)
        {
            void Collect(ScriptAst ast)
            {
                PgslSemanticChecker.CollectWrittenVariables(ast, writes, includeFunctionBodies);
                bool code = false;
                foreach (Stmt statement in ast.Body)
                {
                    if (statement is FunctionDeclStmt function && !string.IsNullOrWhiteSpace(function.Name))
                    {
                        functions.Add(function.Name);
                        declared?.Invoke(function);
                    }
                    else if (statement is not FunctionDeclStmt) code = true;
                }
                if (code) hasCode?.Invoke();
            }

            Dictionary<string, string> eventBodies = PgslPlayCompiler.SplitEventBlocks(source);
            if (eventBodies.Count > 0)
            {
                foreach (string body in eventBodies.Values)
                    Collect(PgslAstBuilder.Parse(body));
            }
            else if (!PgslPlayCompiler.HasEventBlocks(source))
            {
                Collect(PgslAstBuilder.Parse(source ?? string.Empty));
            }
        }

        /// <summary>
        /// Warns once for each function name (any case) that library Scripts declare more than once,
        /// listing every declaration with its parameters. A Script run as a whole still uses its own,
        /// but a call by name from an Object or another Script reaches only one of them, and the
        /// runtime only notices at that call (and only when the argument count differs).
        /// </summary>
        private static void WarnDuplicateLibraryFunctions(
            string projectPath,
            List<(string File, FunctionDeclStmt Function)> declarations,
            List<string> warnings)
        {
            string Where((string File, FunctionDeclStmt Function) item) =>
                SafeRelativePath(projectPath, item.File)
                + (item.Function.Line > 0 ? $" line {item.Function.Line}" : string.Empty)
                + $" ({item.Function.Parameters.Count} parameter{(item.Function.Parameters.Count == 1 ? string.Empty : "s")})";

            foreach (var group in declarations
                .GroupBy(item => item.Function.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                warnings.Add($"{SafeRelativePath(projectPath, first.File)}"
                    + (first.Function.Line > 0 ? $" (line {first.Function.Line}, column {Math.Max(1, first.Function.Column)})" : string.Empty)
                    + $": function '{group.Key}' is declared {group.Count()} times in library Scripts - "
                    + string.Join("; ", group.Select(Where))
                    + ". A call by name from an Object or another Script reaches only one of them, so a call meant for another"
                    + " runs the wrong function or fails on its argument count. Rename all but one.");
            }
        }

        /// <summary>
        /// Warns once for each function (in a library Script or an Object's event) named like a Script
        /// resource that has code of its own: a call by that name runs the function and never the
        /// Script, so without arguments the Script's code silently does not run, and with arguments
        /// that do not fit the function the call is an error.
        /// </summary>
        private static void WarnFunctionsNamedLikeScripts(
            string projectPath,
            List<(string File, FunctionDeclStmt Function)> declarations,
            Dictionary<string, string> scriptsWithCode,
            List<string> warnings)
        {
            foreach (var group in declarations
                .Where(item => scriptsWithCode.ContainsKey(item.Function.Name))
                .GroupBy(item => item.Function.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                string script = scriptsWithCode[group.Key];
                warnings.Add($"{SafeRelativePath(projectPath, first.File)}"
                    + (first.Function.Line > 0 ? $" (line {first.Function.Line}, column {Math.Max(1, first.Function.Column)})" : string.Empty)
                    + $": function '{first.Function.Name}' has the name of the Script '{group.Key}' ({SafeRelativePath(projectPath, script)})."
                    + $" A call {group.Key}(...) runs the function, never the Script: without arguments the Script's own code silently does not run,"
                    + " and with arguments that do not fit the function the call is an error. Rename the function or the Script.");
            }
        }

        private static void ValidateOne(
            string label,
            string source,
            PgslSemanticOptions options,
            List<string> errors,
            List<string> warnings)
        {
            ScriptAst ast;
            try
            {
                ast = PgslAstBuilder.Parse(source ?? string.Empty);
            }
            catch (Exception exception)
            {
                // The game's own compiler is the authority: never refuse to run what it runs.
                if (CompilesForGame(source))
                {
                    warnings.Add($"{label}: not checked - the script checker could not read it ({exception.Message}), but the game compiles it.");
                    return;
                }
                errors.Add($"{label}: parse error - {exception.Message}");
                return;
            }

            foreach (PgslDiagnostic diagnostic in PgslSemanticChecker.Check(ast, options))
            {
                string location = diagnostic.Line > 0
                    ? $"{label} (line {diagnostic.Line}, column {Math.Max(1, diagnostic.Column)}): "
                    : $"{label}: ";
                string message = location + diagnostic.Message;
                if (diagnostic.Severity == PgslDiagnostic.Kind.Error) errors.Add(message);
                else warnings.Add(message);
            }
        }
    }
}
