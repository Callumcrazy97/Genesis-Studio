using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace Genesis.Runtime.Diagnostics
{
    /// <summary>
    /// How long each part of a scene's frame took: every subsystem's update and submission, the
    /// systems, physics, the sky. Kept so that a long frame can name the part that made it long.
    /// Costs two timestamps for each part, which is a few dozen a frame.
    /// </summary>
    public sealed class SceneWorkTimes
    {
        private readonly Dictionary<string, long> _ticks = new(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, long>> _sorted = new();

        /// <summary>Adds the time since <paramref name="started"/> to a part, and returns now.</summary>
        public long Add(string part, long started)
        {
            long now = Stopwatch.GetTimestamp();
            _ticks.TryGetValue(part, out long ticks);
            _ticks[part] = ticks + (now - started);
            return now;
        }

        /// <summary>Forgets what has been added, ready for the next frame.</summary>
        public void Clear() => _ticks.Clear();

        /// <summary>The time added to one part since the last <see cref="Clear"/>, in milliseconds.</summary>
        public double Milliseconds(string part) =>
            _ticks.TryGetValue(part, out long ticks) ? Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds : 0;

        /// <summary>
        /// The parts that took at least <paramref name="minimumMilliseconds"/>, longest first, as
        /// "terrain update 412 ms, physics step 30 ms". Empty when none did.
        /// </summary>
        public string Describe(double minimumMilliseconds = 5, int most = 4)
        {
            _sorted.Clear();
            foreach (KeyValuePair<string, long> part in _ticks)
                if (Stopwatch.GetElapsedTime(0, part.Value).TotalMilliseconds >= minimumMilliseconds) _sorted.Add(part);
            if (_sorted.Count == 0) return string.Empty;
            _sorted.Sort(static (a, b) => b.Value.CompareTo(a.Value));
            var text = new StringBuilder();
            for (int index = 0; index < _sorted.Count && index < most; index++)
            {
                if (index > 0) text.Append(", ");
                text.Append(_sorted[index].Key).Append(' ')
                    .Append(Stopwatch.GetElapsedTime(0, _sorted[index].Value).TotalMilliseconds
                        .ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" ms");
            }

            return text.ToString();
        }

        private static readonly Dictionary<(Type Type, string Doing), string> Names = new();

        /// <summary>
        /// A subsystem's name for the log: its type without the word "Subsystem", then what it was
        /// doing. The same string is returned each time, so naming a part allocates nothing.
        /// </summary>
        public static string NameOf(object part, string doing)
        {
            var key = (part.GetType(), doing);
            lock (Names)
            {
                if (Names.TryGetValue(key, out string name)) return name;
                name = key.Item1.Name;
                foreach (string suffix in new[] { "Subsystem", "System" })
                    if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        name = name[..^suffix.Length];
                        break;
                    }

                return Names[key] = name + " " + doing;
            }
        }
    }
}
