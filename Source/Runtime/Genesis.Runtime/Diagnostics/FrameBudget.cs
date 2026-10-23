using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Genesis.Runtime.Diagnostics
{
    /// <summary>
    /// Where the frames' time goes, part by part over many frames (the parts of <see cref="SceneWorkTimes"/>:
    /// each subsystem's update and draws, physics, the scripts' events, the static meshes): average and
    /// longest time, share of the frame, and how many frames went over a budget set for the part (or for
    /// the whole frame). Off unless asked for (GENESIS_FRAME_BUDGET=1, or FrameBudgetEnable); with the
    /// variable set, the report is written to Debug/Logs/frame_budget.txt when the game closes.
    /// </summary>
    public static class FrameBudget
    {
        private sealed class Part
        {
            public long Frames, Over;
            public double Total, Longest, Budget;
        }

        private static readonly Dictionary<string, Part> Parts = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, double> Budgets = new(StringComparer.Ordinal);
        private static long _frames, _framesOver, _lastEnded;
        private static double _frameTotal, _frameLongest;

        public static bool Enabled { get; set; } = InitialState();

        /// <summary>The whole frame's budget in milliseconds (default a 60 fps frame; GENESIS_FRAME_BUDGET_MS).</summary>
        public static double FrameMilliseconds { get; set; } = InitialFrameBudget();

        public static long Frames => _frames;

        private static bool InitialState()
        {
            bool on = Environment.GetEnvironmentVariable("GENESIS_FRAME_BUDGET") == "1";
            if (on) AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteReport();
            return on;
        }

        private static double InitialFrameBudget() =>
            double.TryParse(Environment.GetEnvironmentVariable("GENESIS_FRAME_BUDGET_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms > 0
                ? ms : 1000.0 / 60.0;

        /// <summary>A part's own budget in milliseconds (0 = none); a frame where it takes longer counts against it.</summary>
        public static void SetBudget(string part, double milliseconds)
        {
            if (string.IsNullOrWhiteSpace(part)) return;
            double budget = double.IsFinite(milliseconds) && milliseconds > 0 ? milliseconds : 0;
            Budgets[part.Trim()] = budget;
            if (Parts.TryGetValue(part.Trim(), out Part known)) known.Budget = budget;
        }

        /// <summary>Folds one frame's parts in; the player calls it as each counted frame ends.</summary>
        public static void FrameEnded(SceneWorkTimes parts)
        {
            long now = Stopwatch.GetTimestamp();
            long last = _lastEnded;
            _lastEnded = now;
            if (!Enabled || parts == null || last == 0) return;
            double frame = Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            _frames++;
            _frameTotal += frame;
            _frameLongest = Math.Max(_frameLongest, frame);
            if (frame > FrameMilliseconds) _framesOver++;
            foreach (KeyValuePair<string, long> entry in parts)
            {
                if (!Parts.TryGetValue(entry.Key, out Part part))
                {
                    part = new Part { Budget = Budgets.TryGetValue(entry.Key, out double budget) ? budget : 0 };
                    Parts[entry.Key] = part;
                }
                double ms = Stopwatch.GetElapsedTime(0, entry.Value).TotalMilliseconds;
                part.Frames++;
                part.Total += ms;
                part.Longest = Math.Max(part.Longest, ms);
                if (part.Budget > 0 && ms > part.Budget) part.Over++;
            }
        }

        /// <summary>A part's average milliseconds over the frames counted (0 when never seen).</summary>
        public static double AverageMilliseconds(string part) =>
            _frames > 0 && part != null && Parts.TryGetValue(part.Trim(), out Part known) ? known.Total / _frames : 0;

        public static void Reset()
        {
            Parts.Clear();
            _frames = _framesOver = 0;
            _frameTotal = _frameLongest = 0;
            _lastEnded = 0;
        }

        /// <summary>The frame, then the parts that took longest on average, one line each.</summary>
        public static string Report(int top)
        {
            var text = new StringBuilder();
            text.Append("frames ").Append(_frames.ToString(CultureInfo.InvariantCulture))
                .Append(" | average ms ").Append(F(_frames > 0 ? _frameTotal / _frames : 0))
                .Append(" | longest ms ").Append(F(_frameLongest))
                .Append(" | budget ms ").Append(F(FrameMilliseconds))
                .Append(" | over budget ").Append(_framesOver.ToString(CultureInfo.InvariantCulture)).AppendLine();
            text.AppendLine("part | average ms | longest ms | share of budget % | budget ms | frames over");
            foreach ((string name, Part part) in Parts.OrderByDescending(pair => pair.Value.Total).Take(Math.Max(1, top)).Select(pair => (pair.Key, pair.Value)))
            {
                double average = _frames > 0 ? part.Total / _frames : 0;
                text.Append(name).Append(" | ").Append(F(average)).Append(" | ").Append(F(part.Longest))
                    .Append(" | ").Append((average * 100 / Math.Max(0.001, FrameMilliseconds)).ToString("F1", CultureInfo.InvariantCulture))
                    .Append(" | ").Append(part.Budget > 0 ? F(part.Budget) : "-")
                    .Append(" | ").Append(part.Over.ToString(CultureInfo.InvariantCulture)).AppendLine();
            }
            return text.ToString();
        }

        private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

        private static void WriteReport()
        {
            try
            {
                string project = Genesis.Runtime.Scripting.PgslCommands.ProjectPath;
                if (string.IsNullOrWhiteSpace(project) || _frames == 0) return;
                string folder = System.IO.Path.Combine(project, "Debug", "Logs");
                System.IO.Directory.CreateDirectory(folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, "frame_budget.txt"), Report(60));
            }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { }
        }
    }
}
