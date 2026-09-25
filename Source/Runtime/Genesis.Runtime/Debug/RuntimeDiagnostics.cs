using System;
using System.Collections.Generic;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// Collects asset problems found deep in the render path, where there is no script host or
    /// debug overlay to report to, so the host can surface them on the same banner and project log
    /// that script failures use.
    /// </summary>
    /// <remarks>
    /// <para>Added for NEXT-092. A sprite whose origin resolves off its own bounds draws far outside
    /// the room, so the object vanishes while its movement, audio and collision all keep working —
    /// the game looks alive and the character simply is not there. Nothing reported it, because the
    /// draw path had nowhere to report to.</para>
    ///
    /// <para>Static because the drawing code that finds these problems is static and per-frame.
    /// Deduplicated on the message, so a defect found every frame is stated once.</para>
    /// </remarks>
    public static class RuntimeDiagnostics
    {
        private static readonly object Gate = new();
        private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);
        private static readonly Queue<string> Pending = new();

        /// <summary>Every asset problem reported since the last <see cref="Reset"/>.</summary>
        public static IReadOnlyCollection<string> ReportedProblems
        {
            get { lock (Gate) return new List<string>(Reported); }
        }

        /// <summary>
        /// Whether asset problems are collected at all.
        /// </summary>
        /// <remarks>
        /// Driven by Preferences ▸ Runtime ▸ "Collect runtime diagnostics for Profiler", which
        /// previously saved a value nothing read: collection ran unconditionally, so turning it off
        /// changed nothing. Defaults to on, matching the shipped preference.
        /// </remarks>
        public static bool Enabled { get; set; } = true;

        /// <summary>Reports an asset problem once. Repeats of the same message are ignored.</summary>
        public static void ReportAssetProblem(string message)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message)) return;
            lock (Gate)
            {
                if (!Reported.Add(message)) return;
                Pending.Enqueue(message);
            }
        }

        /// <summary>Takes the next unseen problem for the host to surface. False when drained.</summary>
        public static bool TryDequeue(out string message)
        {
            lock (Gate)
            {
                if (Pending.Count > 0)
                {
                    message = Pending.Dequeue();
                    return true;
                }
            }

            message = null;
            return false;
        }

        /// <summary>Clears everything. Called when a project starts so one run cannot inherit another's.</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                Reported.Clear();
                Pending.Clear();
            }
        }
    }
}
