using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;

using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Navigation;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Debugger
{
    public enum DebugRenderPass
    {
        FinalColor = 0,
        Lighting = 1,
        Depth = 2,
        Normals = 3,
        Fog = 4,
    }

    public enum DebugHudPanel
    {
        Overview,
        AiNavigation,
        Resources,
        Game,
        /// <summary>Render passes, draw calls and subsystem times; developer builds only.</summary>
        Engine,
        /// <summary>Entities, variables, transport and view toggles.</summary>
        World,
    }

    /// <summary>
    /// Next-generation interactive in-game debugger & live inspection overlay for Genesis Engine.
    /// Provides 100% interactive HUD controls, collapsible entity trees, mouse raycasting ("What Am I Looking At"),
    /// real-time variable tweaking, speed controls, render pass visualizers, and hardware/engine telemetry.
    /// </summary>
    public sealed partial class DebugOverlay : IDisposable
    {
        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                NavigationDebugTelemetry.Enabled = value && ActivePanel == DebugHudPanel.AiNavigation;
                if (!value) NavigationDebugTelemetry.ResumeAi();
            }
        }
        public bool ShowNavigation => IsVisible && ActivePanel == DebugHudPanel.AiNavigation;
        public DebugHudPanel ActivePanel { get; private set; } = DebugHudPanel.Overview;
        public bool IsPaused { get; private set; }
        public float TimeScale { get; set; } = 1.0f;

        /// <summary>
        /// When false (default) F6 shows only the compact strip of figures. When true the full
        /// panel with its tabs is open. F7 or the strip's Panel button toggles it.
        /// </summary>
        public bool ShowExpandedPanels { get; set; } = false;

        // View pass and visual toggles
        public DebugRenderPass ActiveRenderPass { get; set; } = DebugRenderPass.FinalColor;
        public bool ShowBBoxes { get; set; } = true;
        public bool ShowWireframe { get; set; } = false;
        public bool ShowCameras { get; set; } = true;
        private bool _navigationRouteLines = true;
        private bool _navigationOverheadTags = true;
        private bool _navigationCrowdAvoidance = true;
        private bool _navigationNavMeshBounds = true;
        private int _selectedNavigationEntity = -1;
        private int _navigationRosterScroll;
        public bool IsInspectModeActive
        {
            get => _picker.IsInspectModeActive;
            set => _picker.IsInspectModeActive = value;
        }

        // Subsystems
        private readonly DebugFrameGraph _frameGraph = new(120);
        private readonly DebugRaycastPicker _picker = new();
        private readonly DebugVariableEditor _varEditor = new();
        private readonly DebugConsoleCommandRegistry _commands = new();

        // Logging & Diagnostics
        private const int MaxConsoleLog = 64;
        private readonly List<string> _consoleLog = new();

        private const int MaxScriptDiagnostics = 8;
        private readonly List<ScriptDiagnostic> _scriptDiagnostics = new();
        private ScriptHostSystem _scriptHost;
        private RuntimeScene _activeScene;
        public string RoomName { get; set; } = "Level 1";

        private IRenderController _renderer;
        private IGameWindow _window;
        private float _frameDt;
        private bool _stepRequested;

        // Mouse tracking
        private Vector2 _lastMousePos;
        private bool _mouseClicked;
        private bool _clickConsumed;
        private float _mouseWheel;

        private readonly long _startTicks = Environment.TickCount64;

        /// <summary>Recent log lines (pause, recording, errors), newest last.</summary>
        public IReadOnlyList<string> LogLines => _consoleLog;

        public DebugOverlay()
        {
            Profiler.InstanceCounter = CountInstances;
            Engine.DebugWatchChanged += OnWatchChanged;
            RegisterCommands();

            if (Environment.GetEnvironmentVariable("GENESIS_DEBUG_EXPANDED") == "1")
                ShowExpandedPanels = true;

            if (string.Equals(
                    Environment.GetEnvironmentVariable(NavigationDebugTelemetry.InitialPanelEnvironmentVariable),
                    NavigationDebugTelemetry.AiNavigationPanelValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                ActivePanel = DebugHudPanel.AiNavigation;
                ShowExpandedPanels = true;
            }

            // Default startup log messages
            Log("[INFO] Genesis Debug Subsystem initialized.");
            Log("[INFO] F6 toggles this overlay, F7 the full panel, F8 recording; P pauses, N steps while paused.");
            Log("[INFO] AI & Navigation telemetry is available from the F6 ribbon.");
        }

        public bool HasScriptErrors => _scriptDiagnostics.Count > 0;
        public ScriptDiagnostic LatestScriptError =>
            _scriptDiagnostics.Count == 0 ? null : _scriptDiagnostics[^1];

        public void BindScriptHost(ScriptHostSystem scriptHost)
        {
            if (ReferenceEquals(_scriptHost, scriptHost)) return;
            if (_scriptHost != null) _scriptHost.DiagnosticReported -= ReportScriptDiagnostic;

            _scriptHost = scriptHost;
            _scriptDiagnostics.Clear();
            if (_scriptHost == null) return;

            foreach (ScriptDiagnostic diagnostic in _scriptHost.RecentDiagnostics)
                ReportScriptDiagnostic(diagnostic);
            _scriptHost.DiagnosticReported += ReportScriptDiagnostic;
        }

        public void BindScene(RuntimeScene scene, string roomName = null)
        {
            _activeScene = scene;
            if (!string.IsNullOrEmpty(roomName)) RoomName = roomName;
            NavigationDebugTelemetry.BindScene(scene);
        }

        public void BindWindow(IGameWindow window) => _window = window;

        public void SelectPanel(DebugHudPanel panel)
        {
            // The Engine tab exists only in developer builds.
            if (panel == DebugHudPanel.Engine && !EngineCategoryEnabled) panel = DebugHudPanel.Overview;
            ActivePanel = panel;
            if (panel != DebugHudPanel.Resources) IsSearchFocused = false;
            ShowExpandedPanels = true;
            NavigationDebugTelemetry.Enabled = IsVisible && panel == DebugHudPanel.AiNavigation;
            if (panel != DebugHudPanel.AiNavigation) NavigationDebugTelemetry.ResumeAi();
        }

        public void ReportScriptDiagnostic(ScriptDiagnostic diagnostic)
        {
            if (diagnostic == null) return;
            if (!_scriptDiagnostics.Contains(diagnostic)) _scriptDiagnostics.Add(diagnostic);
            while (_scriptDiagnostics.Count > MaxScriptDiagnostics) _scriptDiagnostics.RemoveAt(0);
            Profiler.NoteScriptError(diagnostic);
            Log($"[ERROR] {diagnostic.ToDisplayString()}");
        }

        public void DrainAssetDiagnostics()
        {
            while (RuntimeDiagnostics.TryDequeue(out string message))
            {
                ReportScriptDiagnostic(new ScriptDiagnostic
                {
                    TimestampUtc = DateTime.UtcNow,
                    LastOccurrenceUtc = DateTime.UtcNow,
                    ObjectName = "Assets",
                    BehaviorName = "Assets",
                    EventName = "Draw",
                    Hook = "Draw",
                    Message = message,
                });
            }
        }

        public void Log(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _consoleLog.Add(text);
            while (_consoleLog.Count > MaxConsoleLog) _consoleLog.RemoveAt(0);
        }

        private void OnWatchChanged(string name, object value)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (name == "log") Log(value?.ToString() ?? string.Empty);
            else _varEditor.SetGlobal(name, value);
        }

        public void Dispose()
        {
            // Closing the game finishes a recording: its summary and report are written now.
            StopRecording();
            Profiler.Dispose();
            UpdatePgslProfiling(active: false);
            NavigationDebugTelemetry.Enabled = false;
            NavigationDebugTelemetry.ResumeAi();
            Engine.DebugWatchChanged -= OnWatchChanged;
            if (_scriptHost != null)
            {
                _scriptHost.DiagnosticReported -= ReportScriptDiagnostic;
                _scriptHost = null;
            }
            _activeScene = null;
            _window = null;
        }

        public void HandleInput(InputState input, bool debugMode)
        {
            if (!debugMode || input == null) return;

            _lastMousePos = input.MousePosition;
            _mouseClicked = input.WasPressed(MouseButton.Left);
            _mouseWheel = input.WheelDelta;
            _clickConsumed = false;

            // F6: show / hide the debug screen (the compact strip).
            if (input.WasPressed(Key.F6))
            {
                IsVisible = !IsVisible;
                if (!IsVisible) IsSearchFocused = false;
            }

            // F7: open / close the full panel.
            if (input.WasPressed(PanelKey))
            {
                if (!IsVisible) IsVisible = true;
                ShowExpandedPanels = !ShowExpandedPanels;
                if (!ShowExpandedPanels) IsSearchFocused = false;
            }

            // F8: start / stop recording a profile (works with the screen hidden too).
            if (input.WasPressed(RecordKey))
                ToggleRecording();

            // While the Resources search box has the keyboard, letters are search text, not shortcuts.
            if (HandleSearchInput(input)) return;

            // P: Pause / Resume
            if (input.WasPressed(Key.P))
            {
                IsPaused = !IsPaused;
                Log(IsPaused ? "[INFO] Game paused [P to resume]" : "[INFO] Game resumed");
            }

            // N: Step single frame
            if (input.WasPressed(Key.N) && IsPaused)
            {
                _stepRequested = true;
            }
        }

        public bool ConsumeStepRequest()
        {
            bool requested = _stepRequested;
            _stepRequested = false;
            return requested;
        }

        public void Advance(float dt)
        {
            _frameDt = dt;
            _frameGraph.Record(dt);
            NavigationDebugTelemetry.BeginFrame();
            NavigationDebugTelemetry.AdvanceDebugPreview(_activeScene, dt);
        }

        public void Draw(IHudCanvas hud, IRenderController renderer, int width, int height)
        {
            _renderer = renderer;
            if (hud == null) return;

            // Script error banner
            float top = 8f;
            if (HasScriptErrors)
            {
                DrawScriptErrorBanner(hud, width);
                top = 66f;
            }

            if (ShowNavigation)
            {
                DrawNavigationTelemetry(hud, width, height);
            }

            if (!IsVisible) return;

            // Update raycast picker against real scene entities
            _picker.Update(_lastMousePos, _mouseClicked && !_clickConsumed, _activeScene, _scriptHost, _renderer, width, height);

            // Scene gizmos (bounds, wireframe outlines, light / audio / camera markers) belong to
            // the World tab; the compact strip leaves the game view clear.
            if (ShowExpandedPanels && ActivePanel == DebugHudPanel.World)
                DrawViewportGizmosAndPicker(hud, width, height);

            float stripBottom = DrawCompactStrip(hud, width, top);
            if (ShowExpandedPanels)
                DrawFullPanel(hud, width, height, stripBottom);
        }

        private void DrawNavigationTelemetry(IHudCanvas hud, int width, int height)
        {
            NavigationDebugSnapshot snapshot = NavigationDebugTelemetry.Get(_activeScene);
            if (snapshot == null) return;

            Vector4 cyan = new(0.10f, 0.86f, 1f, 0.96f);
            Vector4 green = new(0.22f, 1f, 0.44f, 0.8f);
            Vector4 red = new(1f, 0.25f, 0.20f, 0.95f);
            Vector4 white = new(0.95f, 0.98f, 1f, 1f);
            Vector4 panel = new(0.035f, 0.055f, 0.075f, 0.88f);

            if (_navigationNavMeshBounds && snapshot.NavMesh is not null)
                DrawNavigationMeshBounds(hud, snapshot.NavMesh, width, height, new Vector4(cyan.X, cyan.Y, cyan.Z, .28f));

            foreach (NavigationDebugAgent agent in snapshot.Agents)
            {
                if (!TryProjectWorld(agent.Position, width, height, out Vector2 agentScreen)) continue;
                bool selected = _selectedNavigationEntity == agent.EntityId;
                Vector4 routeColor = selected ? DebugOverlayPalette.Warning
                    : _selectedNavigationEntity >= 0 ? new Vector4(cyan.X, cyan.Y, cyan.Z, .3f) : cyan;

                if (_navigationRouteLines)
                {
                    Vector2 previousScreen = agentScreen;
                    foreach (Vector3 point in agent.Path)
                    {
                        if (TryProjectWorld(point, width, height, out Vector2 pointScreen))
                        {
                            hud.Line(previousScreen.X, previousScreen.Y, pointScreen.X, pointScreen.Y, routeColor, selected ? 5f : 3f);
                            hud.Rect(pointScreen.X - 3f, pointScreen.Y - 3f, 6f, 6f, routeColor, filled: true);
                            previousScreen = pointScreen;
                        }
                    }
                }

                if (_navigationCrowdAvoidance)
                {
                    DrawProjectedRing(hud, agent.Position, Math.Max(.1f, agent.Radius), width, height, selected ? DebugOverlayPalette.Warning : green);
                    DrawProjectedVector(hud, agent.Position, agent.Velocity, width, height, cyan, 2.5f);
                    DrawProjectedVector(hud, agent.Position, agent.Steering, width, height, red, 2f);
                }
                if (selected)
                    DrawProjectedRing(hud, agent.Position, Math.Max(.65f, agent.Radius * 1.8f), width, height, DebugOverlayPalette.Warning);

                if (_navigationOverheadTags)
                {
                    float labelX = agentScreen.X + 10f;
                    float labelY = agentScreen.Y - 54f;
                    string waypoint = agent.WaypointCount > 0
                        ? $"WP {agent.WaypointIndex + 1}/{agent.WaypointCount}"
                        : "WP —";
                    hud.Rect(labelX - 5f, labelY - 4f, 242f, 52f, panel, filled: true);
                    hud.Text($"{agent.Name}  |  {agent.RoutineName}", labelX, labelY, 10.5f, selected ? DebugOverlayPalette.Warning : white);
                    hud.Text($"{agent.State} · {waypoint}", labelX, labelY + 16f, 9f, cyan);
                    hud.Text($"Speed {agent.Speed:0.00} m/s  |  Remaining {agent.DistanceRemaining:0.00} m", labelX, labelY + 31f, 9f, white);
                }
            }

            DrawNavigationMinimap(hud, snapshot, width, height, cyan, green, red, white, panel);
        }

        private void DrawNavigationMeshBounds(IHudCanvas hud, NavMeshData mesh, int width, int height, Vector4 color)
        {
            int stride = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(Math.Max(1, mesh.Count) / 1800f)));
            for (int z = 0; z < mesh.Depth; z += stride)
            for (int x = 0; x < mesh.Width; x += stride)
            {
                int cell = z * mesh.Width + x;
                if (cell < 0 || cell >= mesh.Walkable.Length || !mesh.Walkable[cell]) continue;
                float x0 = mesh.OriginX + x * mesh.CellSize;
                float z0 = mesh.OriginZ + z * mesh.CellSize;
                float x1 = x0 + mesh.CellSize * stride;
                float z1 = z0 + mesh.CellSize * stride;
                float y = mesh.Heights[cell] + .025f;
                Vector3 a = new(x0, y, z0), b = new(x1, y, z0), c = new(x1, y, z1), d = new(x0, y, z1);
                if (TryProjectWorld(a, width, height, out Vector2 pa) && TryProjectWorld(b, width, height, out Vector2 pb))
                    hud.Line(pa.X, pa.Y, pb.X, pb.Y, color, 1f);
                if (TryProjectWorld(b, width, height, out Vector2 pb2) && TryProjectWorld(c, width, height, out Vector2 pc))
                    hud.Line(pb2.X, pb2.Y, pc.X, pc.Y, color, 1f);
                if (TryProjectWorld(c, width, height, out Vector2 pc2) && TryProjectWorld(d, width, height, out Vector2 pd))
                    hud.Line(pc2.X, pc2.Y, pd.X, pd.Y, color, 1f);
                if (TryProjectWorld(d, width, height, out Vector2 pd2) && TryProjectWorld(a, width, height, out Vector2 pa2))
                    hud.Line(pd2.X, pd2.Y, pa2.X, pa2.Y, color, 1f);
            }
        }

        private void DrawAiNavigationPanel(IHudCanvas hud, int width, int height)
        {
            float panelWidth = Math.Clamp(width * .32f, 360f, 500f);
            float x = width - panelWidth - 10f;
            float y = 76f;
            float panelHeight = Math.Max(260f, height - y - 38f);
            hud.Rect(x, y, panelWidth, panelHeight, DebugOverlayPalette.Surface, filled: true);
            hud.Rect(x, y, panelWidth, panelHeight, DebugOverlayPalette.Border, filled: false);

            NavigationDebugSnapshot snapshot = NavigationDebugTelemetry.Get(_activeScene);
            int agentCount = snapshot?.Agents.Count ?? 0;
            bool live = NavigationDebugTelemetry.Enabled && snapshot is not null;
            hud.Text("AI & NAVIGATION", x + 14f, y + 12f, 14f, DebugOverlayPalette.Text);
            hud.Text(live ? "● TELEMETRY ACTIVE" : "● TELEMETRY ARMED", x + panelWidth - 154f, y + 14f, 9.5f,
                agentCount > 0 ? DebugOverlayPalette.Success : DebugOverlayPalette.Accent);

            float controlY = y + 42f;
            float half = (panelWidth - 34f) * .5f;
            if (DrawButton(hud, x + 10f, controlY, half, 27f,
                    _navigationRouteLines ? "✓ Route Lines" : "□ Route Lines", _navigationRouteLines))
                _navigationRouteLines = !_navigationRouteLines;
            if (DrawButton(hud, x + 18f + half, controlY, half, 27f,
                    _navigationOverheadTags ? "✓ Overhead Tags" : "□ Overhead Tags", _navigationOverheadTags))
                _navigationOverheadTags = !_navigationOverheadTags;
            controlY += 31f;
            if (DrawButton(hud, x + 10f, controlY, half, 27f,
                    _navigationCrowdAvoidance ? "✓ Crowd Avoidance" : "□ Crowd Avoidance", _navigationCrowdAvoidance))
                _navigationCrowdAvoidance = !_navigationCrowdAvoidance;
            if (DrawButton(hud, x + 18f + half, controlY, half, 27f,
                    _navigationNavMeshBounds ? "✓ NavMesh Bounds" : "□ NavMesh Bounds", _navigationNavMeshBounds))
                _navigationNavMeshBounds = !_navigationNavMeshBounds;

            controlY += 40f;
            hud.Text("AI TRANSPORT", x + 12f, controlY, 10f, DebugOverlayPalette.Muted);
            controlY += 20f;
            float transportWidth = (panelWidth - 36f) / 3f;
            if (DrawButton(hud, x + 10f, controlY, transportWidth, 29f, "Pause AI", NavigationDebugTelemetry.IsAiPaused))
            {
                NavigationDebugTelemetry.PauseAi();
                Log("[INFO] AI navigation paused independently of game physics.");
            }
            if (DrawButton(hud, x + 14f + transportWidth, controlY, transportWidth, 29f, "Resume AI", !NavigationDebugTelemetry.IsAiPaused))
            {
                NavigationDebugTelemetry.ResumeAi();
                Log("[INFO] AI navigation resumed.");
            }
            if (DrawButton(hud, x + 18f + transportWidth * 2f, controlY, transportWidth, 29f, "Step AI", false))
            {
                NavigationDebugTelemetry.StepAi();
                Log("[INFO] AI navigation advanced one frame.");
            }

            float rosterY = controlY + 45f;
            hud.Text($"ACTIVE ENTITIES  {agentCount}", x + 12f, rosterY, 10.5f, DebugOverlayPalette.Text);
            rosterY += 22f;
            float rosterHeight = Math.Max(70f, y + panelHeight - rosterY - 12f);
            hud.Rect(x + 8f, rosterY, panelWidth - 16f, rosterHeight, DebugOverlayPalette.Canvas, filled: true);

            IReadOnlyList<NavigationDebugAgent> agents = snapshot?.Agents ?? Array.Empty<NavigationDebugAgent>();
            const float rowHeight = 42f;
            int visibleRows = Math.Max(1, (int)(rosterHeight / rowHeight));
            int maxScroll = Math.Max(0, agents.Count - visibleRows);
            if (IsHovered(x + 8f, rosterY, panelWidth - 16f, rosterHeight) && Math.Abs(_mouseWheel) > .01f)
                _navigationRosterScroll = Math.Clamp(_navigationRosterScroll - Math.Sign(_mouseWheel) * 3, 0, maxScroll);
            else
                _navigationRosterScroll = Math.Clamp(_navigationRosterScroll, 0, maxScroll);

            if (agents.Count == 0)
            {
                hud.Text("No active navigation agents in this room.", x + 18f, rosterY + 16f, 10f, DebugOverlayPalette.Muted);
                hud.Text("Start a Pathing routine or choose Debug from the Pathing Editor.", x + 18f, rosterY + 34f, 9f, DebugOverlayPalette.Muted);
                return;
            }

            int last = Math.Min(agents.Count, _navigationRosterScroll + visibleRows);
            for (int index = _navigationRosterScroll; index < last; index++)
            {
                NavigationDebugAgent agent = agents[index];
                float rowY = rosterY + (index - _navigationRosterScroll) * rowHeight;
                bool selected = agent.EntityId == _selectedNavigationEntity;
                if (DrawButton(hud, x + 10f, rowY + 2f, panelWidth - 20f, rowHeight - 4f, string.Empty, selected,
                        selected ? DebugOverlayPalette.Warning : DebugOverlayPalette.Accent))
                    _selectedNavigationEntity = selected ? -1 : agent.EntityId;
                string waypoint = agent.WaypointCount > 0
                    ? $"WP {agent.WaypointIndex + 1}/{agent.WaypointCount}"
                    : "WP —";
                hud.Text($"{agent.Name}  ·  {agent.RoutineName}", x + 18f, rowY + 7f, 10f,
                    selected ? DebugOverlayPalette.Warning : DebugOverlayPalette.Text);
                hud.Text($"{agent.State}  ·  {waypoint}  ·  Dist {agent.DistanceRemaining:0.0}m  ·  Speed {agent.Speed:0.0}m/s",
                    x + 18f, rowY + 23f, 8.8f, DebugOverlayPalette.Muted);
            }
        }

        private void DrawProjectedRing(IHudCanvas hud, Vector3 center, float radius, int width, int height, Vector4 color)
        {
            const int segments = 20;
            Vector2 first = default;
            Vector2 previous = default;
            bool hasFirst = false;
            bool hasPrevious = false;
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * MathF.Tau / segments;
                Vector3 point = center + new Vector3(MathF.Cos(angle) * radius, .03f, MathF.Sin(angle) * radius);
                if (!TryProjectWorld(point, width, height, out Vector2 screen))
                {
                    hasPrevious = false;
                    continue;
                }
                if (!hasFirst) { first = screen; hasFirst = true; }
                if (hasPrevious) hud.Line(previous.X, previous.Y, screen.X, screen.Y, color, 1.5f);
                previous = screen;
                hasPrevious = true;
            }
            if (hasFirst && hasPrevious) hud.Line(previous.X, previous.Y, first.X, first.Y, color, 1.5f);
        }

        private void DrawProjectedVector(IHudCanvas hud, Vector3 origin, Vector3 vector, int width, int height, Vector4 color, float thickness)
        {
            if (vector.LengthSquared() < 0.0001f
                || !TryProjectWorld(origin, width, height, out Vector2 start)
                || !TryProjectWorld(origin + vector, width, height, out Vector2 end)) return;

            hud.Line(start.X, start.Y, end.X, end.Y, color, thickness);
            Vector2 direction = end - start;
            if (direction.LengthSquared() < 1f) return;
            direction = Vector2.Normalize(direction);
            Vector2 side = new(-direction.Y, direction.X);
            Vector2 tipA = end - direction * 8f + side * 4f;
            Vector2 tipB = end - direction * 8f - side * 4f;
            hud.Line(end.X, end.Y, tipA.X, tipA.Y, color, thickness);
            hud.Line(end.X, end.Y, tipB.X, tipB.Y, color, thickness);
        }

        private static void DrawNavigationMinimap(IHudCanvas hud, NavigationDebugSnapshot snapshot, int width, int height,
            Vector4 cyan, Vector4 green, Vector4 red, Vector4 white, Vector4 panel)
        {
            const float mapW = 244f;
            const float mapH = 184f;
            float mapX = 14f;
            float mapY = Math.Max(8f, height - mapH - 14f);
            hud.Rect(mapX, mapY, mapW, mapH, panel, filled: true);
            hud.Rect(mapX, mapY, mapW, mapH, cyan, filled: false);
            hud.Text("AI & NAVIGATION  |  F6", mapX + 8f, mapY + 6f, 10f, white);

            NavMeshData navMesh = snapshot.NavMesh;
            float minX;
            float minZ;
            float extentX;
            float extentZ;
            if (navMesh != null)
            {
                minX = navMesh.OriginX;
                minZ = navMesh.OriginZ;
                extentX = Math.Max(navMesh.CellSize, navMesh.Width * navMesh.CellSize);
                extentZ = Math.Max(navMesh.CellSize, navMesh.Depth * navMesh.CellSize);
            }
            else if (snapshot.Agents.Count > 0)
            {
                minX = snapshot.Agents.Min(agent => Math.Min(agent.Position.X, agent.Destination.X)) - 2f;
                minZ = snapshot.Agents.Min(agent => Math.Min(agent.Position.Z, agent.Destination.Z)) - 2f;
                float maxX = snapshot.Agents.Max(agent => Math.Max(agent.Position.X, agent.Destination.X)) + 2f;
                float maxZ = snapshot.Agents.Max(agent => Math.Max(agent.Position.Z, agent.Destination.Z)) + 2f;
                extentX = Math.Max(1f, maxX - minX);
                extentZ = Math.Max(1f, maxZ - minZ);
            }
            else
            {
                hud.Text("No active navigation agents", mapX + 8f, mapY + 28f, 9f, white);
                return;
            }

            const float inset = 8f;
            float contentX = mapX + inset;
            float contentY = mapY + 24f;
            float contentW = mapW - inset * 2f;
            float contentH = mapH - 32f;
            Vector2 Map(Vector3 point) => new(
                contentX + (point.X - minX) / extentX * contentW,
                contentY + (point.Z - minZ) / extentZ * contentH);

            if (navMesh != null)
            {
                int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(navMesh.Count / 1800f)));
                float cellW = Math.Max(1f, navMesh.CellSize / extentX * contentW * step);
                float cellH = Math.Max(1f, navMesh.CellSize / extentZ * contentH * step);
                Vector4 walkable = new(0.08f, 0.54f, 0.62f, 0.34f);
                for (int z = 0; z < navMesh.Depth; z += step)
                for (int x = 0; x < navMesh.Width; x += step)
                {
                    int cell = z * navMesh.Width + x;
                    if (!navMesh.Walkable[cell]) continue;
                    Vector2 location = Map(navMesh.Center(cell));
                    hud.Rect(location.X - cellW * .5f, location.Y - cellH * .5f, cellW, cellH, walkable, filled: true);
                }
            }

            foreach (NavigationDebugAgent agent in snapshot.Agents)
            {
                Vector2 position = Map(agent.Position);
                Vector2 destination = Map(agent.Destination);
                hud.Line(position.X, position.Y, destination.X, destination.Y, cyan, 1.2f);
                hud.Rect(destination.X - 2f, destination.Y - 2f, 4f, 4f, red, filled: true);
                hud.Rect(position.X - 3f, position.Y - 3f, 6f, 6f, green, filled: true);
            }
        }

        // ── 3. Left Panel — World & Collapsible Inspector ───────────────────────

        private void DrawLeftInspectorPanel(IHudCanvas hud, float panelX, float panelY, float panelW, float panelH)
        {
            hud.Rect(panelX, panelY, panelW, panelH, DebugOverlayPalette.Canvas, filled: true);
            hud.Text("World & Inspector", panelX + 10f, panelY + 8f, 12f, DebugOverlayPalette.Text);

            float y = panelY + 30f;
            hud.Text($"▼ Room: Assets/Rooms/{RoomName}.room.json", panelX + 10f, y, 10.5f, DebugOverlayPalette.Accent);
            y += 20f;

            // List actual scene entities
            var entities = _picker.AllEntities;
            if (entities.Count == 0)
            {
                hud.Text("No active entities in room", panelX + 12f, y, 10f, DebugOverlayPalette.Muted);
                y += 20f;
            }

            for (int i = 0; i < entities.Count && y < panelY + panelH - 120f; i++)
            {
                var ent = entities[i];
                bool isSelected = _picker.SelectedEntity == ent;

                // Entity Header Row
                string expandIcon = ent.IsExpanded ? "▼" : "►";
                string label = $"{expandIcon} {ent.Name} (#{ent.Id})";

                Vector4 headerBg = isSelected
                    ? DebugOverlayPalette.Selected
                    : DebugOverlayPalette.Raised;

                if (DrawButton(hud, panelX + 6f, y, panelW - 12f, 22f, label, isSelected))
                {
                    ent.IsExpanded = !ent.IsExpanded;
                    _picker.SelectEntity(ent);
                }
                y += 24f;

                // Expanded entity details
                if (ent.IsExpanded)
                {
                    // Built-in variables
                    hud.Text("Built-in variables", panelX + 18f, y, 10f, DebugOverlayPalette.Muted);
                    y += 15f;

                    string bvars = $"(x: {ent.BuiltInVars.GetValueOrDefault("x", 0)}, y: {ent.BuiltInVars.GetValueOrDefault("y", 0)}, depth: {ent.BuiltInVars.GetValueOrDefault("depth", 0)})";
                    hud.Text(bvars, panelX + 18f, y, 9.5f, DebugOverlayPalette.Text);
                    y += 18f;

                    // Custom PGSL variables
                    hud.Text("Custom PGSL variables", panelX + 18f, y, 10f, DebugOverlayPalette.Muted);
                    y += 15f;

                    if (ent.CustomVars.Count == 0)
                    {
                        hud.Text("(none)", panelX + 18f, y, 9.5f, DebugOverlayPalette.Muted);
                        y += 16f;
                    }
                    else
                    {
                        foreach (var (k, v) in ent.CustomVars)
                        {
                            hud.Text($"{k}: {v}", panelX + 18f, y, 9.5f, DebugOverlayPalette.Text);

                            // Interactive quick edit buttons for numeric variables
                            if (v is double dVal)
                            {
                                if (DrawButton(hud, panelX + panelW - 48f, y - 2f, 18f, 16f, "-", false))
                                {
                                    double newVal = Math.Max(0.5, dVal - 1.0);
                                    ent.CustomVars[k] = newVal;
                                    if (ent.Behavior?.Context != null) ent.Behavior.Context.Variables[k] = newVal;
                                    Log($"[INFO] {ent.Name}.{k} = {newVal}");
                                }
                                if (DrawButton(hud, panelX + panelW - 26f, y - 2f, 18f, 16f, "+", false))
                                {
                                    double newVal = dVal + 1.0;
                                    ent.CustomVars[k] = newVal;
                                    if (ent.Behavior?.Context != null) ent.Behavior.Context.Variables[k] = newVal;
                                    Log($"[INFO] {ent.Name}.{k} = {newVal}");
                                }
                            }
                            y += 16f;
                        }
                    }
                    y += 6f;
                }
            }

            y += 8f;
            hud.Line(panelX + 6f, y, panelX + panelW - 6f, y, DebugOverlayPalette.Border, 1f);
            y += 10f;

            // Global Variables Section
            hud.Text("Global Variables", panelX + 10f, y, 11f, DebugOverlayPalette.Accent);
            y += 20f;

            foreach (var (k, v) in _varEditor.Globals)
            {
                hud.Text($"{k} = {v}", panelX + 12f, y, 10f, DebugOverlayPalette.Text);

                if (v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
                {
                    if (DrawButton(hud, panelX + panelW - 52f, y - 2f, 44f, 16f, "+1", false))
                    {
                        object next = v switch
                        {
                            sbyte value => (sbyte)(value + 1), byte value => (byte)(value + 1),
                            short value => (short)(value + 1), ushort value => (ushort)(value + 1),
                            int value => value + 1, uint value => value + 1,
                            long value => value + 1, ulong value => value + 1,
                            float value => value + 1f, double value => value + 1d,
                            decimal value => value + 1m, _ => v,
                        };
                        _varEditor.SetGlobal(k, next);
                        Log($"[INFO] {k} = {next}");
                    }
                }
                else if (v is bool bVal)
                {
                    if (DrawButton(hud, panelX + panelW - 52f, y - 2f, 44f, 16f, "Toggle", false))
                    {
                        _varEditor.SetGlobal(k, !bVal);
                        Log($"[INFO] {k} = {!bVal}");
                    }
                }

                y += 18f;
                if (y > panelY + panelH - 20f) break;
            }
        }

        /// <summary>
        /// AF2.7 F6 cloud status. Raymarch running → timing + quality letter. Prefs on but pass
        /// skipped (Software / missing weather upload) → honest FogVolumes fallback label. Prefs
        /// off → <c>CL: off</c>. Pure helper so headless gates can assert without scraping HUD text.
        /// </summary>
        public static string CloudsStatusLabel(
            RenderStats stats,
            string backendName,
            bool raymarchedEnabled)
        {
            if (!raymarchedEnabled)
                return "CL: off";

            if (stats.RaymarchedCloudsMs > 0.001)
                return $"CL: {stats.RaymarchedCloudsMs:F1} ms {CloudQualityLetter()}";

            // Software (or any backend where the GPU pass did not run) keeps FogVolumes — say so.
            _ = backendName;
            return "CL: FogVolumes (Software)";
        }

        private static string CloudQualityLetter()
        {
            char q = MeshLightingDefaults.CloudQuality switch
            {
                0 => 'P',
                1 => 'B',
                3 => 'C',
                _ => 'H',
            };
            return MeshLightingDefaults.CloudTemporalEnabled ? $"{q}T" : q.ToString();
        }

        private static void DrawUsageBar(IHudCanvas hud, float x, float y, float width, float ratio)
        {
            float clamped = Math.Clamp(ratio, 0f, 1f);
            hud.Rect(x, y, width, 6f, DebugOverlayPalette.UsageTrack, filled: true);
            if (clamped > 0f)
                hud.Rect(x, y, width * clamped, 6f, DebugOverlayPalette.UsageFill, filled: true);
        }

        // ── 5. In-Viewport Raycasting & "What Am I Looking At" ──────────────────

        private void DrawViewportGizmosAndPicker(IHudCanvas hud, int width, int height)
        {
            var entities = _picker.AllEntities;

            // Green wireframe-style outlines (DebugRuntimeF6.png) — HUD approximation + GPU
            // Mesh3DState.Wireframe when ShowWireframe is on (wired by GenesisRuntimeHost).
            if (ShowWireframe)
            {
                foreach (var ent in entities)
                {
                    Vector4 bounds = ent.ScreenBounds;
                    float bw = bounds.Z - bounds.X;
                    float bh = bounds.W - bounds.Y;
                    if (bw <= 0 || bh <= 0) continue;
                    hud.Rect(bounds.X, bounds.Y, bw, bh, DebugOverlayPalette.Success, filled: false);
                }
            }

            // Draw bounding boxes around all active scene entities when BBoxes is checked
            if (ShowBBoxes && !ShowWireframe)
            {
                foreach (var ent in entities)
                {
                    Vector4 bounds = ent.ScreenBounds;
                    float bw = bounds.Z - bounds.X;
                    float bh = bounds.W - bounds.Y;
                    if (bw <= 0 || bh <= 0) continue;

                    hud.Rect(bounds.X, bounds.Y, bw, bh, DebugOverlayPalette.AccentDim, filled: false);
                }
            }

            DrawSceneDebugMarkers(hud, width, height);

            // Inspection tooltip only in expanded debugger (keeps compact F6 mock clear)
            var target = _picker.SelectedEntity ?? _picker.HoveredEntity;
            if (target != null && IsInspectModeActive && ShowExpandedPanels)
            {
                Vector4 b = target.ScreenBounds;
                float bw = b.Z - b.X;
                float bh = b.W - b.Y;

                // Glowing highlight box
                hud.Rect(b.X - 2f, b.Y - 2f, bw + 4f, bh + 4f, DebugOverlayPalette.Accent, filled: false);
                hud.Rect(b.X - 4f, b.Y - 4f, bw + 8f, bh + 8f, DebugOverlayPalette.AccentDim, filled: false);

                // Floating tooltip card ("What Am I Looking At")
                float cardW = 175f;
                float cardH = 80f;
                float cardX = Math.Clamp(b.Z + 20f, 280f, width - cardW - 230f);
                float cardY = Math.Clamp(b.Y - 40f, 50f, height - cardH - 80f);

                // Connecting pointer line
                hud.Line(cardX, cardY + cardH * 0.5f, b.X + bw * 0.5f, b.Y + bh * 0.5f, DebugOverlayPalette.Accent, 1.5f);

                // Card panel
                hud.Rect(cardX, cardY, cardW, cardH, DebugOverlayPalette.Surface, filled: true);

                hud.Text($"{target.Name} #{target.Id}", cardX + 8f, cardY + 6f, 10.5f, DebugOverlayPalette.Accent);
                hud.Text($"Pos: ({target.WorldPosition.X:0.#}, {target.WorldPosition.Y:0.#})", cardX + 8f, cardY + 22f, 9.5f, DebugOverlayPalette.Text);
                hud.Text($"Sprite: {target.SpriteName}", cardX + 8f, cardY + 36f, 9.5f, DebugOverlayPalette.Text);
                hud.Text($"Collider: {target.ColliderType}", cardX + 8f, cardY + 50f, 9.5f, DebugOverlayPalette.Text);
                hud.Text($"Script: {target.ScriptName}", cardX + 8f, cardY + 64f, 9.5f, DebugOverlayPalette.Muted);
            }
        }

        /// <summary>
        /// Screen-space light / audio / camera markers matching <c>DebugRuntimeF6.png</c>.
        /// </summary>
        private void DrawSceneDebugMarkers(IHudCanvas hud, int width, int height)
        {
            if (_activeScene?.World == null) return;

            EcsWorld world = _activeScene.World;
            world.Query<TransformComponent, PointLightComponent>(
                (Entity _, ref TransformComponent transform, ref PointLightComponent light) =>
                {
                    if (!light.Enabled) return;
                    Vector3 worldPos = new(
                        transform.X + light.Offset.X,
                        transform.Y + light.Offset.Y,
                        transform.Z + light.Offset.Z);
                    if (!TryProjectWorld(worldPos, width, height, out Vector2 screen)) return;
                    DrawLightMarker(hud, screen.X, screen.Y);
                });

            world.Query<TransformComponent, AudioComponent>(
                (Entity _, ref TransformComponent transform, ref AudioComponent _) =>
                {
                    Vector3 worldPos = new(transform.X, transform.Y, transform.Z);
                    if (!TryProjectWorld(worldPos, width, height, out Vector2 screen)) return;
                    DrawAudioMarker(hud, screen.X, screen.Y);
                });

            if (ShowCameras && _activeScene.Camera3D != null)
            {
                Camera3D camera = _activeScene.Camera3D;
                // Project a point ahead of the eye so the marker lands in-frame (eye itself is behind).
                Vector3 markerWorld = camera.Position + camera.Forward * 3f;
                if (TryProjectWorld(markerWorld, width, height, out Vector2 camScreen))
                    DrawCameraMarker(hud, camScreen.X, camScreen.Y);
            }
        }

        private bool TryProjectWorld(Vector3 world, int width, int height, out Vector2 screen)
        {
            screen = default;
            Camera3D camera = _activeScene?.Camera3D;
            if (camera == null) return false;

            Matrix4x4 vp = camera.ViewProjection;
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), vp);
            if (Math.Abs(clip.W) < 1e-4f) return false;

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            if (ndcX < -1.35f || ndcX > 1.35f || ndcY < -1.35f || ndcY > 1.35f) return false;
            if (clip.W < 0f) return false;

            screen = new Vector2(
                (ndcX * 0.5f + 0.5f) * width,
                (1f - (ndcY * 0.5f + 0.5f)) * height);
            return true;
        }

        private static void DrawLightMarker(IHudCanvas hud, float x, float y)
        {
            Vector4 color = DebugOverlayPalette.Text;
            hud.Rect(x - 4f, y - 5f, 8f, 8f, color, filled: false);
            hud.Rect(x - 2f, y - 3f, 4f, 4f, color, filled: true);
            hud.Line(x, y - 12f, x, y - 16f, color, 1.2f);
            hud.Line(x + 9f, y - 2f, x + 13f, y - 5f, color, 1.2f);
            hud.Line(x - 9f, y - 2f, x - 13f, y - 5f, color, 1.2f);
            hud.Line(x + 8f, y + 6f, x + 12f, y + 10f, color, 1.2f);
            hud.Line(x - 8f, y + 6f, x - 12f, y + 10f, color, 1.2f);
            hud.Text("L", x - 3f, y + 8f, 8f, DebugOverlayPalette.Muted);
        }

        private static void DrawAudioMarker(IHudCanvas hud, float x, float y)
        {
            Vector4 color = DebugOverlayPalette.Text;
            hud.Rect(x - 7f, y - 4f, 6f, 8f, color, filled: true);
            hud.Line(x - 1f, y - 5f, x + 5f, y - 9f, color, 1.4f);
            hud.Line(x - 1f, y + 3f, x + 5f, y + 7f, color, 1.4f);
            hud.Line(x + 2f, y - 1f, x + 8f, y - 1f, color, 1.2f);
            hud.Text("A", x - 3f, y + 8f, 8f, DebugOverlayPalette.Muted);
        }

        private static void DrawCameraMarker(IHudCanvas hud, float x, float y)
        {
            Vector4 color = DebugOverlayPalette.Accent;
            hud.Rect(x - 8f, y - 5f, 12f, 10f, color, filled: false);
            hud.Line(x + 4f, y - 2f, x + 10f, y - 6f, color, 1.4f);
            hud.Line(x + 4f, y + 2f, x + 10f, y + 6f, color, 1.4f);
            hud.Text("CAM", x - 10f, y + 8f, 8f, DebugOverlayPalette.Muted);
        }

        // ── Helper UI Methods ───────────────────────────────────────────────────

        private bool DrawButton(IHudCanvas hud, float x, float y, float w, float h, string text, bool active, Vector4? accentColor = null)
        {
            bool hovered = IsHovered(x, y, w, h);
            bool clicked = hovered && _mouseClicked && !_clickConsumed;

            if (clicked)
            {
                _clickConsumed = true;
            }

            Vector4 btnColor = active
                ? (accentColor.HasValue ? new Vector4(accentColor.Value.X * 0.3f, accentColor.Value.Y * 0.3f, accentColor.Value.Z * 0.3f, 0.9f) : DebugOverlayPalette.AccentFill)
                : hovered
                    ? DebugOverlayPalette.Hover
                    : DebugOverlayPalette.Raised;

            hud.Rect(x, y, w, h, btnColor, filled: true);

            if (active && accentColor.HasValue)
            {
                hud.Rect(x, y, w, h, accentColor.Value, filled: false);
            }

            Vector4 textColor = active ? DebugOverlayPalette.Text : DebugOverlayPalette.Muted;
            hud.Text(text, x + 5f, y + (h * 0.5f - 6f), 10f, textColor);
            return clicked;
        }

        private bool IsHovered(float x, float y, float w, float h)
        {
            return _lastMousePos.X >= x && _lastMousePos.X <= x + w
                && _lastMousePos.Y >= y && _lastMousePos.Y <= y + h;
        }

        private void DrawScriptErrorBanner(IHudCanvas hud, int width)
        {
            ScriptDiagnostic latest = LatestScriptError;
            if (latest == null) return;

            float bannerWidth = Math.Max(160, width - 16);
            hud.Rect(8, 8, bannerWidth, 52, DebugOverlayPalette.Error, filled: true);
            hud.Text("SCRIPT ERROR - Gameplay code exception", 16, 12, 13f, DebugOverlayPalette.Text);
            hud.Text(latest.ToDisplayString(), 16, 31, 11f, DebugOverlayPalette.Text);
            hud.Text("Details saved to project_player.log", 16, 45, 9f, DebugOverlayPalette.Muted);
        }

        private void RegisterCommands()
        {
            _commands.Register("set", "Set variable value (e.g. set obj_player.moveSpeed 6.0)", args =>
            {
                if (args.Length >= 2)
                {
                    _varEditor.TrySetFromString(args[0], args[1]);
                    Log($"[INFO] Set {args[0]} = {args[1]}");
                    return $"Set {args[0]} = {args[1]}";
                }
                return "Usage: set <varName> <value>";
            });

            _commands.Register("speed", "Set game speed multiplier", args =>
            {
                if (args.Length > 0 && float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float spd))
                {
                    TimeScale = Math.Clamp(spd, 0.1f, 10f);
                    Log($"[INFO] TimeScale set to {TimeScale:F2}x");
                    return $"Speed: {TimeScale:F2}x";
                }
                return $"Speed: {TimeScale:F2}x";
            });

            _commands.Register("pause", "Toggle pause state", _ =>
            {
                IsPaused = !IsPaused;
                return IsPaused ? "Paused" : "Resumed";
            });

            _commands.Register("wireframe", "Toggle wireframe display", _ =>
            {
                ShowWireframe = !ShowWireframe;
                return $"Wireframe: {ShowWireframe}";
            });

            _commands.Register("expand", "Expand or collapse full F6 debugger panels", _ =>
            {
                ShowExpandedPanels = !ShowExpandedPanels;
                return ShowExpandedPanels ? "Expanded" : "Compact";
            });

            _commands.Register("bboxes", "Toggle bounding box display", _ =>
            {
                ShowBBoxes = !ShowBBoxes;
                return $"BBoxes: {ShowBBoxes}";
            });

            _commands.Register("inspect", "Toggle inspect mode", _ =>
            {
                IsInspectModeActive = !IsInspectModeActive;
                return $"Inspect Mode: {IsInspectModeActive}";
            });
        }
    }
}
