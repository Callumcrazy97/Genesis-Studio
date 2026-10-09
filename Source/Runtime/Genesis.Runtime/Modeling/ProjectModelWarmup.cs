using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// Reads the models of the project's Objects on a background thread while the game starts and
    /// plays, so the first instance of an Object a script creates finds its model read. Creating
    /// one used to read and parse its model on the game's thread inside that frame: 3-14 ms a
    /// model, so a batch of new creatures (or a first trip to the Nether) cost one frame 140-300 ms.
    /// </summary>
    /// <remarks>
    /// The models go into the registry the game draws from (<see cref="RuntimeModelAssetRegistry.Shared"/>),
    /// smallest first, at below-normal priority, without holding the registry while each is parsed.
    /// Only Objects' models up to <see cref="MaximumModelBytes"/> each, and <see cref="MaximumTotalBytes"/>
    /// in all, are read: large scenery is placed in rooms, and rooms already read theirs ahead.
    /// GENESIS_MODEL_WARMUP=0 turns it off.
    /// </remarks>
    public static class ProjectModelWarmup
    {
        /// <summary>Largest saved model (the .gmodel) read ahead.</summary>
        public const long MaximumModelBytes = 1024L * 1024;

        /// <summary>All the models read ahead together, at most.</summary>
        public const long MaximumTotalBytes = 48L * 1024 * 1024;

        /// <summary>How long the warm-up waits for the first room to be shown before it starts anyway.</summary>
        public static readonly TimeSpan FirstRoomWait = TimeSpan.FromSeconds(60);

        private static readonly object Gate = new();
        private static readonly ManualResetEventSlim RoomShown = new(false);
        private static string _startedFor;
        private static string _summary;
        private static Action<string> _report;
        private static int _running;

        /// <summary>True while the background read is still going.</summary>
        public static bool Running => Volatile.Read(ref _running) != 0;

        /// <summary>Models read by the last warm-up.</summary>
        public static int Read { get; private set; }

        /// <summary>Starts reading the Objects' models, once per project in a process.</summary>
        public static void Start(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath) || !RuntimeModelStore.PrefetchEnabled
                || Environment.GetEnvironmentVariable("GENESIS_MODEL_WARMUP") == "0") return;
            string startedFor = Path.GetFullPath(projectPath);
            lock (Gate)
            {
                if (string.Equals(_startedFor, startedFor, StringComparison.OrdinalIgnoreCase) || _running != 0) return;
                _startedFor = startedFor;
                _summary = null;
                _running = 1;
            }

            var thread = new Thread(() => Run(startedFor))
            {
                IsBackground = true,
                Name = "Genesis object model warm-up",
                // Below the game's own threads: this only saves later frames some work.
                Priority = ThreadPriority.BelowNormal,
            };
            thread.Start();
        }

        /// <summary>
        /// Says the game's first room is on screen. The warm-up waits for this (or for
        /// <see cref="FirstRoomWait"/>): parsing models while the room loads made its load slower
        /// (GenesisCraft's Create events took 0.4-1.5 s longer), and nothing it reads is needed
        /// before the game is being played.
        /// </summary>
        public static void FirstRoomShown()
        {
            if (!RoomShown.IsSet) RoomShown.Set();
        }

        /// <summary>Where to write one line saying what the warm-up read, when it is done (at once if it is).</summary>
        public static void ReportTo(Action<string> report)
        {
            string summary;
            lock (Gate)
            {
                _report = report;
                summary = _summary;
            }

            if (summary != null) report?.Invoke(summary);
        }

        /// <summary>Waits until the warm-up is done, or the time is up (tests).</summary>
        public static bool WaitUntilDone(TimeSpan timeout)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (Running)
            {
                if (clock.Elapsed >= timeout) return false;
                Thread.Sleep(5);
            }

            return true;
        }

        /// <summary>Forgets a finished warm-up so another can start (tests).</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                if (_running != 0) throw new InvalidOperationException("A model warm-up is still running.");
                _startedFor = null;
                _summary = null;
                _report = null;
                Read = 0;
            }
        }

        /// <summary>The distinct models the project's Objects name, as their resources name them.</summary>
        public static IReadOnlyList<string> ObjectModels(string projectPath)
        {
            var models = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (NamedResource resource in ResourceCatalog.For(projectPath).Entries)
            {
                if (resource.Type != ResourceType.Object) continue;
                string model = ModelOf(resource.FullPath);
                if (!string.IsNullOrWhiteSpace(model) && seen.Add(model.Trim())) models.Add(model.Trim());
            }

            return models;
        }

        private static string ModelOf(string objectFile)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(objectFile),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string model = null;
                if (root.TryGetProperty("model", out JsonElement top) && top.ValueKind == JsonValueKind.String)
                    model = top.GetString();
                // The renderer component's own model wins, as it does when the Object is placed.
                if (root.TryGetProperty("components", out JsonElement components) && components.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement component in components.EnumerateArray())
                    {
                        if (component.ValueKind != JsonValueKind.Object
                            || !component.TryGetProperty("type", out JsonElement type) || type.ValueKind != JsonValueKind.String) continue;
                        string kind = type.GetString();
                        if (kind is not ("ModelRendererComponent" or "ModelComponent")
                            || !component.TryGetProperty("props", out JsonElement props) || props.ValueKind != JsonValueKind.Object) continue;
                        foreach (string name in new[] { "Model", "ModelAsset" })
                            if (props.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                                && !string.IsNullOrWhiteSpace(value.GetString()))
                                model = value.GetString();
                    }
                }

                return model;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        private static void Run(string projectPath)
        {
            RoomShown.Wait(FirstRoomWait);
            Stopwatch clock = Stopwatch.StartNew();
            int read = 0, skipped = 0, held = 0;
            long total = 0;
            int listed = 0;
            try
            {
                IReadOnlyList<string> models = ObjectModels(projectPath);
                listed = models.Count;
                // Smallest first: the most models (creatures, items) ready soonest.
                var sized = new List<(string Model, long Bytes)>(models.Count);
                foreach (string model in models) sized.Add((model, GeometryBytes(projectPath, model)));
                sized.Sort((a, b) => a.Bytes.CompareTo(b.Bytes));
                foreach ((string model, long size) in sized)
                {
                    if (size <= 0 || size > MaximumModelBytes || total + size > MaximumTotalBytes)
                    {
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (RuntimeModelAssetRegistry.Shared.Warm(projectPath, model, MaximumModelBytes, out long bytes))
                        {
                            read++;
                            total += bytes;
                            // A pause between models spreads the garbage their parsing leaves, so
                            // the game's collections stay small while it is being played.
                            Thread.Sleep(1);
                        }
                        else held++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                        or Newtonsoft.Json.JsonException or InvalidDataException or ArgumentException or NotSupportedException)
                    {
                        // The game's own load reads it, and reports a model it cannot read.
                        held++;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // Listing the Objects failed: every model is read when first used, as before.
            }
            finally
            {
                string summary = $"object models: {read} of the {listed} the project's Objects use read ahead on a worker in "
                    + $"{clock.Elapsed.TotalMilliseconds:F0} ms ({total / (1024.0 * 1024.0):F1} MB; {held} already held or left to the game, "
                    + $"{skipped} over the size limits)";
                Action<string> report;
                lock (Gate)
                {
                    Read = read;
                    _summary = summary;
                    report = _report;
                    _running = 0;
                }

                report?.Invoke(summary);
            }
        }

        // The saved geometry's size: the cooked .gmodel beside a Model resource, or a legacy one.
        private static long GeometryBytes(string projectPath, string model)
        {
            try
            {
                string resource = StudioModelResourceLoader.Resolve(projectPath, model);
                string path = string.IsNullOrWhiteSpace(resource)
                    ? RuntimeModelStore.AssetPath(projectPath, model)
                    : StudioModelResourceLoader.CanonicalPath(resource);
                var file = new FileInfo(path);
                return file.Exists ? file.Length : 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return 0;
            }
        }
    }
}
