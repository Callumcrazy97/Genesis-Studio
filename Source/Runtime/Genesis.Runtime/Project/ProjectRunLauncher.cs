using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Shared compile-and-launch path used by the editor (F5) and headless verification CLI.
    /// </summary>
    public static class ProjectRunLauncher
    {
        public sealed class CompileOutcome
        {
            public bool Success;
            public ScriptCompileResult Result;
            public string TargetDll;
            public string ErrorMessage;
        }

        public sealed class LaunchOutcome
        {
            public bool Success;
            public Process Process;
            public ProjectRunSession Session;
            public string ErrorMessage;
            public string RuntimeDir;
            public string RoomName;
        }

        public static CompileOutcome CompileScripts(string projectPath, string outputDir = null)
        {
            projectPath = ProjectRoomResolver.ResolveProjectRoot(projectPath);
            // Run keeps the compiled scripts inside the project. The Player's own folder is
            // read-only once Studio is installed, and one folder cannot serve two projects.
            bool intoProject = outputDir == null && !string.IsNullOrEmpty(projectPath);
            if (intoProject) outputDir = RuntimePaths.ProjectScriptsDir(projectPath);
            string targetDll = string.IsNullOrEmpty(outputDir)
                ? null
                : Path.Combine(outputDir, "GameScripts.dll");
            if (intoProject) RemoveScriptsBesidePlayer();

            if (string.IsNullOrEmpty(projectPath))
            {
                return new CompileOutcome
                {
                    Success = false,
                    ErrorMessage = "Invalid project path.",
                    TargetDll = targetDll,
                };
            }

            // Run (F5) compiled every C# script again on every press, seconds on the UI thread for
            // a project with a few dozen. When the scripts, their resource names and the engine are
            // what the compiled scripts in the project were made from, those are used again; PGSL
            // is still checked in full.
            string inputs = intoProject ? ScriptInputsFingerprint(projectPath) : null;
            if (inputs != null && File.Exists(targetDll) && ReadText(targetDll + InputsSuffix) == inputs)
            {
                PgslValidationReport validation = PgslScriptValidator.ValidateProject(projectPath, strict: true);
                if (validation.Success)
                {
                    return new CompileOutcome
                    {
                        Success = true,
                        TargetDll = targetDll,
                        Result = new ScriptCompileResult
                        {
                            Success = true,
                            Warnings = validation.Warnings,
                            BehaviorTypeNames = Array.Empty<string>(),
                        },
                    };
                }
            }

            var result = CSharpScriptCompiler.CompileProjectScripts(projectPath);
            if (!result.Success)
            {
                TryDelete(targetDll + InputsSuffix);
                QuarantineStaleScriptDll(targetDll);
                string errors = string.Join(Environment.NewLine, result.Errors.Take(8));
                return new CompileOutcome
                {
                    Success = false,
                    Result = result,
                    TargetDll = targetDll,
                    ErrorMessage = errors,
                };
            }

            if (!string.IsNullOrEmpty(targetDll))
            {
                try
                {
                    TryDelete(targetDll + InputsSuffix);
                    if (result.AssemblyBytes != null && result.AssemblyBytes.Length > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetDll));
                        File.WriteAllBytes(targetDll, result.AssemblyBytes);
                        if (inputs != null)
                        {
                            try { File.WriteAllText(targetDll + InputsSuffix, inputs); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    else if (File.Exists(targetDll))
                    {
                        try { File.Delete(targetDll); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    return new CompileOutcome
                    {
                        Success = false,
                        Result = result,
                        TargetDll = targetDll,
                        ErrorMessage = "Failed to write GameScripts.dll: " + ex.Message,
                    };
                }
            }

            return new CompileOutcome { Success = true, Result = result, TargetDll = targetDll };
        }

        /// <summary>Beside the compiled scripts: what they were compiled from (see <see cref="ScriptInputsFingerprint"/>).</summary>
        private const string InputsSuffix = ".inputs";

        /// <summary>
        /// A digest of everything a project's compiled C# depends on: the engine build, and each
        /// script's path, resource names and text. Null when the project has no C# scripts.
        /// </summary>
        private static string ScriptInputsFingerprint(string projectPath)
        {
            try
            {
                var scripts = new List<NamedResource>();
                foreach (NamedResource entry in ResourceNames.For(projectPath).Entries)
                    if (entry.Type == ResourceType.Script && entry.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                        scripts.Add(entry);
                if (scripts.Count == 0) return null;
                scripts.Sort((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));

                using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                void Add(string text) => hash.AppendData(System.Text.Encoding.UTF8.GetBytes((text ?? string.Empty) + "\n"));
                Add("genesis-script-inputs-v1");
                Add(typeof(CSharpScriptCompiler).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
                Add(typeof(Genesis.Shared.Assets.ResourceCatalog).Assembly.ManifestModule.ModuleVersionId.ToString("N"));
                // Every engine library the scripts may be compiled against, by size and time.
                foreach (string folder in new[] { AppContext.BaseDirectory, RuntimePaths.ResolveRuntimeDir() })
                {
                    if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                    string[] engine = Directory.GetFiles(folder, "Genesis*.dll");
                    Array.Sort(engine, StringComparer.OrdinalIgnoreCase);
                    foreach (string library in engine)
                    {
                        var info = new FileInfo(library);
                        Add(library + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
                    }
                }
                foreach (NamedResource script in scripts)
                {
                    Add(script.FullPath);
                    Add(script.Name);
                    Add(script.StorageName);
                    hash.AppendData(File.ReadAllBytes(script.FullPath));
                    Add(string.Empty);
                }
                return Convert.ToHexString(hash.GetHashAndReset());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return null;
            }
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Earlier versions wrote every project's scripts beside the Player. One left there would
        /// be loaded by a project that has no C# of its own, so it is removed where that is allowed.
        /// </summary>
        private static void RemoveScriptsBesidePlayer()
        {
            string runtimeDir = RuntimePaths.ResolveRuntimeDir();
            if (string.IsNullOrEmpty(runtimeDir)) return;
            string beside = Path.Combine(runtimeDir, "GameScripts.dll");
            try { if (File.Exists(beside)) File.Delete(beside); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>Renames a stale GameScripts.dll so play/sandbox cannot load outdated types.</summary>
        private static void QuarantineStaleScriptDll(string targetDll)
        {
            if (string.IsNullOrEmpty(targetDll) || !File.Exists(targetDll)) return;
            try
            {
                string quarantine = targetDll + ".stale." + DateTime.UtcNow.Ticks;
                File.Move(targetDll, quarantine);
            }
            catch
            {
                try { File.Delete(targetDll); } catch { }
            }
        }

        public static LaunchOutcome Launch(
            string projectPath,
            string roomName = null,
            string runtimeDir = null,
            bool waitForExit = false,
            IDictionary<string, string> extraEnvironment = null,
            bool debug = false,
            bool supervised = false,
            Action<int, string> exited = null)
        {
            projectPath = ProjectRoomResolver.ResolveProjectRoot(projectPath);
            runtimeDir ??= RuntimePaths.ResolveRuntimeDir();
            string exePath = runtimeDir == null ? null : Path.Combine(runtimeDir, RuntimePaths.RuntimeExeName);

            if (string.IsNullOrEmpty(projectPath))
                return new LaunchOutcome { Success = false, ErrorMessage = "Invalid project path." };

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return new LaunchOutcome
                {
                    Success = false,
                    ErrorMessage = "Could not find GenesisEngine.exe. Build Genesis Studio first (Build.bat).",
                };
            }

            roomName = ProjectRoomResolver.ResolveRoomName(projectPath, roomName);
            if (string.IsNullOrEmpty(roomName))
            {
                return new LaunchOutcome
                {
                    Success = false,
                    ErrorMessage = "Project has no rooms. Create at least one room before running.",
                };
            }

            if (ProjectRoomResolver.ResolveRoomFile(projectPath, roomName) == null)
            {
                return new LaunchOutcome
                {
                    Success = false,
                    ErrorMessage = $"Room file not found for '{roomName}'.",
                };
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                WorkingDirectory = runtimeDir,
                UseShellExecute = false,
                RedirectStandardInput = supervised,
                RedirectStandardError = supervised,
                RedirectStandardOutput = supervised,
                CreateNoWindow = true,
                // Studio play keeps assets live; exported games (launched without this flag) load once.
                Arguments = (debug ? $"--room \"{roomName}\" --debug" : $"--room \"{roomName}\"") + " " + ProjectPlayerApp.LiveReloadArgument,
            };
            startInfo.EnvironmentVariables["GENESIS_PROJECT_PATH"] = projectPath;
            startInfo.EnvironmentVariables["GENESIS_START_ROOM"] = roomName;
            // The project's own compiled scripts, when it has any; otherwise the Player looks
            // beside itself, as an exported game does.
            string projectScripts = RuntimePaths.ProjectScriptsDll(projectPath);
            if (File.Exists(projectScripts))
                startInfo.EnvironmentVariables[RuntimePaths.GameScriptsEnvironmentVariable] = projectScripts;
            else
                startInfo.EnvironmentVariables.Remove(RuntimePaths.GameScriptsEnvironmentVariable);
            if (supervised)
            {
                using var parent = Process.GetCurrentProcess();
                startInfo.EnvironmentVariables["GENESIS_EDITOR_PID"] = parent.Id.ToString();
                startInfo.EnvironmentVariables["GENESIS_EDITOR_STARTED"] = parent.StartTime.ToUniversalTime().Ticks.ToString();
            }

            if (extraEnvironment != null)
            {
                foreach (var kv in extraEnvironment)
                    startInfo.EnvironmentVariables[kv.Key] = kv.Value ?? string.Empty;
            }

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                    return new LaunchOutcome { Success = false, ErrorMessage = "Process.Start returned null." };

                var session = supervised ? new ProjectRunSession(process, exited) : null;
                if (waitForExit)
                    process.WaitForExit();

                return new LaunchOutcome
                {
                    Success = true,
                    Process = process,
                    Session = session,
                    RuntimeDir = runtimeDir,
                    RoomName = roomName,
                };
            }
            catch (Exception ex)
            {
                return new LaunchOutcome { Success = false, ErrorMessage = ex.Message };
            }
        }
    }
}
