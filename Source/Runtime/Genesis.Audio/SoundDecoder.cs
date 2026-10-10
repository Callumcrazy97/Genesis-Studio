using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Genesis.Shared.Audio;

namespace Genesis.Audio
{
    /// <summary>
    /// Decodes sounds on threads of its own, so no frame waits for one. A sound a game is about to
    /// play goes first; sounds asked for ahead of time (<see cref="XAudioSystem.PreloadSound"/>,
    /// the project's preload budget) are decoded whenever no play is waiting. Every sound decoded
    /// is kept by its source (the file, its size and time, the region played), so a play of it
    /// under any of its names finds it ready.
    /// </summary>
    internal sealed class SoundDecoder : IDisposable
    {
        private sealed class Job
        {
            public Job(string key, string file, AudioAssetSettings? region, bool preload)
            {
                Key = key;
                File = file;
                Region = region;
                Preload = preload;
            }

            public readonly string Key;
            public readonly string File;
            public readonly AudioAssetSettings? Region;
            public readonly TaskCompletionSource<SoundEffect?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>1 once a worker has taken it.</summary>
            public int Taken;
            /// <summary>Asked for ahead of time and not (yet) by a play.</summary>
            public bool Preload;
            /// <summary>The share of the preload budget held for it while it is decoded.</summary>
            public long Reserved;
        }

        private readonly object _gate = new();
        private readonly Queue<Job> _urgent = new();
        private readonly Queue<Job> _ahead = new();
        private readonly Dictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxThreads;
        private int _threads;
        private int _idle;
        private bool _disposed;
        private int _preloadsPending;

        // The project's preload budget: sources smallest first, decoded until the budget is used.
        private IReadOnlyList<XAudioSystem.SoundSource>? _budgetList;
        private bool _budgetListing;
        private int _budgetNext;
        private long _budgetBytes;
        private long _budgetReserved;
        private long _preloadedBytes;
        private int _preloadedSounds;
        private long _budgetStarted;
        private Action<string>? _budgetReport;

        public SoundDecoder()
        {
            // Two threads keep a play from waiting behind another sound; more help a preload of
            // hundreds of short sounds behind a loading screen, without taking the whole machine.
            _maxThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
        }

        /// <summary>
        /// Sounds asked for ahead of time that are not decoded yet, with those the preload budget
        /// has still to decode. Plays waiting for their sound are not counted.
        /// </summary>
        public int PreloadsPending
        {
            get
            {
                lock (_gate)
                {
                    int pending = _preloadsPending + (_budgetListing ? 1 : 0);
                    if (BudgetOpenLocked()) pending += _budgetList!.Count - _budgetNext;
                    return pending;
                }
            }
        }

        /// <summary>
        /// The decode of one source: the one already made or under way, or a new one. A play
        /// (<paramref name="preload"/> false) of a sound still waiting to be decoded ahead of time
        /// moves it to the front.
        /// </summary>
        public Task<SoundEffect?> Request(string key, string file, AudioAssetSettings? region, bool preload)
        {
            lock (_gate)
            {
                if (_jobs.TryGetValue(key, out Job? existing))
                {
                    if (!preload) PromoteLocked(existing);
                    return existing.Done.Task;
                }

                var job = new Job(key, file, region, preload);
                _jobs[key] = job;
                if (preload)
                {
                    _preloadsPending++;
                    _ahead.Enqueue(job);
                }
                else
                {
                    _urgent.Enqueue(job);
                }

                WakeLocked(all: false);
                return job.Done.Task;
            }
        }

        /// <summary>Whether this source has been decoded, or asked for.</summary>
        public bool Knows(string key)
        {
            lock (_gate) return _jobs.ContainsKey(key);
        }

        /// <summary>Lets go of a source's samples (its file has changed); a play still holding them keeps them.</summary>
        public void Forget(string key)
        {
            lock (_gate)
            {
                if (_jobs.TryGetValue(key, out Job? job) && job.Done.Task.IsCompleted) _jobs.Remove(key);
            }
        }

        /// <summary>A play wants a sound that was asked for ahead of time: it is decoded next.</summary>
        public void Promote(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                if (_jobs.TryGetValue(key, out Job? job)) PromoteLocked(job);
            }
        }

        private void PromoteLocked(Job job)
        {
            if (!job.Preload || Volatile.Read(ref job.Taken) != 0) return;
            job.Preload = false;
            _preloadsPending--;
            _urgent.Enqueue(job);
            WakeLocked(all: false);
        }

