using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>
    /// Pictures a script asks for (<c>ScreenshotSave</c>): taken once the frame they were asked
    /// for in has been drawn, so one run can step a camera through many views. Saved as PNG in
    /// the project's debug images folder, like the autoshot.
    /// </summary>
    public static class ScriptScreenshots
    {
        private static readonly object Gate = new();
        private static readonly Queue<string> Pending = new();

        /// <summary>The file the last picture was saved to; empty before the first or after a failure.</summary>
        public static string LastPath { get; private set; } = string.Empty;

        /// <summary>Pictures saved since the game started.</summary>
        public static int Saved { get; private set; }

        public static int PendingCount { get { lock (Gate) return Pending.Count; } }

        /// <summary>Asks for a picture of this frame; false for an empty name.</summary>
        public static bool Request(string name)
        {
            string file = FileName(name);
            if (file.Length == 0) return false;
            lock (Gate)
            {
                if (Pending.Count >= 64) return false;
                Pending.Enqueue(file);
            }
            return true;
        }

        /// <summary>Takes the pictures asked for. The host calls this after the frame is drawn, before it is shown.</summary>
        public static void CaptureFrame(IRenderController renderer, string projectPath)
        {
            if (renderer == null) return;
            while (true)
            {
                string name;
                lock (Gate)
                {
                    if (Pending.Count == 0) return;
                    name = Pending.Dequeue();
                }
                string path = string.IsNullOrWhiteSpace(projectPath)
                    ? null
                    : ProjectScreenshot.Capture(renderer, projectPath, name, frameAlreadySubmitted: true);
                LastPath = path ?? string.Empty;
                if (path != null) Saved++;
            }
        }

        /// <summary>Forgets pictures not yet taken (a new game).</summary>
        public static void Reset()
        {
            lock (Gate) Pending.Clear();
            LastPath = string.Empty;
            Saved = 0;
        }

        private static string FileName(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) return string.Empty;
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = trimmed.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            string file = new string(chars);
            return file.Length > 120 ? file.Substring(0, 120) : file;
        }
    }
}
