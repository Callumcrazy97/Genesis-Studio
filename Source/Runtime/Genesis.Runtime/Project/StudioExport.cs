using System;
using System.Collections.Generic;
using System.IO;

namespace Genesis.Runtime.Project
{
    /// <summary>Copies the Ember engine payload from a Genesis Studio install for export.</summary>
    public static class StudioExport
    {
        private static readonly HashSet<string> EditorOnlyFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "Genesis Application.exe",
            "Genesis Application.dll",
            "Genesis Application.deps.json",
            "Genesis Application.runtimeconfig.json",
            "Genesis Application.pdb",
            "Genesis.Application.Core.dll",
            "Genesis.Application.Editors.Image.dll",
            "Genesis.Application.Editors.Suite.dll",
            "GenesisEditor.exe",
            "GenesisEditor.dll",
            "GenesisEditor.deps.json",
            "GenesisEditor.runtimeconfig.json",
            "GenesisEditor.pdb",
            "Genesis.Rendering.WinForms.dll",
            "Genesis.Rendering.WinForms.pdb",
            "FastColoredTextBox.dll",
            "Scintilla5.NET.dll",
            "Scintilla.NET.dll",
            "UkooLabs.FbxSharpie.dll",
            "SharpGLTF.Core.dll",
            "SharpGLTF.Runtime.dll",
            "SharpGLTF.Toolkit.dll",
        };

        public static bool IsStudioBuilt(string studioDir = null)
        {
            studioDir ??= RuntimePaths.StudioDir;
            return File.Exists(Path.Combine(studioDir, "GenesisEditor.exe"));
        }

        public static void CopyEnginePayload(string studioDir, string outputDir)
            => CopyEnginePayload(studioDir, outputDir, RuntimePaths.ResolveRuntimeDir());

        /// <summary>Copies an engine payload using an explicitly resolved Player directory.</summary>
        /// <remarks>
        /// The overload keeps export deterministic for callers that already resolved the installed
        /// Player and for regression tests that must not depend on the current process directory.
        /// </remarks>
        public static void CopyEnginePayload(string studioDir, string outputDir, string runtimeDir)
        {
            if (string.IsNullOrEmpty(studioDir) || !Directory.Exists(studioDir))
                throw new DirectoryNotFoundException("Genesis Studio folder not found.");

            Directory.CreateDirectory(outputDir);

            // A release is a Player payload. Older exports copied the complete Studio folder and
            // tried to remove a short deny-list afterwards, which leaked editor assemblies and
            // tools whenever their product names changed. Use the installed Player as the source
            // of truth and retain Studio only as a compatibility fallback for old layouts.
            string payloadDir = !string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir)
                ? runtimeDir
                : studioDir;
            foreach (string file in Directory.GetFiles(payloadDir))
            {
                string name = Path.GetFileName(file);
                if (ShouldSkipFile(name)) continue;
                File.Copy(file, Path.Combine(outputDir, name), overwrite: true);
            }
            foreach (string dir in Directory.GetDirectories(payloadDir))
                CopyDir(dir, Path.Combine(outputDir, Path.GetFileName(dir)));
        }

        private static bool ShouldSkipFile(string fileName)
        {
            if (EditorOnlyFiles.Contains(fileName))
                return true;
            if (fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains(".stale.", StringComparison.OrdinalIgnoreCase))
                return true;
            if (fileName.EndsWith(".editorconfig", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(file);
                if (ShouldSkipFile(name))
                    continue;
                File.Copy(file, Path.Combine(dst, name), overwrite: true);
            }
            foreach (string dir in Directory.GetDirectories(src))
                CopyDir(dir, Path.Combine(dst, Path.GetFileName(dir)));
        }
    }
}