        /// <summary>
        /// Decodes the project's sounds ahead of time, smallest file first, until
        /// <paramref name="budgetBytes"/> of samples are held. The list is made on a worker too.
        /// </summary>
        public void StartBudget(Func<IReadOnlyList<XAudioSystem.SoundSource>> listSources, long budgetBytes, Action<string>? report)
        {
            if (budgetBytes <= 0 || listSources == null) return;
            lock (_gate)
            {
                if (_disposed || _budgetList != null || _budgetListing) return;
                _budgetListing = true;
                _budgetBytes = budgetBytes;
                _budgetReport = report;
                _budgetStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            Task.Run(() =>
            {
                IReadOnlyList<XAudioSystem.SoundSource> sources;
                try { sources = listSources(); }
                catch (Exception)
                {
                    // An unreadable project folder preloads nothing; every sound is still decoded when played.
                    sources = Array.Empty<XAudioSystem.SoundSource>();
                }

                string? message;
                lock (_gate)
                {
                    _budgetListing = false;
                    _budgetList = sources;
                    _budgetNext = 0;
                    message = sources.Count == 0 ? FinishBudgetLocked() : null;
                    if (message == null) WakeLocked(all: true);
                }

                if (message != null) _budgetReport?.Invoke(message);
            });
        }

        private void WakeLocked(bool all)
        {
            if (_disposed) return;
            if (!all && _idle > 0)
            {
                Monitor.Pulse(_gate);
                return;
            }

            if (all) Monitor.PulseAll(_gate);
            int wanted = all ? _maxThreads : Math.Min(_maxThreads, _threads + 1);
            while (_threads < wanted)
            {
                _threads++;
                new Thread(Work) { IsBackground = true, Name = "Genesis sound decoder " + _threads }.Start();
            }
        }

        // Open while what is decoded, and what the sounds being decoded are expected to take, is
        // under the budget: several threads must not all start a sound the budget has no room for.
        private bool BudgetOpenLocked() =>
            _budgetList != null && _budgetNext < _budgetList.Count
            && Interlocked.Read(ref _preloadedBytes) + _budgetReserved < _budgetBytes;

        /// <summary>About what a sound's samples take: an Ogg file about ten times its size, a WAV its size.</summary>
        private static long EstimateSamples(XAudioSystem.SoundSource source) =>
            source.File.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? source.Length : source.Length * 10;

        /// <summary>Ends the budget's preload; the line to report, once.</summary>
        private string? FinishBudgetLocked()
        {
            if (_budgetList == null) return null;
            int listed = _budgetList.Count;
            _budgetList = null;
            return $"sounds decoded ahead of time: {_preloadedSounds} of the project's {listed} "
                + $"({Interlocked.Read(ref _preloadedBytes) / (1024.0 * 1024.0):0.0} MB of samples, budget {_budgetBytes / (1024 * 1024)} MB) "
                + $"in {System.Diagnostics.Stopwatch.GetElapsedTime(_budgetStarted).TotalMilliseconds:F0} ms";
        }

        private static Job? TakeLocked(Queue<Job> queue)
        {
            while (queue.Count > 0)
            {
                Job job = queue.Dequeue();
                if (Interlocked.CompareExchange(ref job.Taken, 1, 0) == 0) return job;
            }

            return null;
        }

        private Job? NextLocked()
        {
            while (true)
            {
                Job? job = TakeLocked(_urgent) ?? TakeLocked(_ahead);
                if (job != null) return job;
                if (!BudgetOpenLocked()) return null;
                XAudioSystem.SoundSource source = _budgetList![_budgetNext++];
                if (_jobs.ContainsKey(source.Key)) continue;
                job = new Job(source.Key, source.File, source.Settings, preload: true) { Taken = 1, Reserved = EstimateSamples(source) };
                _jobs[source.Key] = job;
                _preloadsPending++;
                _budgetReserved += job.Reserved;
                return job;
            }
        }

        private void Work()
        {
            while (true)
            {
                Job? job;
                string? report = null;
                lock (_gate)
                {
                    while ((job = _disposed ? null : NextLocked()) == null)
                    {
                        if (_disposed) return;
                        if (_budgetList != null && _preloadsPending == 0 && (report = FinishBudgetLocked()) != null) break;
                        _idle++;
                        Monitor.Wait(_gate);
                        _idle--;
                    }
                }

                if (report != null)
                {
                    _budgetReport?.Invoke(report);
                    continue;
                }

                SoundEffect? effect = null;
                try { effect = SoundEffect.FromWavFile(job!.File, job.Region); }
                catch (Exception)
                {
                    // A file the decoder cannot read is a sound that does not play. Nothing may
                    // escape a worker thread: it would end the game.
                }

                lock (_gate)
                {
                    _budgetReserved -= job!.Reserved;
                    if (job.Preload)
                    {
                        _preloadsPending--;
                        if (effect != null)
                        {
                            Interlocked.Add(ref _preloadedBytes, effect.SampleBytes);
                            _preloadedSounds++;
                        }
                    }
                }

                job.Done.TrySetResult(effect);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                Monitor.PulseAll(_gate);
                while (TakeLocked(_urgent) is Job urgent) urgent.Done.TrySetResult(null);
                while (TakeLocked(_ahead) is Job ahead) ahead.Done.TrySetResult(null);
                _budgetList = null;
            }
        }
    }
}
