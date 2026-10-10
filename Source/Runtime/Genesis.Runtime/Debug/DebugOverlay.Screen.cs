using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Genesis.Runtime.Diagnostics;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Diagnostics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// The debug screen's layout: a one-line strip of the figures that matter most (always shown
    /// while the overlay is open), and a full panel (F7) with tabs for Overview, Resources, Game,
    /// Engine (developer builds only), World and AI &amp; Navigation. F8 or the Record button records
    /// a profile into the project's debug folder.
    /// </summary>
    public sealed partial class DebugOverlay
    {
        public const Key PanelKey = Key.F7;
        public const Key RecordKey = Key.F8;

        /// <summary>Measurements behind the strip, the Overview tab and recordings.</summary>
        public RuntimeFrameProfiler Profiler { get; } = new();

        /// <summary>True when the Engine tab and the Engine columns of a recording are available.</summary>
        public bool EngineCategoryEnabled
        {
            get => Profiler.EngineEnabled;
            set
            {
                Profiler.EngineEnabled = value;
                if (!value && ActivePanel == DebugHudPanel.Engine) ActivePanel = DebugHudPanel.Overview;
            }
        }

        /// <summary>Where recordings are written: a folder per session is made inside it.</summary>
        public string ProfilesDirectory { get; set; }

        /// <summary>The project named in a recording's report.</summary>
        public string ProjectName { get; set; } = string.Empty;

        public bool IsRecording => Profiler.IsRecording;

        /// <summary>The folder of the last recording that finished, or null.</summary>
        public string LastRecordingFolder { get; private set; }

        /// <summary>Raised with the session folder when a recording starts.</summary>
        public event Action<string> RecordingStarted;

        /// <summary>Raised with the session folder once a recording's summary and report are written.</summary>
        public event Action<string> RecordingSaved;

        /// <summary>The Resources tab's search text.</summary>
        public string ResourceSearch { get; set; } = string.Empty;

        /// <summary>True while typed keys go to the Resources search box.</summary>
        public bool IsSearchFocused { get; set; }

        public DebugResourceSort ResourceSort { get; set; } = DebugResourceSort.Size;
        public bool ResourceSortDescending { get; set; } = true;

        private long _lastObserveStamp;
        private bool _pgslEnabledByOverlay;
        private long _stripRefreshStamp;
        private string[] _stripTexts = Array.Empty<string>();
        private string[] _measuredStripTexts;
        private float[] _stripWidths = Array.Empty<float>();
        private long _recordingNoticeUntil;
        private Vector4[] _stripColors = Array.Empty<Vector4>();
        private long _resourceRefreshStamp;
        private List<DebugResourceRow> _resourceRows = new();
        private List<DebugResourceRow> _resourceView = new();
        private string _resourceViewQuery;
        private DebugResourceSort _resourceViewSort;
        private bool _resourceViewDescending;
        private int _resourceScroll;
        private long _gameRefreshStamp;
        private IReadOnlyList<PgslEventProfile> _gameEvents = Array.Empty<PgslEventProfile>();
        private Dictionary<string, int> _gameInstances = new(StringComparer.Ordinal);

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        // ── Host hooks ─────────────────────────────────────────────────────────

        /// <summary>
        /// Called by the host once per presented frame. With the overlay closed and nothing
        /// recording this reads one timestamp and returns.
        /// </summary>
        public void ObserveFrame(double simulation, double collect, double draw, double present, double overlay, IRenderController renderer)
        {
            long now = Stopwatch.GetTimestamp();
            double frameMs = _lastObserveStamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastObserveStamp, now).TotalMilliseconds;
            _lastObserveStamp = now;

            bool active = IsVisible || Profiler.IsRecording;
            Profiler.LiveSampling = IsVisible;
            UpdatePgslProfiling(active);
            if (!active)
            {
                Profiler.Idle();
                return;
            }

            var timings = new DebugHostTimings
            {
                FrameMilliseconds = frameMs,
                SimulationMilliseconds = simulation,
                CollectMilliseconds = collect,
                DrawMilliseconds = draw,
                PresentMilliseconds = present,
                OverlayMilliseconds = overlay,
            };
            string parts = Profiler.EngineEnabled && Profiler.IsRecording ? _activeScene?.WorkTimes.Describe(0.5, 6) : null;
            Profiler.Observe(timings, renderer, parts);
        }

        private void UpdatePgslProfiling(bool active)
        {
            if (active && !PgslProfiler.Enabled)
            {
                PgslProfiler.Enabled = true;
                _pgslEnabledByOverlay = true;
            }
            else if (!active && _pgslEnabledByOverlay)
            {
                PgslProfiler.Enabled = false;
                _pgslEnabledByOverlay = false;
            }
        }

        /// <summary>Starts a recording. False (with a log line) when there is no folder to write to.</summary>
        public bool StartRecording()
        {
            if (Profiler.IsRecording) return true;
            if (string.IsNullOrWhiteSpace(ProfilesDirectory))
            {
                Log("[WARN] Recording needs a project debug folder.");
                return false;
            }

            try
            {
                bool pgslWasEnabled = PgslProfiler.Enabled;
                DebugProfileRecording recording = Profiler.StartRecording(ProfilesDirectory, ProjectName, RoomName);
                // Recording switched the PGSL profiler on; hand it back off afterwards if it was off.
                if (!pgslWasEnabled) _pgslEnabledByOverlay = true;
                Log($"[INFO] Recording profile to {recording.Folder}");
                Console.WriteLine("GENESIS_PROFILE_RECORDING " + recording.Folder);
                RecordingStarted?.Invoke(recording.Folder);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Log("[ERROR] Could not start recording: " + exception.Message);
                return false;
            }
        }

        /// <summary>Stops the recording and writes its summary and report. Returns the session folder.</summary>
        public string StopRecording()
        {
            if (!Profiler.IsRecording) return null;
            try
            {
                string folder = Profiler.StopRecording();
                LastRecordingFolder = folder;
                _recordingNoticeUntil = Environment.TickCount64 + 6000;
                Log($"[INFO] Profile saved: {folder}");
                Console.WriteLine("GENESIS_PROFILE_SAVED " + folder);
                RecordingSaved?.Invoke(folder);
                return folder;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                // The rows already in frames.csv stay; only the summary and report are missing.
                Log("[ERROR] Could not finish recording: " + exception.Message);
                return null;
            }
        }

        public void ToggleRecording()
        {
            if (Profiler.IsRecording) StopRecording();
            else StartRecording();
        }

        private IReadOnlyDictionary<string, int> CountInstances()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            ScriptHostSystem host = _scriptHost;
            if (host == null) return counts;
            foreach (EntityBehavior behavior in host.Instances)
            {
                if (behavior == null || !behavior.IsAlive) continue;
                string name = behavior is PgslBehavior pgsl && !string.IsNullOrEmpty(pgsl.ScriptName)
                    ? pgsl.ScriptName
                    : behavior.GetType().Name;
                counts.TryGetValue(name, out int count);
                counts[name] = count + 1;
            }

            return counts;
        }

        /// <summary>Typing into the Resources search box. Returns true when it took the keyboard.</summary>
        private bool HandleSearchInput(InputState input)
        {
            if (!IsSearchFocused || !ShowExpandedPanels || ActivePanel != DebugHudPanel.Resources) return false;
            string typed = input.TakeTypedText();
            foreach (char character in typed)
                if (!char.IsControl(character) && ResourceSearch.Length < 64) ResourceSearch += character;
            if (input.WasPressed(Key.Backspace) && ResourceSearch.Length > 0)
                ResourceSearch = ResourceSearch[..^1];
            if (input.WasPressed(Key.Delete)) ResourceSearch = string.Empty;
            if (input.WasPressed(Key.Enter)) IsSearchFocused = false;
            // The letters are search text: the game does not also walk or jump with them.
            input.SuppressKeyboard();
            return true;
        }

        // ── Compact strip ──────────────────────────────────────────────────────

        /// <summary>The strip's figures as text, in order; also what a test reads.</summary>
        public IReadOnlyList<string> CompactStripTexts
        {
            get
            {
                RefreshStrip(force: true);
                return _stripTexts;
            }
        }

        private void RefreshStrip(bool force = false)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && _stripRefreshStamp != 0 && Stopwatch.GetElapsedTime(_stripRefreshStamp, now).TotalMilliseconds < 250) return;
            _stripRefreshStamp = now;

            DebugFrameSample last = Profiler.Last;
            double frameMs = Profiler.RecentFrameMilliseconds();
            if (frameMs <= 0) frameMs = _frameGraph.CurrentMs;
            double fps = frameMs > 0 ? 1000d / frameMs : _frameGraph.SmoothedFps;
            RenderStats stats = _renderer?.GetStats() ?? default;
            double gpu = stats.GpuMs > 0.001 ? stats.GpuMs : last.GpuMilliseconds;
            long workingSet = Profiler.WorkingSetBytes;
            long privateBytes = Profiler.PrivateBytes;
            if (workingSet == 0) RuntimeFrameProfiler.TryReadProcessMemory(out workingSet, out _, out privateBytes);
            long heap = Profiler.ManagedHeapBytes > 0 ? Profiler.ManagedHeapBytes : GC.GetTotalMemory(false);

            _stripTexts = new[]
            {
                "DEBUG F6",
                $"FPS {fps:0.0}",
                $"Frame {frameMs:0.00} ms",
                $"CPU {last.CpuMilliseconds:0.00} ms",
                gpu > 0.001 ? $"GPU {gpu:0.00} ms" : "GPU —",
                $"RAM {workingSet / 1048576d:0} MB · priv {privateBytes / 1048576d:0} MB",
                $"Heap {heap / 1048576d:0.0} MB",
                Profiler.VideoMemoryBytes > 0 ? $"VRAM {Profiler.VideoMemoryBytes / 1048576d:0} MB" : "VRAM n/a",
            };
            Vector4 frameColor = frameMs <= 16.7 ? DebugOverlayPalette.Success
                : frameMs <= 33.4 ? DebugOverlayPalette.Warning : DebugOverlayPalette.Error;
            _stripColors = new[]
            {
                DebugOverlayPalette.Accent, frameColor, frameColor, DebugOverlayPalette.Text, DebugOverlayPalette.Text,
                DebugOverlayPalette.Text, DebugOverlayPalette.Text, DebugOverlayPalette.Muted,
            };
        }

        /// <summary>Draws the strip and returns its bottom edge.</summary>
        private float DrawCompactStrip(IHudCanvas hud, int width, float top)
        {
            RefreshStrip();
            const float height = 26f;
            const float textSize = 10.5f;
            const float gap = 14f;
            float x = 8f;
            float y = top;
            float right = width - 8f;
            float rowStart = x;

            // Text widths are measured when the figures change (four times a second), not every frame.
            if (!ReferenceEquals(_measuredStripTexts, _stripTexts))
            {
                _measuredStripTexts = _stripTexts;
                _stripWidths = new float[_stripTexts.Length];
                for (int index = 0; index < _stripTexts.Length; index++)
                    _stripWidths[index] = Math.Max(24f, hud.MeasureText(_stripTexts[index], textSize).X);
            }

            // Lay the figures out left to right, wrapping to a second row on a narrow window.
            var placed = new List<(string Text, Vector4 Color, float X, float Y)>(_stripTexts.Length);
            float cursor = rowStart + 8f;
            float rowY = y;
            float widest = 0f;
            const float buttonsWidth = 196f;
            for (int index = 0; index < _stripTexts.Length; index++)
            {
                float textWidth = _stripWidths[index];
                if (cursor + textWidth > right - buttonsWidth && cursor > rowStart + 8f)
                {
                    widest = Math.Max(widest, cursor);
                    cursor = rowStart + 8f;
                    rowY += height - 4f;
                }

                placed.Add((_stripTexts[index], _stripColors[index], cursor, rowY));
                cursor += textWidth + gap;
            }

            widest = Math.Max(widest, cursor);
            float stripWidth = Math.Min(right - x, widest - x + buttonsWidth);
            float stripHeight = rowY - y + height;
            hud.Rect(x, y, stripWidth, stripHeight, DebugOverlayPalette.Canvas, filled: true);
            hud.Rect(x, y, stripWidth, stripHeight, DebugOverlayPalette.Border, filled: false);
            foreach ((string text, Vector4 color, float textX, float textY) in placed)
                hud.Text(text, textX, textY + 6f, textSize, color);

            float buttonX = x + stripWidth - buttonsWidth + 6f;
            string recordLabel = IsRecording ? $"■ REC {Profiler.Recording.Elapsed:m\\:ss}" : "● Record (F8)";
            if (DrawButton(hud, buttonX, y + 3f, 100f, 20f, recordLabel, IsRecording, DebugOverlayPalette.Error))
                ToggleRecording();
            if (DrawButton(hud, buttonX + 104f, y + 3f, 82f, 20f, ShowExpandedPanels ? "Close (F7)" : "Panel (F7)",
                    ShowExpandedPanels, DebugOverlayPalette.Accent))
                ShowExpandedPanels = !ShowExpandedPanels;

            float bottom = y + stripHeight;
            // For a few seconds after a recording stops, say where it went.
            if (!IsRecording && LastRecordingFolder != null && Environment.TickCount64 < _recordingNoticeUntil)
            {
                string notice = "Profile saved: " + Path.Combine(LastRecordingFolder, DebugProfileRecording.ReportFileName);
                hud.Rect(x, bottom + 2f, Math.Min(right - x, hud.MeasureText(notice, 9.5f).X + 16f), 20f, DebugOverlayPalette.Canvas, filled: true);
                hud.Text(notice, x + 8f, bottom + 6f, 9.5f, DebugOverlayPalette.Success);
                bottom += 22f;
            }

            return bottom;
        }

        // ── Full panel ─────────────────────────────────────────────────────────

        /// <summary>The tabs the full panel shows, in order. Engine only in developer builds.</summary>
        public IReadOnlyList<DebugHudPanel> AvailablePanels
        {
            get
            {
                var panels = new List<DebugHudPanel> { DebugHudPanel.Overview, DebugHudPanel.Resources, DebugHudPanel.Game };
                if (EngineCategoryEnabled) panels.Add(DebugHudPanel.Engine);
                panels.Add(DebugHudPanel.World);
                panels.Add(DebugHudPanel.AiNavigation);
                return panels;
            }
        }

        private void DrawFullPanel(IHudCanvas hud, int width, int height, float top)
        {
            float panelW = Math.Min(width - 16f, Math.Clamp(width * 0.52f, 560f, 820f));
            float panelX = width - panelW - 8f;
            float panelY = top + 6f;
            float panelH = Math.Max(200f, height - panelY - 8f);
            hud.Rect(panelX, panelY, panelW, panelH, DebugOverlayPalette.Surface, filled: true);
            hud.Rect(panelX, panelY, panelW, panelH, DebugOverlayPalette.Border, filled: false);

            float tabX = panelX + 8f;
            foreach (DebugHudPanel panel in AvailablePanels)
            {
                string label = PanelName(panel);
                float tabW = Math.Max(70f, hud.MeasureText(label, 10f).X + 18f);
                if (DrawButton(hud, tabX, panelY + 8f, tabW, 24f, label, ActivePanel == panel, DebugOverlayPalette.Accent))
                    SelectPanel(panel);
                tabX += tabW + 4f;
            }

            hud.Line(panelX + 6f, panelY + 38f, panelX + panelW - 6f, panelY + 38f, DebugOverlayPalette.Border, 1f);
            hud.Text("F6 hide · F7 panel · F8 record · P pause · N step", panelX + 12f, panelY + panelH - 18f, 9f, DebugOverlayPalette.Muted);

            float contentX = panelX + 12f;
            float contentY = panelY + 48f;
            float contentW = panelW - 24f;
            float contentH = panelY + panelH - contentY - 26f;
            switch (ActivePanel)
            {
                case DebugHudPanel.Resources:
                    DrawResourcesTab(hud, contentX, contentY, contentW, contentH);
                    break;
                case DebugHudPanel.Game:
                    DrawGameTab(hud, contentX, contentY, contentW, contentH);
                    break;
                case DebugHudPanel.Engine when EngineCategoryEnabled:
                    DrawEngineTab(hud, contentX, contentY, contentW, contentH, width, height);
                    break;
                case DebugHudPanel.World:
                    DrawWorldTab(hud, contentX, contentY, contentW, contentH);
                    break;
                case DebugHudPanel.AiNavigation:
                    DrawAiNavigationPanel(hud, width, height);
                    break;
                default:
                    DrawOverviewTab(hud, contentX, contentY, contentW, contentH);
                    break;
            }
        }

        private void DrawCard(IHudCanvas hud, float x, float y, float w, string label, string value, Vector4? valueColor = null)
        {
            hud.Rect(x, y, w, 40f, DebugOverlayPalette.Raised, filled: true);
            hud.Text(label, x + 6f, y + 4f, 9f, DebugOverlayPalette.Muted);
            hud.Text(value, x + 6f, y + 19f, 12.5f, valueColor ?? DebugOverlayPalette.Text);
        }

        private void DrawOverviewTab(IHudCanvas hud, float x, float y, float w, float h)
        {
            float bottom = y + h;
            RuntimeFrameProfiler p = Profiler;
            double average = p.AverageFrameMilliseconds();
            float cardW = (w - 12f) / 4f;
            DrawCard(hud, x, y, cardW, "FPS (avg / 1% low)", $"{(average > 0 ? 1000d / average : 0):0.0} / {p.OnePercentLowFps():0.0}");
            DrawCard(hud, x + (cardW + 4f), y, cardW, "Process CPU (all cores)", $"{p.ProcessCpuPercent:0.0} %");
            DrawCard(hud, x + (cardW + 4f) * 2, y, cardW, "CPU game thread", $"{p.Last.CpuMilliseconds:0.00} ms");
            RenderStats stats = _renderer?.GetStats() ?? default;
            DrawCard(hud, x + (cardW + 4f) * 3, y, cardW, "GPU frame", stats.GpuMs > 0.001 ? $"{stats.GpuMs:0.00} ms" : "not measured");
            y += 48f;

            hud.Text("FRAME TIME (last 600 frames)", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            hud.Text($"avg {average:0.00}   p50 {p.FramePercentile(50):0.00}   p90 {p.FramePercentile(90):0.00}   "
                + $"p95 {p.FramePercentile(95):0.00}   p99 {p.FramePercentile(99):0.00}   max {p.FramePercentile(100):0.00} ms",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 24f;

            hud.Text("MEMORY", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            const double mb = 1048576d;
            hud.Text($"Working set {p.WorkingSetBytes / mb:0.0} MB (peak {p.PeakWorkingSetBytes / mb:0.0})   Private {p.PrivateBytes / mb:0.0} MB",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 16f;
            string videoMemory = p.VideoMemoryBytes > 0
                ? $"VRAM {p.VideoMemoryBytes / mb:0.0} MB of {p.VideoMemoryBudgetBytes / mb:0} MB budget"
                : "VRAM n/a (not reported by the driver)";
            hud.Text($"Managed heap {p.ManagedHeapBytes / mb:0.0} MB   GC committed {p.CommittedBytes / mb:0.0} MB   {videoMemory}",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 24f;

            hud.Text("GARBAGE COLLECTION", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            hud.Text($"Collections gen0 {GC.CollectionCount(0)}   gen1 {GC.CollectionCount(1)}   gen2 {GC.CollectionCount(2)}   "
                + $"Allocation {p.AllocationMegabytesPerSecond:0.00} MB/s   Pause time {p.GcPauseTimePercent:0.00} %",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 16f;
            RuntimeGcRecord? lastGc = p.LastCollection;
            hud.Text(lastGc is { } gc
                    ? $"Last collection: gen{gc.Generation} ({gc.Reason}) paused {gc.PauseMilliseconds:0.000} ms at {gc.TimestampUtc.ToLocalTime():HH:mm:ss}"
                    : "Last collection: none since the debug screen opened",
                x, y, 10f, DebugOverlayPalette.Muted);
            y += 24f;

            hud.Text("PROCESS", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            hud.Text($"Threads {(p.ThreadCount > 0 ? p.ThreadCount.ToString(Invariant) : "…")}   Thread pool {System.Threading.ThreadPool.ThreadCount}   "
                + $"Handles {p.HandleCount}   Cores {Environment.ProcessorCount}   Debug screen open for {TimeSpan.FromMilliseconds(Environment.TickCount64 - _startTicks):hh\\:mm\\:ss}",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 24f;

            hud.Text("RECORDING", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            if (IsRecording)
                hud.Text($"Recording {Profiler.Recording.FrameCount} frames to {Profiler.Recording.Folder}", x, y, 9.5f, DebugOverlayPalette.Error);
            else if (!string.IsNullOrEmpty(LastRecordingFolder))
                hud.Text($"Last profile: {LastRecordingFolder}", x, y, 9.5f, DebugOverlayPalette.Muted);
            else
                hud.Text("Not recording. Press F8 or ● Record to capture frames.csv, summary.json and report.md.", x, y, 9.5f, DebugOverlayPalette.Muted);
            y += 20f;

            _frameGraph.Draw(hud, x, y, w, Math.Clamp(bottom - y - 4f, 40f, 140f));
        }

        private void RefreshResources(bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && _resourceRefreshStamp != 0 && Stopwatch.GetElapsedTime(_resourceRefreshStamp, now).TotalMilliseconds < 500)
                return;
            _resourceRefreshStamp = now;
            _resourceRows = CollectResourceRows();
            _resourceViewQuery = null;
        }

        /// <summary>Every resource the Resources tab lists, before search and sort.</summary>
        public List<DebugResourceRow> CollectResourceRows()
        {
            List<DebugResourceRow> rows = DebugResourceCatalog.Collect();
            IReadOnlyDictionary<string, int> instances = CountInstances();
            foreach (KeyValuePair<string, int> pair in instances)
                rows.Add(new DebugResourceRow("Object", pair.Key, pair.Value, 0, "live instances"));

            ScriptHostSystem host = _scriptHost;
            if (host != null)
            {
                int listed = 0;
                foreach (EntityBehavior behavior in host.Instances)
                {
                    if (behavior == null || !behavior.IsAlive) continue;
                    if (++listed > 2000) break;
                    string name = behavior is PgslBehavior pgsl ? pgsl.ScriptName : behavior.GetType().Name;
                    rows.Add(new DebugResourceRow("Instance", $"{name} #{behavior.Entity.Id}", 1, 0, name));
                }
            }

            if (_activeScene != null)
            {
                int emitters = 0, particles = 0;
                foreach (ISceneSubsystem subsystem in _activeScene.Subsystems)
                {
                    if (subsystem is ObjectCompositionSubsystem composition)
                    {
                        emitters += composition.ParticleEmitterCount;
                        particles += composition.ActiveParticleCount;
                    }
                }

                if (emitters > 0 || particles > 0)
                {
                    rows.Add(new DebugResourceRow("Particles", "Emitters", emitters, 0, "particle systems"));
                    rows.Add(new DebugResourceRow("Particles", "Live particles", particles, 0, "particles alive now"));
                }

                rows.Add(new DebugResourceRow("Instance", "All entities", _activeScene.World?.LivingEntityCount ?? 0, 0, "entities in the room"));
            }

            return rows;
        }

        /// <summary>The Resources tab's rows after its search and sort, as drawn.</summary>
        public IReadOnlyList<DebugResourceRow> VisibleResourceRows
        {
            get
            {
                if (_resourceViewQuery == null || _resourceViewQuery != ResourceSearch
                    || _resourceViewSort != ResourceSort || _resourceViewDescending != ResourceSortDescending)
                {
                    _resourceView = DebugResourceFilter.Apply(_resourceRows, ResourceSearch, ResourceSort, ResourceSortDescending);
                    _resourceViewQuery = ResourceSearch;
                    _resourceViewSort = ResourceSort;
                    _resourceViewDescending = ResourceSortDescending;
                }

                return _resourceView;
            }
        }

        /// <summary>Re-reads every resource now (the tab otherwise refreshes twice a second).</summary>
        public void RefreshResourcesNow() => RefreshResources(force: true);

        private void DrawResourcesTab(IHudCanvas hud, float x, float y, float w, float h)
        {
            float bottom = y + h;
            RefreshResources(force: false);

            // Search box: click to type, Enter to finish, Delete to clear.
            float boxW = Math.Min(w - 290f, 340f);
            bool hovered = IsHovered(x, y, boxW, 24f);
            if (hovered && _mouseClicked && !_clickConsumed)
            {
                _clickConsumed = true;
                IsSearchFocused = true;
            }
            else if (_mouseClicked && !_clickConsumed && !hovered)
            {
                IsSearchFocused = false;
            }

            hud.Rect(x, y, boxW, 24f, DebugOverlayPalette.Canvas, filled: true);
            hud.Rect(x, y, boxW, 24f, IsSearchFocused ? DebugOverlayPalette.Accent : DebugOverlayPalette.Border, filled: false);
            string shown = ResourceSearch.Length == 0 && !IsSearchFocused
                ? "Search (click and type): grass, kind:texture, >1mb"
                : "⌕ " + ResourceSearch + (IsSearchFocused && (Environment.TickCount64 / 500) % 2 == 0 ? "|" : string.Empty);
            hud.Text(shown, x + 6f, y + 6f, 10f, ResourceSearch.Length == 0 && !IsSearchFocused ? DebugOverlayPalette.Muted : DebugOverlayPalette.Text);

            float sortX = x + boxW + 8f;
            hud.Text("Sort", sortX, y + 6f, 9.5f, DebugOverlayPalette.Muted);
            sortX += 30f;
            foreach (DebugResourceSort sort in new[] { DebugResourceSort.Size, DebugResourceSort.Count, DebugResourceSort.Name, DebugResourceSort.Kind })
            {
                bool active = ResourceSort == sort;
                string label = sort + (active ? (ResourceSortDescending ? " ▼" : " ▲") : string.Empty);
                if (DrawButton(hud, sortX, y, 58f, 24f, label, active, DebugOverlayPalette.Accent))
                {
                    if (active) ResourceSortDescending = !ResourceSortDescending;
                    else
                    {
                        ResourceSort = sort;
                        ResourceSortDescending = sort is DebugResourceSort.Size or DebugResourceSort.Count;
                    }
                }

                sortX += 62f;
            }

            y += 32f;
            IReadOnlyList<DebugResourceRow> rows = VisibleResourceRows;
            string totals = string.Join("   ", _resourceRows
                .Where(row => row.Kind != "Instance" || row.Name == "All entities")
                .GroupBy(row => row.Kind)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    long bytes = group.Sum(row => row.Bytes);
                    long count = group.Key is "Particles" or "Instance" ? group.Max(row => row.Count) : group.Count();
                    string kinds = group.Key.EndsWith('s') ? group.Key : group.Key + "s";
                    return bytes > 0 ? $"{kinds} {count} ({DebugResourceFilter.FormatBytes(bytes)})" : $"{kinds} {count}";
                }));
            hud.Text(totals.Length == 0 ? "No resources reported yet." : totals, x, y, 9.5f, DebugOverlayPalette.Muted);
            y += 16f;
            hud.Text($"{rows.Count} of {_resourceRows.Count} shown", x, y, 9.5f, DebugOverlayPalette.Muted);
            y += 18f;

            float countX = x + w - 150f;
            float sizeX = x + w - 74f;
            hud.Text("KIND", x, y, 9f, DebugOverlayPalette.Accent);
            hud.Text("NAME", x + 80f, y, 9f, DebugOverlayPalette.Accent);
            hud.Text("COUNT", countX, y, 9f, DebugOverlayPalette.Accent);
            hud.Text("SIZE", sizeX, y, 9f, DebugOverlayPalette.Accent);
            y += 16f;

            const float rowH = 17f;
            float listH = Math.Max(rowH, bottom - y);
            int visible = Math.Max(1, (int)(listH / rowH));
            int maxScroll = Math.Max(0, rows.Count - visible);
            if (IsHovered(x, y, w, listH) && Math.Abs(_mouseWheel) > .01f)
                _resourceScroll = Math.Clamp(_resourceScroll - Math.Sign(_mouseWheel) * 3, 0, maxScroll);
            _resourceScroll = Math.Clamp(_resourceScroll, 0, maxScroll);
            int nameChars = Math.Max(12, (int)((countX - x - 90f) / 6.2f));
            for (int index = _resourceScroll; index < rows.Count && index < _resourceScroll + visible; index++)
            {
                DebugResourceRow row = rows[index];
                if ((index - _resourceScroll) % 2 == 0)
                    hud.Rect(x - 4f, y - 2f, w + 8f, rowH, DebugOverlayPalette.Raised, filled: true);
                hud.Text(row.Kind, x, y, 9.5f, DebugOverlayPalette.Muted);
                string label = string.IsNullOrEmpty(row.Detail) || row.Detail == row.Name ? row.Name : $"{row.Name}  ·  {row.Detail}";
                hud.Text(Truncate(label, nameChars), x + 80f, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(row.Count.ToString("N0", Invariant), countX, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(row.SizeText, sizeX, y, 9.5f, DebugOverlayPalette.Text);
                y += rowH;
            }

            if (rows.Count == 0)
                hud.Text("Nothing matches the search.", x, y + 4f, 10f, DebugOverlayPalette.Muted);
        }

        private static string Truncate(string text, int characters) =>
            string.IsNullOrEmpty(text) || text.Length <= characters ? text ?? string.Empty : text[..Math.Max(1, characters - 1)] + "…";

        private void RefreshGame(bool force)
        {
            long now = Stopwatch.GetTimestamp();
            if (!force && _gameRefreshStamp != 0 && Stopwatch.GetElapsedTime(_gameRefreshStamp, now).TotalMilliseconds < 500) return;
            _gameRefreshStamp = now;
            _gameEvents = PgslProfiler.Snapshot();
            _gameInstances = new Dictionary<string, int>(CountInstances(), StringComparer.Ordinal);
        }

        private void DrawGameTab(IHudCanvas hud, float x, float y, float w, float h)
        {
            RefreshGame(force: _gameRefreshStamp == 0);
            DebugFrameSample last = Profiler.Last;
            int instanceTotal = _gameInstances.Values.Sum();
            float cardW = (w - 12f) / 4f;
            DrawCard(hud, x, y, cardW, "PGSL this frame", $"{last.PgslMilliseconds:0.000} ms");
            DrawCard(hud, x + cardW + 4f, y, cardW, "Event calls / frame", last.PgslCalls.ToString("N0", Invariant));
            DrawCard(hud, x + (cardW + 4f) * 2, y, cardW, "Objects / instances", $"{_gameInstances.Count} / {instanceTotal}");
            DrawCard(hud, x + (cardW + 4f) * 3, y, cardW, "Script errors", _scriptDiagnostics.Count.ToString(Invariant),
                _scriptDiagnostics.Count > 0 ? DebugOverlayPalette.Error : DebugOverlayPalette.Success);
            y += 50f;

            // Objects: each object's events added together, with its live instances.
            hud.Text("PGSL OBJECTS", x, y, 10.5f, DebugOverlayPalette.Accent);
            y += 17f;
            float c1 = x + w * 0.42f, c2 = c1 + 56f, c3 = c2 + 64f, c4 = c3 + 70f, c5 = c4 + 66f;
            hud.Text("OBJECT", x, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("INST", c1, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("CALLS", c2, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("TOTAL ms", c3, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("MAX µs", c4, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("ERR", c5, y, 9f, DebugOverlayPalette.Muted);
            y += 15f;
            var objects = _gameEvents
                .GroupBy(profile => profile.ObjectName, StringComparer.Ordinal)
                .Select(group => (Name: group.Key, Calls: group.Sum(item => item.CallCount), Total: group.Sum(item => item.TotalMicroseconds),
                    Max: group.Max(item => item.MaximumMicroseconds), Failed: group.Sum(item => item.FailedCalls)))
                .Concat(_gameInstances.Keys.Where(name => _gameEvents.All(profile => profile.ObjectName != name))
                    .Select(name => (Name: name, Calls: 0L, Total: 0d, Max: 0d, Failed: 0L)))
                .OrderByDescending(item => item.Total)
                .Take(8)
                .ToList();
            if (objects.Count == 0) hud.Text("No PGSL object has run yet.", x, y, 9.5f, DebugOverlayPalette.Muted);
            foreach (var item in objects)
            {
                _gameInstances.TryGetValue(item.Name, out int instances);
                hud.Text(Truncate(item.Name, 34), x, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(instances.ToString(Invariant), c1, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(item.Calls.ToString("N0", Invariant), c2, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text((item.Total / 1000d).ToString("0.000", Invariant), c3, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(item.Max.ToString("0.0", Invariant), c4, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(item.Failed.ToString(Invariant), c5, y, 9.5f, item.Failed > 0 ? DebugOverlayPalette.Error : DebugOverlayPalette.Muted);
                y += 15f;
            }

            y += 10f;
            hud.Text("PGSL EVENTS", x, y, 10.5f, DebugOverlayPalette.Accent);
            y += 17f;
            hud.Text("OBJECT.EVENT", x, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("CALLS", c2, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("AVG µs", c3, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("MAX µs", c4, y, 9f, DebugOverlayPalette.Muted);
            hud.Text("TOTAL ms", c5, y, 9f, DebugOverlayPalette.Muted);
            y += 15f;
            if (_gameEvents.Count == 0) hud.Text("No PGSL event has run yet.", x, y, 9.5f, DebugOverlayPalette.Muted);
            foreach (PgslEventProfile profile in _gameEvents.Take(10))
            {
                hud.Text(Truncate($"{profile.ObjectName}.{profile.EventName}", 46), x, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(profile.CallCount.ToString("N0", Invariant), c2, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(profile.AverageMicroseconds.ToString("0.0", Invariant), c3, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text(profile.MaximumMicroseconds.ToString("0.0", Invariant), c4, y, 9.5f, DebugOverlayPalette.Text);
                hud.Text((profile.TotalMicroseconds / 1000d).ToString("0.000", Invariant), c5, y, 9.5f, DebugOverlayPalette.Text);
                y += 15f;
            }

            y += 10f;
            hud.Text("SCRIPT ERRORS", x, y, 10.5f, DebugOverlayPalette.Accent);
            y += 17f;
            if (_scriptDiagnostics.Count == 0) hud.Text("None.", x, y, 9.5f, DebugOverlayPalette.Success);
            foreach (ScriptDiagnostic diagnostic in _scriptDiagnostics.Skip(Math.Max(0, _scriptDiagnostics.Count - 3)))
            {
                hud.Text(Truncate(diagnostic.ToDisplayString(), 110), x, y, 9.5f, DebugOverlayPalette.Error);
                y += 15f;
            }
        }

        private void DrawEngineTab(IHudCanvas hud, float x, float y, float w, float h, int width, int height)
        {
            float bottom = y + h;
            RenderStats stats = _renderer?.GetStats() ?? default;
            string adapter = string.IsNullOrWhiteSpace(_renderer?.AdapterName) ? "(no adapter)" : _renderer.AdapterName;
            string vsync = _window == null ? "—" : (_window.VSync ? "on" : "off");
            hud.Text($"GPU device: {adapter} ({_renderer?.BackendName ?? "no renderer"})   VSync {vsync}   Window {width}x{height}",
                x, y, 10f, DebugOverlayPalette.Text);
            y += 20f;

            float cardW = (w - 12f) / 4f;
            DrawCard(hud, x, y, cardW, "Draw 2D / 3D", $"{stats.DrawCalls2D} / {stats.DrawCalls3D}");
            DrawCard(hud, x + cardW + 4f, y, cardW, "Batches", stats.Batches.ToString(Invariant));
            DrawCard(hud, x + (cardW + 4f) * 2, y, cardW, "Triangles", stats.Triangles.ToString("N0", Invariant));
            DrawCard(hud, x + (cardW + 4f) * 3, y, cardW, "WorldMeshes", stats.WorldMeshes.ToString("N0", Invariant));
            y += 48f;

            float half = (w - 12f) / 2f;
            float left = x, right = x + half + 12f;
            float rowY = y;
            hud.Text($"Lights  {stats.LightsUsed} / {stats.LightsCap}", left, rowY, 9.5f, DebugOverlayPalette.Muted);
            DrawUsageBar(hud, left, rowY + 14f, half, stats.LightsUsed / (float)Math.Max(1, stats.LightsCap));
            hud.Text($"Sprites  {stats.SpriteInstances} / {stats.SpriteInstanceCap}", right, rowY, 9.5f, DebugOverlayPalette.Muted);
            DrawUsageBar(hud, right, rowY + 14f, half, stats.SpriteInstances / (float)Math.Max(1, stats.SpriteInstanceCap));
            rowY += 26f;
            hud.Text($"Meshes  {stats.MeshInstances} / {stats.MeshInstanceCap}", left, rowY, 9.5f, DebugOverlayPalette.Muted);
            DrawUsageBar(hud, left, rowY + 14f, half, stats.MeshInstances / (float)Math.Max(1, stats.MeshInstanceCap));
            double foliageUploadMb = stats.FoliageUploadBytes / 1048576d;
            hud.Text($"Foliage  {stats.FoliageInstances:N0} inst / {stats.FoliageBatches:N0} batches / {foliageUploadMb:0.00}MB", right, rowY, 9.5f, DebugOverlayPalette.Muted);
            DrawUsageBar(hud, right, rowY + 14f, half,
                Math.Clamp(stats.FoliageUploadBytes / (Math.Max(1, stats.MeshInstanceCap) * 96f), 0f, 1f));
            y = rowY + 30f;

            hud.Text("RENDER PASSES (GPU)", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            string Pass(string name, double ms) => ms > 0.001 ? $"{name} {ms:0.00} ms" : $"{name} off";
            hud.Text(string.Join("   ", Pass("Frame", stats.GpuMs), Pass("AO", stats.AoMs), Pass("Contact shadows", stats.ContactShadowMs),
                Pass("Local volumetrics", stats.LocalVolumetricMs),
                stats.AntiAliasing == AntiAliasingMode.Off ? "Anti-aliasing off" : "Anti-aliasing " + AntiAliasingModes.Name(stats.AntiAliasing).ToUpperInvariant()),
                x, y, 9.5f, DebugOverlayPalette.Text);
            y += 15f;
            hud.Text(string.Join("   ", Pass("Smoke", stats.SmokeExtinctionMs), Pass("Bloom", stats.BloomMs), Pass("Atmosphere LUT", stats.AtmosphereLutMs),
                Pass("Celestial", stats.CelestialExtrasMs), CloudsStatusLabel(stats, _renderer?.BackendName ?? string.Empty, MeshLightingDefaults.RaymarchedCloudsEnabled)),
                x, y, 9.5f, DebugOverlayPalette.Text);
            y += 15f;
            hud.Text($"Shadow casters {stats.ShadowCasterDraws}   cascades {stats.ShadowCascadesRendered} ({stats.ShadowMapResolution} px)   local shadow lights {stats.LocalShadowLights}   "
                + $"instances drawn {stats.InstancesDrawn} (culled {stats.InstancesCulled}, dropped {stats.InstancesDropped})",
                x, y, 9.5f, DebugOverlayPalette.Muted);
            y += 22f;

            DebugFrameSample last = Profiler.Last;
            hud.Text("FRAME ON THE CPU (ms)", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            hud.Text($"Simulation {last.SimulationMilliseconds:0.00}   Gather {last.CollectMilliseconds:0.00}   Draw {last.DrawMilliseconds:0.00}   "
                + $"Overlay {last.OverlayMilliseconds:0.00}   Present (incl. GPU wait) {last.PresentMilliseconds:0.00}", x, y, 9.5f, DebugOverlayPalette.Text);
            y += 15f;
            string parts = _activeScene?.WorkTimes.Describe(0.05, 6);
            hud.Text("Subsystems: " + (string.IsNullOrEmpty(parts) ? "all under 0.05 ms" : parts), x, y, 9.5f, DebugOverlayPalette.Text);
            y += 22f;

            hud.Text("TEXTURES", x, y, 10f, DebugOverlayPalette.Accent);
            y += 16f;
            if (Genesis.Runtime.Textures.RuntimeTextureAtlas.IsActive)
                hud.Text($"Runtime atlas stitch: active, {Genesis.Runtime.Textures.RuntimeTextureAtlas.AtlasSheetCount} sheet(s), "
                    + $"{Genesis.Runtime.Textures.RuntimeTextureAtlas.MappedSpriteCount} sprites, occupancy "
                    + $"{Genesis.Runtime.Textures.RuntimeTextureAtlas.AverageOccupancyPercent:0.0}%", x, y, 9.5f, DebugOverlayPalette.Success);
            else
                hud.Text("Runtime atlas stitch: idle; images bind as unique textures.", x, y, 9.5f, DebugOverlayPalette.Muted);
            y += 15f;
            hud.Text($"Texture switches (last frame): {stats.TextureSwitches:N0}   uploads {stats.UploadBytes / 1024d:0} KB   "
                + $"created textures {stats.TexturesCreated} buffers {stats.BuffersCreated} pipelines {stats.PipelinesCreated}",
                x, y, 9.5f, DebugOverlayPalette.Text);
            y += 20f;

            _frameGraph.Draw(hud, x, y, w, Math.Clamp(bottom - y - 4f, 30f, 110f));
        }

        private void DrawWorldTab(IHudCanvas hud, float x, float y, float w, float h)
        {
            float bottom = y + h;
            // Transport: pause, step, game speed and inspect.
            float bx = x;
            if (DrawButton(hud, bx, y, 66f, 24f, IsPaused ? "▶ Play" : "❚❚ Pause", IsPaused)) IsPaused = !IsPaused;
            bx += 70f;
            if (DrawButton(hud, bx, y, 60f, 24f, "▶❚ Step", false)) _stepRequested = true;
            bx += 64f;
            foreach (float speed in new[] { 0.5f, 1.0f, 2.0f, 5.0f })
            {
                if (DrawButton(hud, bx, y, 44f, 24f, $"{speed:0.#}x", Math.Abs(TimeScale - speed) < 0.05f))
                {
                    TimeScale = speed;
                    Log($"[INFO] Game speed set to {speed:0.#}x");
                }

                bx += 48f;
            }

            if (DrawButton(hud, bx, y, 84f, 24f, "Inspect", IsInspectModeActive, DebugOverlayPalette.Accent))
                IsInspectModeActive = !IsInspectModeActive;
            y += 30f;

            // View toggles and the debug render pass.
            bx = x;
            if (DrawButton(hud, bx, y, 78f, 24f, ShowBBoxes ? "✓ BBoxes" : "□ BBoxes", ShowBBoxes)) ShowBBoxes = !ShowBBoxes;
            bx += 82f;
            if (DrawButton(hud, bx, y, 92f, 24f, ShowWireframe ? "✓ Wireframe" : "□ Wireframe", ShowWireframe)) ShowWireframe = !ShowWireframe;
            bx += 96f;
            if (DrawButton(hud, bx, y, 82f, 24f, ShowCameras ? "✓ Cameras" : "□ Cameras", ShowCameras)) ShowCameras = !ShowCameras;
            bx += 86f;
            string[] passes = { "Final Color", "Lighting", "Depth", "Normals", "Fog" };
            for (int index = 0; index < passes.Length; index++)
            {
                if (DrawButton(hud, bx, y, 70f, 24f, passes[index], (int)ActiveRenderPass == index))
                {
                    ActiveRenderPass = (DebugRenderPass)index;
                    Log($"[INFO] Render pass changed to: {passes[index]}");
                }

                bx += 72f;
                if (bx + 70f > x + w) break;
            }

            y += 34f;
            DrawLeftInspectorPanel(hud, x - 4f, y, w + 8f, Math.Max(60f, bottom - y));
        }

        private static string PanelName(DebugHudPanel panel) => panel switch
        {
            DebugHudPanel.Resources => "Resources",
            DebugHudPanel.Game => "Game",
            DebugHudPanel.Engine => "Engine",
            DebugHudPanel.World => "World",
            DebugHudPanel.AiNavigation => "AI & Navigation",
            _ => "Overview",
        };
    }
}
