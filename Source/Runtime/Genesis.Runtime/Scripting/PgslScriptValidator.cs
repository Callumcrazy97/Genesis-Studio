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
            var fileVariables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                bool objectEvent = PgslPlayCompiler.TryObjectEventFile(file, out _, out _);
                if (!objectEvent)
                {
                    string stem = ResourceNames.Name(projectPath, file, ResourceType.Script);
                    if (!string.IsNullOrWhiteSpace(stem))
                    {
                        externalFunctions.Add(stem);
                        externalFunctions.Add("scr_" + stem);
                    }
                }

                var writes = new HashSet<string>(StringComparer.Ordinal);
                try { CollectSourceWrites(File.ReadAllText(file), writes); }
                catch { /* the validation pass below reports the actual read/parse failure */ }

                if (objectEvent)
                {
                    string folder = Path.GetDirectoryName(file) ?? file;
                    if (!objectVariables.TryGetValue(folder, out HashSet<string> shared))
                    {
                        shared = new HashSet<string>(StringComparer.Ordinal);
                        objectVariables[folder] = shared;
                    }
                    shared.UnionWith(writes);
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
                if (PgslPlayCompiler.TryObjectEventFile(file, out _, out _))
                {
                    string folder = Path.GetDirectoryName(file) ?? file;
                    knownVariables = objectVariables.TryGetValue(folder, out HashSet<string> shared)
                        ? shared
                        : Array.Empty<string>();
                }
                else
                {
                    knownVariables = fileVariables.TryGetValue(file, out HashSet<string> local)
                        ? local
                        : Array.Empty<string>();
                }

                var options = new PgslSemanticOptions
                {
                    Strict = strict,
                    ExternalFunctions = externalFunctions,
                    KnownVariables = knownVariables,
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

        private static void CollectSourceWrites(string source, ISet<string> writes)
        {
            Dictionary<string, string> eventBodies = PgslPlayCompiler.SplitEventBlocks(source);
            if (eventBodies.Count > 0)
            {
                foreach (string body in eventBodies.Values)
                    PgslSemanticChecker.CollectWrittenVariables(PgslAstBuilder.Parse(body), writes);
            }
            else if (!PgslPlayCompiler.HasEventBlocks(source))
            {
                PgslSemanticChecker.CollectWrittenVariables(PgslAstBuilder.Parse(source ?? string.Empty), writes);
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
