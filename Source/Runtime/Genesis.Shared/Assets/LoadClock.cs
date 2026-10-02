using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Genesis.Shared.Assets
{
    /// <summary>What a piece of loading work was, for <see cref="LoadClock"/>.</summary>
    public enum LoadWork
    {
        /// <summary>A model file read, or waited for, by the thread that needed it.</summary>
        ModelRead,
        /// <summary>A model's meshes sent to the graphics card.</summary>
        ModelUpload,
        /// <summary>A texture read, decoded and sent to the graphics card.</summary>
        Texture,
        /// <summary>A collision shape fitted to a model's triangles.</summary>
        Collider,
        /// <summary>A shader pipeline compiled by the graphics driver.</summary>
        Pipeline,
        /// <summary>A sound file read and decoded.</summary>
        Sound,
        /// <summary>A piece of terrain made solid for the physics engine.</summary>
        TerrainCollider,
        /// <summary>The trees and rocks of one terrain cell worked out so those nearby can be made solid.</summary>
        ScatterCell,
    }

    /// <summary>
    /// Adds up the loading work done on the game's own thread, by kind, so a long frame can say
    /// what it was spent on without a profiler. Work on worker threads is not counted: it does not
    /// hold a frame up. Costs two timestamps per piece of work.
    /// </summary>
    public static class LoadClock
    {
        private static readonly int Kinds = Enum.GetValues<LoadWork>().Length;
        private static readonly long[] Ticks = new long[Kinds];
        private static readonly int[] Counts = new int[Kinds];
        private static int _gameThread;

        /// <summary>Names the thread whose work is counted. Until it is called, every thread's is.</summary>
        public static void UseCurrentThread() => _gameThread = Environment.CurrentManagedThreadId;

        /// <summary>Counts every thread's work again, as before any thread was named.</summary>
        public static void UseEveryThread() => _gameThread = 0;

        public readonly struct Scope : IDisposable
        {
            private readonly LoadWork _work;
            private readonly long _started;

            internal Scope(LoadWork work, long started)
            {
                _work = work;
                _started = started;
            }

            public void Dispose()
            {
                if (_started == 0) return;
                Interlocked.Add(ref Ticks[(int)_work], Stopwatch.GetTimestamp() - _started);
                Interlocked.Increment(ref Counts[(int)_work]);
            }
        }

        /// <summary>Times the work until the returned value is disposed.</summary>
        public static Scope Measure(LoadWork work) =>
            _gameThread != 0 && Environment.CurrentManagedThreadId != _gameThread
                ? default
                : new Scope(work, Stopwatch.GetTimestamp());

        /// <summary>How many pieces of one kind of work have been done since the process started.</summary>
        public static int Count(LoadWork work) => Volatile.Read(ref Counts[(int)work]);

        /// <summary>The time spent on one kind of work since the process started.</summary>
        public static double Milliseconds(LoadWork work) =>
            Stopwatch.GetElapsedTime(0, Interlocked.Read(ref Ticks[(int)work])).TotalMilliseconds;

        /// <summary>A copy of every total, to compare with a later one.</summary>
        public static LoadClockSnapshot Capture()
        {
            var snapshot = new LoadClockSnapshot(new long[Kinds], new int[Kinds]);
            for (int kind = 0; kind < Kinds; kind++)
            {
                snapshot.Ticks[kind] = Interlocked.Read(ref Ticks[kind]);
                snapshot.Counts[kind] = Volatile.Read(ref Counts[kind]);
            }

            return snapshot;
        }
    }

    public readonly record struct LoadClockSnapshot(long[] Ticks, int[] Counts)
    {
        /// <summary>The time, in milliseconds, spent on loading between an earlier snapshot and this one.</summary>
        public double MillisecondsSince(LoadClockSnapshot earlier)
        {
            long ticks = 0;
            for (int kind = 0; kind < Ticks.Length; kind++)
                ticks += Ticks[kind] - (earlier.Ticks?[kind] ?? 0);
            return Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;
        }

        /// <summary>
        /// What was loaded between an earlier snapshot and this one, in words:
        /// "12 textures 640 ms, 3 models read 85 ms". Empty when nothing was.
        /// </summary>
        public string Describe(LoadClockSnapshot earlier)
        {
            var text = new StringBuilder();
            for (int kind = 0; kind < Ticks.Length; kind++)
            {
                int count = Counts[kind] - (earlier.Counts?[kind] ?? 0);
                if (count <= 0) continue;
                double milliseconds = Stopwatch.GetElapsedTime(0, Ticks[kind] - (earlier.Ticks?[kind] ?? 0)).TotalMilliseconds;
                if (text.Length > 0) text.Append(", ");
                text.Append(count).Append(' ').Append(Name((LoadWork)kind, count)).Append(' ')
                    .Append(milliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(" ms");
            }

            return text.ToString();
        }

        private static string Name(LoadWork work, int count) => work switch
        {
            LoadWork.ModelRead => count == 1 ? "model read" : "models read",
            LoadWork.ModelUpload => count == 1 ? "model sent to the graphics card" : "models sent to the graphics card",
            LoadWork.Texture => count == 1 ? "texture" : "textures",
            LoadWork.Collider => count == 1 ? "collision shape fitted" : "collision shapes fitted",
            LoadWork.Pipeline => count == 1 ? "shader pipeline" : "shader pipelines",
            LoadWork.Sound => count == 1 ? "sound" : "sounds",
            LoadWork.TerrainCollider => count == 1 ? "piece of terrain made solid" : "pieces of terrain made solid",
            LoadWork.ScatterCell => count == 1 ? "cell of scattered objects placed" : "cells of scattered objects placed",
            _ => work.ToString(),
        };
    }
}
