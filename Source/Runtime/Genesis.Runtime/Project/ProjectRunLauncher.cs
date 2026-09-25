using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Genesis.Runtime.Scripting;

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
            outputDir ??= RuntimePaths.ResolveRuntimeDir() ?? RuntimePaths.StudioDir;
            string targetDll = string.IsNullOrEmpty(outputDir)
                ? null
                : Path.Combine(outputDir, "GameScripts.dll");

            if (string.IsNullOrEmpty(projectPath))
            {
                return new CompileOutcome
                {
                    Success = false,
                    ErrorMessage = "Invalid project path.",
                    TargetDll = targetDll,
                };
            }

            var result = CSharpScriptCompiler.CompileProjectScripts(projectPath);
            if (!result.Success)
            {
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
                    if (result.AssemblyBytes != null && result.AssemblyBytes.Length > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetDll));
                        File.WriteAllBytes(targetDll, result.AssemblyBytes);
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

        /// <summary>Launches <c>GenesisEngine.exe</c> as a separate process (F5 and CI).</summary>
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
                Arguments = debug ? $"--room \"{roomName}\" --debug" : $"--room \"{roomName}\"",
            };
            startInfo.EnvironmentVariables["GENESIS_PROJECT_PATH"] = projectPath;
            startInfo.EnvironmentVariables["GENESIS_START_ROOM"] = roomName;
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
