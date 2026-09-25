using System;
using System.Collections.Generic;
using System.Reflection;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Shared.Commands
{
    // Marks a method on the Engine class as a PGSL-callable engine command.
    // The PGSL compiler (Phase 4) reflects over Engine.* methods at startup to
    // build its CALL_NATIVE dispatch table. The editor (Phase 5) uses this for
    // autocomplete. The Demo Suite uses EngineCommandRegistry.GetCatalog() for
    // integration testing.
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class EngineCommandAttribute : Attribute
    {
        public string Signature   { get; }
        public string Category    { get; }
        public int    Phase       { get; }
        public string Description { get; }
        public bool   IsImplemented { get; }

        public EngineCommandAttribute(string signature, string category,
                                      int phase = 0, string description = "",
                                      bool implemented = true)
        {
            Signature   = signature;
            Category    = category;
            Phase       = phase;
            Description = description;
            IsImplemented = implemented;
        }
    }

    public sealed class EngineCommandInfo
    {
        public string Name        { get; init; }
        public string Signature   { get; init; }
        public string Category    { get; init; }
        public string Description { get; init; }
        public int    Phase       { get; init; }
        public bool   IsImplemented { get; init; }
    }

    // Reflects over Engine.* methods once at startup; provides the catalog used
    // by the PGSL compiler (CALL_NATIVE IDs), the code editor (autocomplete),
    // and the Demo Suite (integration test enumeration).
    public static class EngineCommandRegistry
    {
        private static List<EngineCommandInfo> _catalog;

        public static void Build()
        {
            _catalog = new List<EngineCommandInfo>(64);
            foreach (MethodInfo m in typeof(Engine).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var attr = m.GetCustomAttribute<EngineCommandAttribute>();
                if (attr == null) continue;

                // Extract bare name from "Engine.XYZ(..." → "XYZ"
                string sig  = attr.Signature;
                int    dot  = sig.IndexOf('.');
                int    paren = sig.IndexOf('(');
                string name = (dot >= 0 && paren > dot)
                    ? sig.Substring(dot + 1, paren - dot - 1).Trim()
                    : sig;

                _catalog.Add(new EngineCommandInfo
                {
                    Name          = name,
                    Signature     = sig,
                    Category      = attr.Category,
                    Description   = attr.Description,
                    Phase         = attr.Phase,
                    IsImplemented = attr.IsImplemented,
                });
            }
        }

        public static IReadOnlyList<EngineCommandInfo> GetCatalog()
        {
            if (_catalog == null) Build();
            return _catalog;
        }

        public static EngineCommandInfo GetByName(string name)
        {
            if (_catalog == null) Build();
            foreach (var info in _catalog)
                if (string.Equals(info.Name, name, StringComparison.OrdinalIgnoreCase))
                    return info;
            return null;
        }
    }

    // ── Single source of truth for all Engine.* commands ────────────────────────
    //
    // Add new commands HERE and only here. See Documentation/README.md Part I — Rule 16.
    //
    // Phase 2 commands  — backed by static events; subscribe in the render host.
    // Phase 3+ commands — throw NotImplementedException until their phase begins.
    //
    // C# naming: PGSL syntax "Engine.3DStart()" maps to C# method Start3D() because
    // C# identifiers cannot begin with a digit. The PGSL-facing name
    public static class Engine
    {
        // ── Backing State ───────────────────────────────────────────────────────

        private static int _fpsTarget = -1;
        private static bool _vsync = true;
        private static string _windowMode = "Windowed";
        private static string _windowTitle = string.Empty;
        private static int _windowWidth;
        private static int _windowHeight;
        private static string _debugView = "Shaded";
        private static bool _lighting = true;
        private static string _cullingWinding = "CCW";
        private static string _cullingFaceMode = "Back";
        private static bool _fogEnabled = false;
        private static Vector3 _fogStart = Vector3.Zero;
        private static Vector3 _fogEnd = new Vector3(0, 0, 100);
        private static Vector4 _fogColour = Vector4.One;
        private static float _fogDensity = 0.05f;

        private static bool _textureAtlas3D = true;
        private static float _atlasMaxSize = 4096f;
        private static bool _backfaceCulling = true;
        private static bool _deferredLighting2D = false;
        private static bool _deferredLighting3D = false;
        private static float _faceCulling = 0f; // 0=Back, 1=Front, 2=None
        private static float _firstPersonCamera = 1f;
        private static bool _frustumCulling = true;
        private static bool _globalLighting = true;
        private static bool _globalShadows = true;
        private static float _shadowStrength = 1f;
        private static bool _gpuCulling = false;
        private static float _lodFar = 1000f;
        private static float _lodMid = 500f;
        private static float _lodNear = 100f;
        private static bool _lodScaling = true;
        private static bool _occlusionCulling = false;

        static Engine()
        {
            // Keep LodPolicy / RenderAutoState aligned with Engine defaults at process start.
            var lod = Genesis.Shared.Rendering.LodPolicy.Current;
            lod.Near = _lodNear;
            lod.Mid = _lodMid;
            lod.Far = _lodFar;
            lod.ScalingEnabled = _lodScaling;
            Genesis.Shared.Rendering.RenderAutoState.FrustumCulling = _frustumCulling;
            Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling = _occlusionCulling;
            Genesis.Shared.Rendering.RenderAutoState.GpuCulling = _gpuCulling;
        }
        private static string _renderMode = "Auto";
        private static float _textureAllocSize = 256f;
        private static float _textureMaxSize = 0f;
        private static bool _windingOrderCcw = true;
        private static bool _zPrepass3D = false;

        private static Vector3 _cameraPosition = Vector3.Zero;
        private static Vector3 _cameraTarget = new Vector3(0, 0, 1);
        private static float _cameraFov = 60f;
        private static float _cameraNearPlane = 0.1f;
        private static float _cameraFarPlane = 2000f;
        private static Vector3 _ambientLight = new Vector3(0.2f, 0.2f, 0.2f);
        private static Vector3 _globalLightColor = Vector3.One;
        private static Vector3 _globalLightDirection = new Vector3(0f, -1f, 0f);
        private static Vector2 _globalLightDirection2D = new Vector2(0f, -1f);

        /// <summary>
        /// Indexed cameras share Room viewport slot ids (0..7). One play camera is drawn; non-active
        /// slots keep pose/frustum until switched.
        /// </summary>
        public const int MaxIndexedCameras = 8;

        private struct Camera2DSlot
        {
            public bool Exists;
            public float X, Y;
            public float Left, Right, Top, Bottom;
        }

        private struct Camera3DSlot
        {
            public bool Exists;
            public Vector3 Position;
            public float YawDegrees;
            public float PitchDegrees;
            public float FovDegrees;
            public float AspectRatio;
            public float NearZ;
            public float FarZ;
        }

        private static readonly Camera2DSlot[] _cameras2D = new Camera2DSlot[MaxIndexedCameras];
        private static readonly Camera3DSlot[] _cameras3D = new Camera3DSlot[MaxIndexedCameras];
        private static int _activeCamera2DId;
        private static int _activeCamera3DId;

        // ── Events ──────────────────────────────────────────────────────────────

        public static event Action<int>    FpsTargetChanged;
        public static event Action<bool>   VsyncChanged;
        public static event Action<string> WindowModeChanged;
        public static event Action<string> WindowTitleChanged;
        public static event Action<int, int> WindowSizeChanged;

        // ── Phase 10 rendering (Temp Dev 3D + future hosts) ─────────────────────

        public static event Action<bool>   LightingChanged;
        public static event Action<bool>   ShadowsChanged;
        public static event Action<float>  ShadowStrengthChanged;
        public static event Action<bool>   FogChanged;
        public static event Action<float>  FogStartChanged;
        public static event Action<float>  FogEndChanged;
        public static event Action<Vector4> FogColourChanged;
        public static event Action<float>  FogDensityChanged;
        public static event Action<string, string> CullingChanged;
        public static event Action<string, bool> GpuFunctionChanged;
        public static event Action<RenderDebugView> DebugViewChanged;

        public static event Action<bool>   DeferredLighting2DChanged;
        public static event Action<bool>   DeferredLighting3DChanged;
        public static event Action<float>  CameraFovChanged;
        public static event Action<float>  CameraNearPlaneChanged;
        public static event Action<float>  CameraFarPlaneChanged;
        public static event Action<Vector3> CameraPositionChanged;
        public static event Action<Vector3> CameraTargetChanged;
        /// <summary>Fired when an indexed 2D camera slot is created or mutated (id is the viewport slot).</summary>
        public static event Action<int> Camera2DRegistryChanged;
        /// <summary>Fired when an indexed 3D camera slot is created or mutated (id is the viewport slot).</summary>
        public static event Action<int> Camera3DRegistryChanged;
        public static event Action<Vector3> AmbientLightChanged;
        public static event Action<Vector3> GlobalLightColorChanged;
        public static event Action<Vector3> GlobalLightDirectionChanged;
        public static event Action<Vector2> GlobalLightDirection2DChanged;
        public static event Action<float, float, float, float, float, float, float> Light2DAdded;
        public static event Action         Lights2DCleared;
        public static event Action         SettingsApplied;

        // ── Windowing ───────────────────────────────────────────────────────────
        //
        // Everything that decides what the game's window *is*: its size, title, frame pacing,
        // VSync and windowed/fullscreen mode. Grouped under one category so the command reference
        // presents them together, and so Room settings and Preferences have one obvious place to
        // reach rather than each inventing their own path to the window.

        [EngineCommand("Engine.FpsTarget(target)", "Windowing", phase: 2,
            description: "-1 for uncapped; positive integer for strict frame pacing")]
        public static void FpsTarget(int target)
        {
            _fpsTarget = target;
            FpsTargetChanged?.Invoke(target);
        }

        [EngineCommand("Engine.FpsVsync(bool)", "Windowing", phase: 2,
            description: "Explicit hardware VSync toggle")]
        public static void FpsVsync(bool enabled)
        {
            _vsync = enabled;
            VsyncChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.WindowMode(mode)", "Windowing", phase: 2,
            description: "\"Windowed\" or \"Fullscreen\"")]
        public static void WindowMode(string mode)
        {
            _windowMode = mode;
            WindowModeChanged?.Invoke(mode);
        }

        [EngineCommand("Engine.WindowTitle(title)", "Windowing", phase: 2,
            description: "Sets the game window's title bar text")]
        public static void WindowTitle(string title)
        {
            _windowTitle = title ?? string.Empty;
            WindowTitleChanged?.Invoke(_windowTitle);
        }

        [EngineCommand("Engine.WindowSize(width, height)", "Windowing", phase: 2,
            description: "Resizes the game window's client area")]
        public static void WindowSize(int width, int height)
        {
            _windowWidth = Math.Max(1, width);
            _windowHeight = Math.Max(1, height);
            WindowSizeChanged?.Invoke(_windowWidth, _windowHeight);
        }

        [EngineCommand("Engine.DebugView(mode)", "Rendering", phase: 10,
            description: "\"Shaded\", \"Normals\", \"Depth\", or \"Shadow\"")]
        public static void DebugView(string mode)
        {
            _debugView = mode;
            RenderDebugView view = mode?.ToLowerInvariant() switch
            {
                "normals" => RenderDebugView.Normals,
                "depth"   => RenderDebugView.SceneDepth,
                "shadow"  => RenderDebugView.ShadowMap,
                _         => RenderDebugView.Shaded,
            };
            DebugViewChanged?.Invoke(view);
        }

        [EngineCommand("Engine.DisplayResolution(w, h)", "Performance", phase: 12,
            description: "Set the output resolution", implemented: false)]
        public static void DisplayResolution(int w, int h) =>
            throw new NotImplementedException("Engine.DisplayResolution — Phase 12");

        [EngineCommand("Engine.EnableCPUFunction(funcName)", "Performance", phase: 7,
            description: "Re-enable a named CPU-side system (e.g. \"Physics\")", implemented: false)]
        public static void EnableCPUFunction(string funcName) =>
            throw new NotImplementedException("Engine.EnableCPUFunction — Phase 7");

        [EngineCommand("Engine.DisableCPUFunction(funcName)", "Performance", phase: 7,
            description: "Disable a named CPU-side system (e.g. \"Physics\")", implemented: false)]
        public static void DisableCPUFunction(string funcName) =>
            throw new NotImplementedException("Engine.DisableCPUFunction — Phase 7");

        [EngineCommand("Engine.EnableGPUFunction(funcName)", "Performance", phase: 7,
            description: "Re-enable a named GPU-side pass (e.g. \"Shadows\")")]
        public static void EnableGPUFunction(string funcName)
        {
            if (IsGpuPhase10Function(funcName))
            {
                SetGpuFunctionState(funcName, true);
                GpuFunctionChanged?.Invoke(funcName, true);
            }
            else
                throw new NotImplementedException("Engine.EnableGPUFunction — Phase 7");
        }

        [EngineCommand("Engine.DisableGPUFunction(funcName)", "Performance", phase: 7,
            description: "Disable a named GPU-side pass (e.g. \"Shadows\")")]
        public static void DisableGPUFunction(string funcName)
        {
            if (IsGpuPhase10Function(funcName))
            {
                SetGpuFunctionState(funcName, false);
                GpuFunctionChanged?.Invoke(funcName, false);
            }
            else
                throw new NotImplementedException("Engine.DisableGPUFunction — Phase 7");
        }

        private static void SetGpuFunctionState(string funcName, bool enabled)
        {
            if (funcName.Equals("Shadows", StringComparison.OrdinalIgnoreCase))
            {
                _globalShadows = enabled;
                ShadowsChanged?.Invoke(enabled);
            }
            else if (funcName.Equals("Fog", StringComparison.OrdinalIgnoreCase))
            {
                _fogEnabled = enabled;
                FogChanged?.Invoke(enabled);
            }
            else if (funcName.Equals("Lighting", StringComparison.OrdinalIgnoreCase))
            {
                _lighting = enabled;
                _globalLighting = enabled;
                LightingChanged?.Invoke(enabled);
            }
        }

        private static bool IsGpuPhase10Function(string funcName)
        {
            if (string.IsNullOrEmpty(funcName)) return false;
            return funcName.Equals("Shadows", StringComparison.OrdinalIgnoreCase)
                || funcName.Equals("Fog", StringComparison.OrdinalIgnoreCase)
                || funcName.Equals("Lighting", StringComparison.OrdinalIgnoreCase);
        }

        // ── Rendering, Pipeline & Environment ───────────────────────────────────

        [EngineCommand("Engine.Start3D()", "Rendering", phase: 10,
            description: "Switch rendering context from default 2D to 3D", implemented: false)]
        public static void Start3D() =>
            throw new NotImplementedException("Engine.Start3D - Phase 10");

        [EngineCommand("Engine.Lighting(state)", "Rendering", phase: 10,
            description: "Enable or disable scene lighting")]
        public static void Lighting(bool state)
        {
            _lighting = state;
            _globalLighting = state;
            LightingChanged?.Invoke(state);
        }

        [EngineCommand("Engine.Culling(windingOrder, faceMode)", "Rendering", phase: 10,
            description: "Winding: \"CW\"/\"CCW\". Face: \"Front\"/\"Back\"/\"Off\"")]
        public static void Culling(string windingOrder, string faceMode)
        {
            _windingOrderCcw = string.Equals(windingOrder, "CCW", StringComparison.OrdinalIgnoreCase);
            _cullingWinding = _windingOrderCcw ? "CCW" : "CW";
            _cullingFaceMode = faceMode?.Trim().ToLowerInvariant() switch
            {
                "front" => "Front",
                "off" or "none" => "Off",
                _ => "Back",
            };
            _faceCulling = _cullingFaceMode == "Front" ? 1f : _cullingFaceMode == "Off" ? 2f : 0f;
            _backfaceCulling = _cullingFaceMode != "Off";
            CullingChanged?.Invoke(_cullingWinding, _cullingFaceMode);
        }

        [EngineCommand("Engine.Fog(state)", "Rendering", phase: 10,
            description: "Enable or disable distance fog")]
        public static void Fog(bool state)
        {
            _fogEnabled = state;
            FogChanged?.Invoke(state);
        }

        [EngineCommand("Engine.FogStart(x, y, z)", "Rendering", phase: 10,
            description: "Fog start/depth. 3D uses distance; in 2D call FogStart(0,0,z) to use authored Z/layer depth.")]
        public static void FogStart(float x, float y, float z)
        {
            float dist = FogDistance(x, y, z);
            _fogStart = new Vector3(dist, 0, 0);
            FogStartChanged?.Invoke(dist);
        }

        [EngineCommand("Engine.FogEnd(x, y, z)", "Rendering", phase: 10,
            description: "Fog end/depth. End-Start is fog thickness; in 2D Z is authored layer depth.")]
        public static void FogEnd(float x, float y, float z)
        {
            float dist = FogDistance(x, y, z);
            _fogEnd = new Vector3(dist, 0, 0);
            FogEndChanged?.Invoke(dist);
        }

        [EngineCommand("Engine.FogColour(r, g, b, a)", "Rendering", phase: 10,
            description: "RGBA fog colour, components 0–1. Alpha is the maximum fog blend opacity.")]
        public static void FogColour(float r, float g, float b, float a)
        {
            _fogColour = new Vector4(r, g, b, a);
            FogColourChanged?.Invoke(_fogColour);
        }

        [EngineCommand("Engine.FogDensity(value)", "Rendering", phase: 10,
            description: "Exponential fog density factor")]
        public static void FogDensity(float value)
        {
            _fogDensity = value;
            FogDensityChanged?.Invoke(value);
        }

        private static float FogDistance(float x, float y, float z)
        {
            // The same commands serve both dimensions. In 2D the natural Z coordinate is draw
            // depth/layer order, so the explicit (0,0,z) form must preserve its sign and value.
            // General 3D vectors keep the historical magnitude behaviour.
            if (MathF.Abs(x) < 0.000001f && MathF.Abs(y) < 0.000001f)
                return z;
            return MathF.Sqrt(x * x + y * y + z * z);
        }

        // ── Fog Volumes (Issue 6 Stage 1) ────────────────────────────────────────

        private static readonly Dictionary<int, FogVolume> _scriptedFogVolumes = new();
        private static int _nextFogVolumeId = 1;

        public static event Action FogVolumesChanged;

        [EngineCommand("Engine.FogVolumeCreate(shape)", "Rendering", phase: 10,
            description: "Create a placeable analytic fog volume. shape: \"Box\"/\"Sphere\"/\"Ellipsoid\"/\"HeightSlab\". Returns its id.")]
        public static int FogVolumeCreate(string shape)
        {
            int id = _nextFogVolumeId++;
            _scriptedFogVolumes[id] = new FogVolume
            {
                Shape        = ParseFogVolumeShape(shape),
                Center       = Vector3.Zero,
                Extents      = Vector3.One,
                Color        = new Vector3(0.80f, 0.84f, 0.87f),
                Density      = 0.30f,
                FalloffCurve = 1.5f,
                Kind         = FogVolumeKind.Haze,
            };
            FogVolumesChanged?.Invoke();
            return id;
        }

        [EngineCommand("Engine.FogVolumeSetBounds(id, x, y, z, ex, ey, ez)", "Rendering", phase: 10,
            description: "Set a fog volume's world-space centre (x,y,z) and half-extents (ex,ey,ez)")]
        public static void FogVolumeSetBounds(int id, float x, float y, float z,
                                               float ex, float ey, float ez)
        {
            if (!_scriptedFogVolumes.TryGetValue(id, out FogVolume v)) return;
            v.Center  = new Vector3(x, y, z);
            v.Extents = new Vector3(ex, ey, ez);
            _scriptedFogVolumes[id] = v;
            FogVolumesChanged?.Invoke();
        }

        [EngineCommand("Engine.FogVolumeSetColor(id, r, g, b, a)", "Rendering", phase: 10,
            description: "Set a fog volume's tint colour; a scales its density contribution (0-1)")]
        public static void FogVolumeSetColor(int id, float r, float g, float b, float a)
        {
            if (!_scriptedFogVolumes.TryGetValue(id, out FogVolume v)) return;
            v.Color   = new Vector3(r, g, b);
            v.Density = Math.Clamp(a, 0f, 1f);
            _scriptedFogVolumes[id] = v;
            FogVolumesChanged?.Invoke();
        }

        [EngineCommand("Engine.FogVolumeSetDensity(id, d)", "Rendering", phase: 10,
            description: "Set a fog volume's density (0-1)")]
        public static void FogVolumeSetDensity(int id, float d)
        {
            if (!_scriptedFogVolumes.TryGetValue(id, out FogVolume v)) return;
            v.Density = Math.Clamp(d, 0f, 1f);
            _scriptedFogVolumes[id] = v;
            FogVolumesChanged?.Invoke();
        }

        [EngineCommand("Engine.FogVolumeDestroy(id)", "Rendering", phase: 10,
            description: "Remove a fog volume created with Engine.FogVolumeCreate")]
        public static void FogVolumeDestroy(int id)
        {
            if (_scriptedFogVolumes.Remove(id))
                FogVolumesChanged?.Invoke();
        }

        public static IReadOnlyDictionary<int, FogVolume> GetScriptedFogVolumes() => _scriptedFogVolumes;

        private static FogVolumeShape ParseFogVolumeShape(string shape) => shape?.ToLowerInvariant() switch
        {
            "sphere"     => FogVolumeShape.Sphere,
            "ellipsoid"  => FogVolumeShape.Ellipsoid,
            "heightslab" => FogVolumeShape.HeightSlab,
            _            => FogVolumeShape.Box,
        };

        // ── Multi-Viewport & View Commands ───────────────────────────────────────

        [EngineCommand("Engine.ViewCreate(id)", "Viewport", phase: 7,
            description: "Create a named viewport slot", implemented: false)]
        public static void ViewCreate(int id) =>
            throw new NotImplementedException("Engine.ViewCreate — Phase 7");

        [EngineCommand("Engine.ViewSetPos(id, x, y)", "Viewport", phase: 7,
            description: "Set viewport top-left position in screen pixels", implemented: false)]
        public static void ViewSetPos(int id, int x, int y) =>
            throw new NotImplementedException("Engine.ViewSetPos — Phase 7");

        [EngineCommand("Engine.ViewSetSize(id, w, h)", "Viewport", phase: 7,
            description: "Set viewport dimensions in screen pixels", implemented: false)]
        public static void ViewSetSize(int id, int w, int h) =>
            throw new NotImplementedException("Engine.ViewSetSize — Phase 7");

        [EngineCommand("Engine.ViewSetCamera(viewId, camId)", "Viewport", phase: 7,
            description: "Bind a camera to a viewport", implemented: false)]
        public static void ViewSetCamera(int viewId, int camId) =>
            throw new NotImplementedException("Engine.ViewSetCamera — Phase 7");

        [EngineCommand("Engine.ViewSetActive(id)", "Viewport", phase: 7,
            description: "Set which viewport receives subsequent draw calls", implemented: false)]
        public static void ViewSetActive(int id) =>
            throw new NotImplementedException("Engine.ViewSetActive — Phase 7");

        // ── Camera Systems — 2D ──────────────────────────────────────────────────

        [EngineCommand("Engine.Camera2DCreate(id)", "Camera", phase: 7,
            description: "Create/activate a 2D orthographic camera; id is a Room viewport slot (0-7)")]
        public static void Camera2DCreate(int id)
        {
            EnsureCamera2D(id, activate: true);
            Camera2DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera2DSetPos(id, x, y)", "Camera", phase: 7,
            description: "Set the 2D camera world-space position")]
        public static void Camera2DSetPos(int id, float x, float y)
        {
            ref Camera2DSlot slot = ref EnsureCamera2D(id, activate: false);
            slot.X = x;
            slot.Y = y;
            Camera2DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera2DGetPosX(id)", "Camera", phase: 7,
            description: "Get the 2D camera world-space X position")]
        public static float Camera2DGetPosX(int id) =>
            TryGetCamera2D(id, out Camera2DSlot slot) ? slot.X : 0f;

        [EngineCommand("Engine.Camera2DGetPosY(id)", "Camera", phase: 7,
            description: "Get the 2D camera world-space Y position")]
        public static float Camera2DGetPosY(int id) =>
            TryGetCamera2D(id, out Camera2DSlot slot) ? slot.Y : 0f;

        [EngineCommand("Engine.Camera2DSetFrustum(id, left, right, top, bottom)", "Camera", phase: 7,
            description: "Set 2D orthographic frustum bounds in world space")]
        public static void Camera2DSetFrustum(int id, float left, float right,
                                               float top, float bottom)
        {
            ref Camera2DSlot slot = ref EnsureCamera2D(id, activate: false);
            slot.Left = left;
            slot.Right = right;
            slot.Top = top;
            slot.Bottom = bottom;
            Camera2DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera2DGetFrustum(id)", "Camera", phase: 7,
            description: "Returns [left, right, top, bottom] frustum bounds")]
        public static float[] Camera2DGetFrustum(int id)
        {
            if (!TryGetCamera2D(id, out Camera2DSlot slot))
                return new float[] { 0f, 0f, 0f, 0f };
            return new float[] { slot.Left, slot.Right, slot.Top, slot.Bottom };
        }

        // ── Camera Systems — 3D ──────────────────────────────────────────────────

        [EngineCommand("Engine.Camera3DCreate(id)", "Camera", phase: 10,
            description: "Create/activate a 3D perspective camera; id is a Room viewport slot (0-7)")]
        public static void Camera3DCreate(int id)
        {
            EnsureCamera3D(id, activate: true);
            Camera3DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera3DSetPos(id, x, y, z)", "Camera", phase: 10,
            description: "Set the 3D camera world-space position")]
        public static void Camera3DSetPos(int id, float x, float y, float z)
        {
            ref Camera3DSlot slot = ref EnsureCamera3D(id, activate: false);
            slot.Position = new Vector3(x, y, z);
            if (id == _activeCamera3DId)
            {
                _cameraPosition = slot.Position;
                CameraPositionChanged?.Invoke(_cameraPosition);
            }
            Camera3DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera3DGetPosX(id)", "Camera", phase: 10,
            description: "Get the 3D camera world-space X")]
        public static float Camera3DGetPosX(int id) =>
            TryGetCamera3D(id, out Camera3DSlot slot) ? slot.Position.X : 0f;

        [EngineCommand("Engine.Camera3DGetPosY(id)", "Camera", phase: 10,
            description: "Get the 3D camera world-space Y")]
        public static float Camera3DGetPosY(int id) =>
            TryGetCamera3D(id, out Camera3DSlot slot) ? slot.Position.Y : 0f;

        [EngineCommand("Engine.Camera3DGetPosZ(id)", "Camera", phase: 10,
            description: "Get the 3D camera world-space Z")]
        public static float Camera3DGetPosZ(int id) =>
            TryGetCamera3D(id, out Camera3DSlot slot) ? slot.Position.Z : 0f;

        [EngineCommand("Engine.Camera3DSetYaw(id, angle)", "Camera", phase: 10,
            description: "Set yaw in degrees (clockwise from above)")]
        public static void Camera3DSetYaw(int id, float angle)
        {
            ref Camera3DSlot slot = ref EnsureCamera3D(id, activate: false);
            slot.YawDegrees = angle;
            Camera3DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera3DGetYaw(id)", "Camera", phase: 10,
            description: "Get yaw in degrees")]
        public static float Camera3DGetYaw(int id) =>
            TryGetCamera3D(id, out Camera3DSlot slot) ? slot.YawDegrees : 0f;

        [EngineCommand("Engine.Camera3DSetPitch(id, angle)", "Camera", phase: 10,
            description: "Set pitch in degrees (positive = look up); clamped ±89°")]
        public static void Camera3DSetPitch(int id, float angle)
        {
            ref Camera3DSlot slot = ref EnsureCamera3D(id, activate: false);
            slot.PitchDegrees = Math.Clamp(angle, -89f, 89f);
            Camera3DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera3DGetPitch(id)", "Camera", phase: 10,
            description: "Get pitch in degrees")]
        public static float Camera3DGetPitch(int id) =>
            TryGetCamera3D(id, out Camera3DSlot slot) ? slot.PitchDegrees : 0f;

        [EngineCommand("Engine.Camera3DSetFrustum(id, fovDeg, aspectRatio, nearZ, farZ)", "Camera", phase: 10,
            description: "Set perspective frustum parameters")]
        public static void Camera3DSetFrustum(int id, float fovDeg, float aspectRatio,
                                               float nearZ, float farZ)
        {
            ref Camera3DSlot slot = ref EnsureCamera3D(id, activate: false);
            slot.FovDegrees = Math.Clamp(fovDeg, 1f, 179f);
            slot.AspectRatio = Math.Max(0.0001f, aspectRatio);
            slot.NearZ = Math.Max(0.001f, nearZ);
            slot.FarZ = Math.Max(slot.NearZ + 0.001f, farZ);
            if (id == _activeCamera3DId)
            {
                _cameraFov = slot.FovDegrees;
                _cameraNearPlane = slot.NearZ;
                _cameraFarPlane = slot.FarZ;
                CameraFovChanged?.Invoke(_cameraFov);
                CameraNearPlaneChanged?.Invoke(_cameraNearPlane);
                CameraFarPlaneChanged?.Invoke(_cameraFarPlane);
            }
            Camera3DRegistryChanged?.Invoke(id);
        }

        [EngineCommand("Engine.Camera3DGetFrustum(id)", "Camera", phase: 10,
            description: "Returns [fovDeg, aspectRatio, nearZ, farZ]")]
        public static float[] Camera3DGetFrustum(int id)
        {
            if (!TryGetCamera3D(id, out Camera3DSlot slot))
                return new float[] { 60f, 16f / 9f, 0.1f, 2000f };
            return new float[] { slot.FovDegrees, slot.AspectRatio, slot.NearZ, slot.FarZ };
        }

        public static int ActiveCamera2DId => _activeCamera2DId;
        public static int ActiveCamera3DId => _activeCamera3DId;

        public static bool TryGetCamera3DPose(
            int id,
            out Vector3 position,
            out float yawDegrees,
            out float pitchDegrees,
            out float fovDegrees,
            out float aspectRatio,
            out float nearZ,
            out float farZ)
        {
            position = default;
            yawDegrees = pitchDegrees = fovDegrees = aspectRatio = nearZ = farZ = 0f;
            if (!TryGetCamera3D(id, out Camera3DSlot slot)) return false;
            position = slot.Position;
            yawDegrees = slot.YawDegrees;
            pitchDegrees = slot.PitchDegrees;
            fovDegrees = slot.FovDegrees;
            aspectRatio = slot.AspectRatio;
            nearZ = slot.NearZ;
            farZ = slot.FarZ;
            return true;
        }

        public static bool TryGetCamera2DPose(
            int id,
            out float x,
            out float y,
            out float left,
            out float right,
            out float top,
            out float bottom)
        {
            x = y = left = right = top = bottom = 0f;
            if (!TryGetCamera2D(id, out Camera2DSlot slot)) return false;
            x = slot.X;
            y = slot.Y;
            left = slot.Left;
            right = slot.Right;
            top = slot.Top;
            bottom = slot.Bottom;
            return true;
        }

        private static ref Camera2DSlot EnsureCamera2D(int id, bool activate)
        {
            ValidateCameraId(id);
            ref Camera2DSlot slot = ref _cameras2D[id];
            if (!slot.Exists)
            {
                slot = new Camera2DSlot
                {
                    Exists = true,
                    X = 0f,
                    Y = 0f,
                    Left = -640f,
                    Right = 640f,
                    Top = -360f,
                    Bottom = 360f,
                };
            }
            if (activate) _activeCamera2DId = id;
            return ref slot;
        }

        private static ref Camera3DSlot EnsureCamera3D(int id, bool activate)
        {
            ValidateCameraId(id);
            ref Camera3DSlot slot = ref _cameras3D[id];
            if (!slot.Exists)
            {
                slot = new Camera3DSlot
                {
                    Exists = true,
                    Position = _cameraPosition,
                    YawDegrees = 0f,
                    PitchDegrees = 0f,
                    FovDegrees = _cameraFov > 0f ? _cameraFov : 60f,
                    AspectRatio = 16f / 9f,
                    NearZ = _cameraNearPlane > 0f ? _cameraNearPlane : 0.1f,
                    FarZ = _cameraFarPlane > 0f ? _cameraFarPlane : 2000f,
                };
            }
            if (activate)
            {
                _activeCamera3DId = id;
                _cameraPosition = slot.Position;
                _cameraFov = slot.FovDegrees;
                _cameraNearPlane = slot.NearZ;
                _cameraFarPlane = slot.FarZ;
                CameraPositionChanged?.Invoke(_cameraPosition);
                CameraFovChanged?.Invoke(_cameraFov);
                CameraNearPlaneChanged?.Invoke(_cameraNearPlane);
                CameraFarPlaneChanged?.Invoke(_cameraFarPlane);
            }
            return ref slot;
        }

        private static bool TryGetCamera2D(int id, out Camera2DSlot slot)
        {
            slot = default;
            if (id < 0 || id >= MaxIndexedCameras) return false;
            slot = _cameras2D[id];
            return slot.Exists;
        }

        private static bool TryGetCamera3D(int id, out Camera3DSlot slot)
        {
            slot = default;
            if (id < 0 || id >= MaxIndexedCameras) return false;
            slot = _cameras3D[id];
            return slot.Exists;
        }

        private static void ValidateCameraId(int id)
        {
            if (id < 0 || id >= MaxIndexedCameras)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(id),
                    id,
                    $"Camera id must be a Room viewport slot in 0..{MaxIndexedCameras - 1}.");
            }
        }

        // --- Physics ---

        [EngineCommand("Engine.PhysicsUse(assetName)", "Physics", phase: 10,
            description: "Apply a named Physics asset to this instance (zero-script tag)", implemented: false)]
        public static void PhysicsUse(string assetName) =>
            throw new NotImplementedException("Engine.PhysicsUse — Phase 10");

        [EngineCommand("Engine.Physics(type, shape, mass, friction, restitution, gravityScale)", "Physics", phase: 10,
            description: "One-line declarative body setup", implemented: false)]
        public static void Physics(string type, string shape, float mass, float friction = 0.5f, float restitution = 0.1f, float gravityScale = 1.0f) =>
            throw new NotImplementedException("Engine.Physics — Phase 10");

        // ── Legacy Engine Getters/Setters & Utilities ──────────────────────────────

        [EngineCommand("Engine.Get3DTextureAtlas()", "Rendering", phase: 10,
            description: "Reserved texture-atlas policy; no live renderer consumer yet", implemented: false)]
        public static bool Get3DTextureAtlas() => _textureAtlas3D;

        [EngineCommand("Engine.Set3DTextureAtlas(enabled)", "Rendering", phase: 10,
            description: "Reserved texture-atlas policy; no live renderer consumer yet", implemented: false)]
        public static void Set3DTextureAtlas(bool enabled) => _textureAtlas3D = enabled;

        [EngineCommand("Engine.GetAtlasMaxSize()", "Rendering", phase: 10,
            description: "Reserved texture-atlas limit; no live renderer consumer yet", implemented: false)]
        public static float GetAtlasMaxSize() => _atlasMaxSize;

        [EngineCommand("Engine.SetAtlasMaxSize(size)", "Rendering", phase: 10,
            description: "Reserved texture-atlas limit; no live renderer consumer yet", implemented: false)]
        public static void SetAtlasMaxSize(float size) => _atlasMaxSize = size;

        [EngineCommand("Engine.GetBackfaceCulling()", "Rendering", phase: 10,
            description: "Check if backface culling is enabled")]
        public static bool GetBackfaceCulling() => _backfaceCulling;

        [EngineCommand("Engine.SetBackfaceCulling(enabled)", "Rendering", phase: 10,
            description: "Enable or disable backface culling")]
        public static void SetBackfaceCulling(bool enabled)
        {
            _backfaceCulling = enabled;
            _faceCulling = enabled ? 0f : 2f;
            _cullingFaceMode = enabled ? "Back" : "Off";
            CullingChanged?.Invoke(_cullingWinding, _cullingFaceMode);
        }

        [EngineCommand("Engine.GetDeferredLighting2D()", "Rendering", phase: 10,
            description: "Reserved until the 2D light-map renderer ships", implemented: false)]
        public static bool GetDeferredLighting2D() => _deferredLighting2D;

        [EngineCommand("Engine.SetDeferredLighting2D(enabled)", "Rendering", phase: 10,
            description: "Reserved until the 2D light-map renderer ships", implemented: false)]
        public static void SetDeferredLighting2D(bool enabled)
        {
            _deferredLighting2D = enabled;
            DeferredLighting2DChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.GetDeferredLighting3D()", "Rendering", phase: 10,
            description: "Reserved; the production 3D renderer is currently forward", implemented: false)]
        public static bool GetDeferredLighting3D() => _deferredLighting3D;

        [EngineCommand("Engine.SetDeferredLighting3D(enabled)", "Rendering", phase: 10,
            description: "Reserved; the production 3D renderer is currently forward", implemented: false)]
        public static void SetDeferredLighting3D(bool enabled)
        {
            _deferredLighting3D = enabled;
            DeferredLighting3DChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.GetFaceCulling()", "Rendering", phase: 10,
            description: "Get face culling mode: 0=Back, 1=Front, 2=None")]
        public static float GetFaceCulling() => _faceCulling;

        [EngineCommand("Engine.SetFaceCulling(mode)", "Rendering", phase: 10,
            description: "Set face culling mode: 0=Back, 1=Front, 2=None")]
        public static void SetFaceCulling(float mode)
        {
            _faceCulling = mode switch { >= 1.5f => 2f, >= 0.5f => 1f, _ => 0f };
            _cullingFaceMode = _faceCulling switch { 1f => "Front", 2f => "Off", _ => "Back" };
            _backfaceCulling = _faceCulling != 2f;
            CullingChanged?.Invoke(_cullingWinding, _cullingFaceMode);
        }

        [EngineCommand("Engine.GetFirstPersonCamera()", "Camera", phase: 10,
            description: "Reserved legacy toggle; first-person is authored through camera PGSL", implemented: false)]
        public static float GetFirstPersonCamera() => _firstPersonCamera;

        [EngineCommand("Engine.EnableFirstPersonCamera(enabled)", "Camera", phase: 10,
            description: "Reserved legacy toggle; first-person is authored through camera PGSL", implemented: false)]
        public static void EnableFirstPersonCamera(bool enabled) => _firstPersonCamera = enabled ? 1f : 0f;

        [EngineCommand("Engine.GetFogEnabled()", "Rendering", phase: 10,
            description: "Check if fog is enabled")]
        public static bool GetFogEnabled() => _fogEnabled;

        [EngineCommand("Engine.GetFrustumCulling()", "Rendering", phase: 10,
            description: "Check if frustum culling is enabled")]
        public static bool GetFrustumCulling() => Genesis.Shared.Rendering.RenderAutoState.FrustumCulling;

        [EngineCommand("Engine.SetFrustumCulling(enabled)", "Rendering", phase: 10,
            description: "Enable or disable frustum culling")]
        public static void SetFrustumCulling(bool enabled)
        {
            _frustumCulling = enabled;
            Genesis.Shared.Rendering.RenderAutoState.FrustumCulling = enabled;
            FrustumCullingChanged?.Invoke(enabled);
        }

        /// <summary>Raised when frustum culling toggles; host syncs <c>RendererOptions</c>.</summary>
        public static event Action<bool> FrustumCullingChanged;

        [EngineCommand("Engine.GetGlobalLighting()", "Rendering", phase: 10,
            description: "Check if global lighting is enabled")]
        public static bool GetGlobalLighting() => _globalLighting;

        [EngineCommand("Engine.SetGlobalLighting(enabled)", "Rendering", phase: 10,
            description: "Enable or disable global lighting")]
        public static void SetGlobalLighting(bool enabled)
        {
            _globalLighting = enabled;
            _lighting = enabled;
            LightingChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.GetGlobalShadows()", "Rendering", phase: 10,
            description: "Check if scene shadows are enabled")]
        public static bool GetGlobalShadows() => _globalShadows;

        [EngineCommand("Engine.SetGlobalShadows(enabled)", "Rendering", phase: 10,
            description: "Enable or disable scene shadows")]
        public static void SetGlobalShadows(bool enabled)
        {
            _globalShadows = enabled;
            ShadowsChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.GetShadowStrength()", "Rendering", phase: 10,
            description: "Get global visible shadow darkness from 0 to 1")]
        public static float GetShadowStrength() => _shadowStrength;

        [EngineCommand("Engine.SetShadowStrength(strength)", "Rendering", phase: 10,
            description: "Set global visible shadow darkness from 0 to 1")]
        public static void SetShadowStrength(float strength)
        {
            _shadowStrength = Math.Clamp(strength, 0f, 1f);
            ShadowStrengthChanged?.Invoke(_shadowStrength);
        }

        [EngineCommand("Engine.GetGpuCulling()", "Rendering", phase: 10,
            description: "Reserved until the GPU compute cull pass ships", implemented: false)]
        public static bool GetGpuCulling() => Genesis.Shared.Rendering.RenderAutoState.GpuCulling;

        [EngineCommand("Engine.SetGpuCulling(enabled)", "Rendering", phase: 10,
            description: "Reserved until the GPU compute cull pass ships", implemented: false)]
        public static void SetGpuCulling(bool enabled)
        {
            _gpuCulling = enabled;
            Genesis.Shared.Rendering.RenderAutoState.GpuCulling = enabled;
        }

        [EngineCommand("Engine.GetLODFar()", "Rendering", phase: 10,
            description: "Get LOD far cull distance (meters)")]
        public static float GetLODFar() => Genesis.Shared.Rendering.LodPolicy.Current.Far;

        [EngineCommand("Engine.SetLODFar(distance)", "Rendering", phase: 10,
            description: "Set LOD far cull distance (meters)")]
        public static void SetLODFar(float distance)
        {
            _lodFar = distance;
            Genesis.Shared.Rendering.LodPolicy.Current.Far = distance;
            LodPolicyChanged?.Invoke();
        }

        [EngineCommand("Engine.GetLODMid()", "Rendering", phase: 10,
            description: "Get LOD mid distance (meters)")]
        public static float GetLODMid() => Genesis.Shared.Rendering.LodPolicy.Current.Mid;

        [EngineCommand("Engine.SetLODMid(distance)", "Rendering", phase: 10,
            description: "Set LOD mid distance (meters)")]
        public static void SetLODMid(float distance)
        {
            _lodMid = distance;
            Genesis.Shared.Rendering.LodPolicy.Current.Mid = distance;
            LodPolicyChanged?.Invoke();
        }

        [EngineCommand("Engine.GetLODNear()", "Rendering", phase: 10,
            description: "Get LOD near distance (meters)")]
        public static float GetLODNear() => Genesis.Shared.Rendering.LodPolicy.Current.Near;

        [EngineCommand("Engine.SetLODNear(distance)", "Rendering", phase: 10,
            description: "Set LOD near distance (meters)")]
        public static void SetLODNear(float distance)
        {
            _lodNear = distance;
            Genesis.Shared.Rendering.LodPolicy.Current.Near = distance;
            LodPolicyChanged?.Invoke();
        }

        [EngineCommand("Engine.GetLodScaling()", "Rendering", phase: 10,
            description: "Check if LOD scaling is enabled")]
        public static bool GetLodScaling() => Genesis.Shared.Rendering.LodPolicy.Current.ScalingEnabled;

        [EngineCommand("Engine.SetLodScaling(enabled)", "Rendering", phase: 10,
            description: "Enable or disable LOD scaling")]
        public static void SetLodScaling(bool enabled)
        {
            _lodScaling = enabled;
            Genesis.Shared.Rendering.LodPolicy.Current.ScalingEnabled = enabled;
            LodPolicyChanged?.Invoke();
        }

        /// <summary>Raised when Engine LOD distances/scale change.</summary>
        public static event Action LodPolicyChanged;

        [EngineCommand("Engine.GetOcclusionCulling()", "Rendering", phase: 10,
            description: "Check if software occlusion culling is enabled")]
        public static bool GetOcclusionCulling() => Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling;

        [EngineCommand("Engine.SetOcclusionCulling(enabled)", "Rendering", phase: 10,
            description: "Enable or disable software occlusion culling")]
        public static void SetOcclusionCulling(bool enabled)
        {
            _occlusionCulling = enabled;
            Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling = enabled;
        }

        [EngineCommand("Engine.NetDisconnect()", "Networking", phase: 2,
            description: "Disconnect and stop hosting/connecting.")]
        public static void NetDisconnect() => _net?.Disconnect();

        [EngineCommand("Engine.NetSend(peerId, tag, payload)", "Networking", phase: 2,
            description: "Send a tagged payload to one peer (peerId 0 = broadcast when host).")]
        public static void NetSend(int peerId, int tag, byte[] payload)
            => _net?.Send(peerId, tag, payload ?? Array.Empty<byte>());

        [EngineCommand("Engine.NetBroadcast(tag, payload)", "Networking", phase: 2,
            description: "Broadcast a tagged payload to all connected peers (host).")]
        public static void NetBroadcast(int tag, byte[] payload)
            => _net?.Broadcast(tag, payload ?? Array.Empty<byte>());

        [EngineCommand("Engine.NetPeerCount()", "Networking", phase: 2,
            description: "Number of connected peers.")]
        public static int NetPeerCount() => _net?.PeerCount ?? 0;

        [EngineCommand("Engine.NetIsHost()", "Networking", phase: 2,
            description: "1 when this process is hosting.")]
        public static float NetIsHost() => _net != null && _net.IsHost ? 1f : 0f;

        [EngineCommand("Engine.NetIsConnected()", "Networking", phase: 2,
            description: "1 when connected or hosting.")]
        public static float NetIsConnected() => _net != null && _net.IsConnected ? 1f : 0f;

        [EngineCommand("Engine.GetRenderMode()", "Rendering", phase: 10,
            description: "Reserved render-mode override; rooms currently select the live path", implemented: false)]
        public static string GetRenderMode() => _renderMode;

        [EngineCommand("Engine.SetRenderMode(mode)", "Rendering", phase: 10,
            description: "Reserved render-mode override; rooms currently select the live path", implemented: false)]
        public static void SetRenderMode(string mode) => _renderMode = mode;

        [EngineCommand("Engine.GetTextureAllocSize()", "Rendering", phase: 10,
            description: "Reserved texture allocation policy", implemented: false)]
        public static float GetTextureAllocSize() => _textureAllocSize;

        [EngineCommand("Engine.SetTextureAllocSize(size)", "Rendering", phase: 10,
            description: "Reserved texture allocation policy", implemented: false)]
        public static void SetTextureAllocSize(float size) => _textureAllocSize = size;

        [EngineCommand("Engine.GetTextureAtlasMaxSize()", "Rendering", phase: 10,
            description: "Reserved texture-atlas limit; no live renderer consumer yet", implemented: false)]
        public static float GetTextureAtlasMaxSize() => _atlasMaxSize;

        [EngineCommand("Engine.SetTextureAtlasMaxSize(size)", "Rendering", phase: 10,
            description: "Reserved texture-atlas limit; no live renderer consumer yet", implemented: false)]
        public static void SetTextureAtlasMaxSize(float size) => _atlasMaxSize = size;

        [EngineCommand("Engine.GetTextureMaxSize()", "Rendering", phase: 10,
            description: "Reserved texture limit; no live loader consumer yet", implemented: false)]
        public static float GetTextureMaxSize() => _textureMaxSize;

        [EngineCommand("Engine.SetTextureMaxSize(size)", "Rendering", phase: 10,
            description: "Reserved texture limit; no live loader consumer yet", implemented: false)]
        public static void SetTextureMaxSize(float size) => _textureMaxSize = size;

        [EngineCommand("Engine.GetVsync()", "Rendering", phase: 2,
            description: "Check if vertical sync (VSync) is enabled")]
        public static bool GetVsync() => _vsync;

        [EngineCommand("Engine.SetVsync(enabled)", "Rendering", phase: 2,
            description: "Enable or disable vertical sync (VSync)")]
        public static void SetVsync(bool enabled)
        {
            _vsync = enabled;
            VsyncChanged?.Invoke(enabled);
        }

        [EngineCommand("Engine.GetWindingOrder()", "Rendering", phase: 10,
            description: "Check if winding order is CCW (counter-clockwise)")]
        public static bool GetWindingOrder() => _windingOrderCcw;

        [EngineCommand("Engine.SetWindingOrder(ccw)", "Rendering", phase: 10,
            description: "Set winding order: true=CCW (counter-clockwise), false=CW")]
        public static void SetWindingOrder(bool ccw)
        {
            _windingOrderCcw = ccw;
            _cullingWinding = ccw ? "CCW" : "CW";
            CullingChanged?.Invoke(_cullingWinding, _cullingFaceMode);
        }

        [EngineCommand("Engine.GetZPrepass3D()", "Rendering", phase: 10,
            description: "Reserved until the production depth prepass ships", implemented: false)]
        public static bool GetZPrepass3D() => _zPrepass3D;

        [EngineCommand("Engine.SetZPrepass3D(enabled)", "Rendering", phase: 10,
            description: "Reserved until the production depth prepass ships", implemented: false)]
        public static void SetZPrepass3D(bool enabled) => _zPrepass3D = enabled;

        [EngineCommand("Engine.SetAmbientLight(r, g, b)", "Rendering", phase: 10,
            description: "Set ambient light color")]
        public static void SetAmbientLight(float r, float g, float b)
        {
            _ambientLight = new Vector3(r, g, b);
            AmbientLightChanged?.Invoke(_ambientLight);
        }

        [EngineCommand("Engine.SetCameraFov(degrees)", "Camera", phase: 10,
            description: "Override play camera field of view")]
        public static void SetCameraFov(float degrees)
        {
            _cameraFov = degrees;
            CameraFovChanged?.Invoke(degrees);
        }

        [EngineCommand("Engine.SetCameraNearPlane(distance)", "Camera", phase: 10,
            description: "Override play camera near clipping plane")]
        public static void SetCameraNearPlane(float distance)
        {
            _cameraNearPlane = distance;
            CameraNearPlaneChanged?.Invoke(distance);
        }

        [EngineCommand("Engine.SetCameraFarPlane(distance)", "Camera", phase: 10,
            description: "Override play camera far clipping plane")]
        public static void SetCameraFarPlane(float distance)
        {
            _cameraFarPlane = distance;
            CameraFarPlaneChanged?.Invoke(distance);
        }

        [EngineCommand("Engine.SetCameraPosition(x, y, z)", "Camera", phase: 10,
            description: "Override play camera position")]
        public static void SetCameraPosition(float x, float y, float z)
        {
            _cameraPosition = new Vector3(x, y, z);
            CameraPositionChanged?.Invoke(_cameraPosition);
        }

        [EngineCommand("Engine.SetCameraTarget(x, y, z)", "Camera", phase: 10,
            description: "Override play camera look-at target")]
        public static void SetCameraTarget(float x, float y, float z)
        {
            _cameraTarget = new Vector3(x, y, z);
            CameraTargetChanged?.Invoke(_cameraTarget);
        }

        [EngineCommand("Engine.SetGlobalLightColor(r, g, b)", "Rendering", phase: 10,
            description: "Set global light color")]
        public static void SetGlobalLightColor(float r, float g, float b)
        {
            _globalLightColor = new Vector3(r, g, b);
            GlobalLightColorChanged?.Invoke(_globalLightColor);
        }

        [EngineCommand("Engine.SetGlobalLightDirection(x, y, z)", "Rendering", phase: 10,
            description: "Set global light direction")]
        public static void SetGlobalLightDirection(float x, float y, float z)
        {
            _globalLightDirection = new Vector3(x, y, z);
            GlobalLightDirectionChanged?.Invoke(_globalLightDirection);
        }

        [EngineCommand("Engine.SetGlobalLightDirection2D(x, y)", "Rendering", phase: 10,
            description: "Reserved until the 2D light-map renderer ships", implemented: false)]
        public static void SetGlobalLightDirection2D(float x, float y)
        {
            _globalLightDirection2D = new Vector2(x, y);
            GlobalLightDirection2DChanged?.Invoke(_globalLightDirection2D);
        }

        [EngineCommand("Engine.SetFog(enabled, r, g, b, start, end, alpha=1)", "Rendering", phase: 10,
            description: "Configure fog. End-Start is thickness; in 2D start/end are authored Z/layer depth, in 3D they are camera distance. Alpha is maximum blend opacity.")]
        public static void SetFog(bool enabled, float r, float g, float b, float start, float end, float alpha = 1f)
        {
            _fogEnabled = enabled;
            _fogColour = new Vector4(r, g, b, Math.Clamp(alpha, 0f, 1f));
            _fogStart = new Vector3(start, 0, 0);
            _fogEnd = new Vector3(Math.Max(start + 0.001f, end), 0, 0);
            FogChanged?.Invoke(enabled);
            FogColourChanged?.Invoke(_fogColour);
            FogStartChanged?.Invoke(start);
            FogEndChanged?.Invoke(_fogEnd.X);
        }

        [EngineCommand("Engine.AddLight2D(x, y, radius, r, g, b, intensity)", "Rendering", phase: 10,
            description: "Reserved until the 2D light-map renderer ships", implemented: false)]
        public static void AddLight2D(float x, float y, float radius, float r, float g, float b, float intensity)
        {
            Light2DAdded?.Invoke(x, y, radius, r, g, b, intensity);
        }

        [EngineCommand("Engine.ClearLights2D()", "Rendering", phase: 10,
            description: "Reserved until the 2D light-map renderer ships", implemented: false)]
        public static void ClearLights2D()
        {
            Lights2DCleared?.Invoke();
        }

        [EngineCommand("Engine.ApplySettings()", "Rendering", phase: 2,
            description: "Reserved legacy commit point; live settings already apply immediately", implemented: false)]
        public static void ApplySettings()
        {
            SettingsApplied?.Invoke();
        }

        // ── Draw & Assets (unified command surface) ─────────────────────────────
        // These commands are the canonical, backend-agnostic way for gameplay
        // scripts (PGSL or C#) to load assets and submit 2D draws. They route
        // through EngineCommandPipeline so every call is profiled and visible to
        // the in-game debugger. The frame sink is canonical for sprites; submitted
        // events bridge primitives into the active render controller and remain
        // available as diagnostics/extension points.

        public static event Action<string, TextureHandle> AssetLoaded;
        public static event Action<SpriteDrawSpec> SpriteDrawSubmitted;
        public static event Action<RectDrawSpec>   RectDrawSubmitted;
        public static event Action<LineDrawSpec>   LineDrawSubmitted;
        public static event Action<TextDrawSpec>   TextDrawSubmitted;

        [EngineCommand("Engine.LoadAsset(path)", "Assets", phase: 0,
            description: "Load a project-relative texture through the asset pipeline; returns a TextureHandle")]
        public static TextureHandle LoadAsset(string path)
        {
            // ProjectGameContext / SandboxGameContext installs the synchronous resolver.
            // Observers receive the result, never the old pre-resolution Invalid placeholder.
            TextureHandle handle = ResolveAssetLoad(path);
            AssetLoaded?.Invoke(path, handle);
            return handle;
        }

        [EngineCommand("Engine.DrawSprite(texture, x, y)", "Drawing 2D", phase: 0,
            description: "Submit a sprite draw at (x,y) using the given texture handle or asset name")]
        public static void DrawSprite(object texture, float x, float y) =>
            DrawSprite(texture, x, y, 1f, 1f, 0f, 1f);

        [EngineCommand("Engine.DrawSprite(texture, x, y, scaleX, scaleY, rotation, alpha)", "Drawing 2D", phase: 0,
            description: "Submit a scaled/rotated sprite draw")]
        public static void DrawSprite(object texture, float x, float y, float scaleX, float scaleY, float rotation, float alpha)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected++;
                return;
            }

            var handle = CoerceTextureHandle(texture);
            var spec = new SpriteDrawSpec
            {
                Texture = handle,
                X = x, Y = y, ScaleX = scaleX, ScaleY = scaleY,
                Rotation = rotation, Alpha = alpha,
            };

            // Preferred path: enqueue into the frame command sink (batched flush).
            if (_drawCommandSink != null && handle.IsValid)
            {
                float w = MathF.Abs(scaleX) < 1e-4f ? 32f : MathF.Abs(scaleX) * 32f;
                float h = MathF.Abs(scaleY) < 1e-4f ? 32f : MathF.Abs(scaleY) * 32f;
                // When callers pass pixel sizes (>2), treat Scale as width/height directly.
                if (MathF.Abs(scaleX) > 2f || MathF.Abs(scaleY) > 2f)
                {
                    w = MathF.Abs(scaleX);
                    h = MathF.Abs(scaleY);
                }

                _drawCommandSink.DrawSprite(new SpriteDrawCall
                {
                    Texture = handle,
                    X = x,
                    Y = y,
                    Width = w,
                    Height = h,
                    OriginX = w * 0.5f,
                    OriginY = h * 0.5f,
                    Rotation = rotation,
                    Alpha = alpha,
                    Tint = RenderColor.White,
                    ScaleX = scaleX < 0f ? -1f : 1f,
                    ScaleY = scaleY < 0f ? -1f : 1f,
                });
            }

            SpriteDrawSubmitted?.Invoke(spec);
        }

        private static Genesis.Shared.Interfaces.IRenderCommandSink _drawCommandSink;
        /// <summary>Host binds the active frame queue so Engine.DrawSprite enqueues SpriteDrawCall.</summary>
        public static void SetDrawCommandSink(Genesis.Shared.Interfaces.IRenderCommandSink sink)
            => _drawCommandSink = sink;

        [EngineCommand("Engine.DrawRect(x, y, w, h, r, g, b, a, filled)", "Drawing 2D", phase: 0,
            description: "Draw a rectangle outline (filled=false) or filled (filled=true)")]
        public static void DrawRect(float x, float y, float w, float h, float r, float g, float b, float a, bool filled)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected++;
                return;
            }
            var spec = new RectDrawSpec { X = x, Y = y, W = w, H = h, R = r, G = g, B = b, A = a, Filled = filled };
            RectDrawSubmitted?.Invoke(spec);
        }

        [EngineCommand("Engine.DrawLine(x1, y1, x2, y2, r, g, b, a)", "Drawing 2D", phase: 0,
            description: "Draw a 2D line segment in the given colour")]
        public static void DrawLine(float x1, float y1, float x2, float y2, float r, float g, float b, float a)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected++;
                return;
            }
            var spec = new LineDrawSpec { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, R = r, G = g, B = b, A = a };
            LineDrawSubmitted?.Invoke(spec);
        }

        [EngineCommand("Engine.DrawText(text, x, y, size, r, g, b, a)", "Drawing 2D", phase: 0,
            description: "Draw HUD text at (x,y) in the given size and colour")]
        public static void DrawText(string text, float x, float y, float size, float r, float g, float b, float a)
        {
            if (!Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit)
            {
                Genesis.Shared.Rendering.RenderAutoState.DrawSubmitRejected++;
                return;
            }
            var spec = new TextDrawSpec { Text = text ?? string.Empty, X = x, Y = y, Size = size, R = r, G = g, B = b, A = a };
            TextDrawSubmitted?.Invoke(spec);
        }

        // ── Audio / Net / Debug namespace anchors ───────────────────────────────
        // Audio is now wired (Phase 2.1): the host installs an IAudioSystem resolver
        // (Engine.SetAudioSystem) so these commands route to the real XAudio2 runtime.
        // Net/Debug anchors remain Phase 2.2/2.3.

        private static Func<string, int> _audioLoad;        // host installs: path → soundId
        private static Func<int, float, float, bool, int> _audioPlay;  // soundId,vol,pitch,loop → channelId

        /// <summary>Host (ProjectPlayerApp) installs the audio service so Engine.Audio.* commands resolve.</summary>
        public static void SetAudioSystem(
            Func<string, int> loadSound,
            Func<int, float, float, bool, int> playSound)
        {
            _audioLoad = loadSound;
            _audioPlay = playSound;
        }

        [EngineCommand("Engine.AudioPlay(name, volume, pitch, loop)", "Audio", phase: 2,
            description: "Play a project audio asset. Returns a channel id (0 = failed).")]
        public static int AudioPlay(string name, float volume, float pitch, bool loop)
        {
            if (_audioLoad == null || _audioPlay == null)
                throw new NotImplementedException("Engine.AudioPlay — no audio host attached (sandbox/test).");
            int soundId = _audioLoad(name);
            if (soundId == 0) return 0;
            return _audioPlay(soundId, volume, pitch, loop);
        }

        [EngineCommand("Engine.AudioStop(channel)", "Audio", phase: 2,
            description: "Stop a playing audio channel.")]
        public static void AudioStop(int channel)
        {
            // Channel stop is handled by the host's IAudioSystem; the Engine facade
            // forwards via a stop resolver when one is installed, else no-op.
            _audioStop?.Invoke(channel);
        }
        private static Action<int> _audioStop;
        /// <summary>Optional: host installs channel-stop forwarding.</summary>
        public static void SetAudioStop(Action<int> stop) => _audioStop = stop;

        [EngineCommand("Engine.AudioMasterVolume(volume)", "Audio", phase: 2,
            description: "Set the master output volume (0..1).")]
        public static void AudioMasterVolume(float volume)
        {
            _audioMasterVolume?.Invoke(volume);
        }
        private static Action<float> _audioMasterVolume;
        /// <summary>Optional: host installs master-volume forwarding.</summary>
        public static void SetAudioMasterVolume(Action<float> set) => _audioMasterVolume = set;

        [EngineCommand("Engine.NetHost(port)", "Networking", phase: 2,
            description: "Begin hosting a multiplayer game on the given port.")]
        public static void NetHost(int port)
        {
            _net?.Host(port);
        }

        [EngineCommand("Engine.NetConnect(ip, port)", "Networking", phase: 2,
            description: "Connect to a hosted game at ip:port.")]
        public static void NetConnect(string ip, int port)
        {
            _net?.Connect(ip, port);
        }

        private static Genesis.Shared.Net.IGameNetwork _net;
        /// <summary>Host installs the network service so Engine.Net.* commands resolve.</summary>
        public static void SetNetwork(Genesis.Shared.Net.IGameNetwork net) => _net = net;

        [EngineCommand("Engine.DebugWatch(name, value)", "Debug", phase: 2,
            description: "Publish a named value to the in-game debugger's Watch window.")]
        public static void DebugWatch(string name, object value)
        {
            // Lightweight — safe to implement now: just stash into the debug sink.
            DebugWatchChanged?.Invoke(name, value);
        }

        [EngineCommand("Engine.DebugLog(message)", "Debug", phase: 2,
            description: "Write a line to the in-game debugger console.")]
        public static void DebugLog(string message) => DebugWatchChanged?.Invoke("log", message);

        public static event Action<string, object> DebugWatchChanged;

        // ── Draw-spec value types (backend-agnostic command payloads) ───────────

        public struct SpriteDrawSpec
        {
            public TextureHandle Texture;
            public float X, Y, ScaleX, ScaleY, Rotation, Alpha;
        }
        public struct RectDrawSpec   { public float X, Y, W, H, R, G, B, A; public bool Filled; }
        public struct LineDrawSpec   { public float X1, Y1, X2, Y2, R, G, B, A; }
        public struct TextDrawSpec   { public string Text; public float X, Y, Size, R, G, B, A; }

        // ── Draw-command helpers ─────────────────────────────────────────────────

        private static TextureHandle ResolveAssetLoad(string path)
        {
            // The active IGameContext (held by ScriptHostSystem at runtime, or
            // SandboxGameContext in the editor) performs the actual load. Until
            // the host registers a resolver, returns Invalid so callers degrade
            // gracefully. Wired fully in Phase 0.3 (SandboxGameContext) and 2.x.
            return _assetResolver?.Invoke(path) ?? TextureHandle.Invalid;
        }

        private static Func<string, TextureHandle> _assetResolver;
        /// <summary>Host installs this so <see cref="LoadAsset"/> can resolve synchronously.</summary>
        public static void SetAssetResolver(Func<string, TextureHandle> resolver) => _assetResolver = resolver;

        private static TextureHandle CoerceTextureHandle(object texture)
        {
            if (texture is TextureHandle h) return h;
            if (texture is string path) return ResolveAssetLoad(path);
            return TextureHandle.Invalid;
        }
    }
}
