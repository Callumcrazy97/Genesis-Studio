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

        /// <summary>Pictures asked for and not yet written: still to be read back, or being encoded.</summary>
        public static int PendingCount
        {
            get
            {
                lock (Gate)
                {
                    Writing.RemoveAll(static task => task.IsCompleted);
                    return Pending.Count + Writing.Count;
                }
            }
        }

        // Read-back pixels being encoded and written on workers. Encoding a 1920x1080 PNG on the
        // game thread held the frame it was taken in for about 110 ms (Cathedral Test's shots).
        private static readonly List<System.Threading.Tasks.Task> Writing = new();

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
                // The pixels are read back now, from the frame just drawn; the PNG is made off the frame.
                if (string.IsNullOrWhiteSpace(projectPath)
                    || !renderer.TryReadSubmittedFramePixels(out int w, out int h, out byte[] bgra) || bgra == null)
                {
                    LastPath = string.Empty;
                    continue;
                }
                string path;
                try { path = ProjectScreenshot.PathFor(projectPath, name); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { LastPath = string.Empty; continue; }
                lock (Gate)
                {
                    Writing.RemoveAll(task => task.IsCompleted);
                    System.Threading.Tasks.Task task = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            ProjectScreenshot.WritePng(path, w, h, bgra);
                            lock (Gate) { LastPath = path; Saved++; }
                        }
                        catch (Exception) { lock (Gate) LastPath = string.Empty; }
                    });
                    Writing.Add(task);
                }
            }
        }

        /// <summary>Waits for pictures still being written, so a game that ends straight after asking keeps them.</summary>
        public static void Flush(int milliseconds = 5000)
        {
            System.Threading.Tasks.Task[] tasks;
            lock (Gate) tasks = Writing.ToArray();
            if (tasks.Length > 0) System.Threading.Tasks.Task.WaitAll(tasks, milliseconds);
            lock (Gate) Writing.RemoveAll(task => task.IsCompleted);
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
