using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Genesis.Runtime.Assets;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.ECS;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting
{
    public sealed class PgslBehavior : EntityBehavior
    {
        private readonly string _scriptName;
        private readonly PgslContext _ctx;
        private CompiledScriptAsset _onCreateScript;
        private CompiledScriptAsset _onGameStartScript;
        private CompiledScriptAsset _onRoomStartScript;
        private CompiledScriptAsset _onStepBeginScript;
        private CompiledScriptAsset _onUpdateScript;
        private CompiledScriptAsset _onStepEndScript;
        private CompiledScriptAsset _onDrawScript;
        private CompiledScriptAsset _onDrawGuiScript;
        private CompiledScriptAsset _onKeyPressedScript;
        private CompiledScriptAsset _onKeyHeldScript;
        private CompiledScriptAsset _onKeyReleasedScript;
        private CompiledScriptAsset _onMouseLeftPressedScript;
        private CompiledScriptAsset _onMouseRightPressedScript;
        private CompiledScriptAsset _onMouseEnterScript;
        private CompiledScriptAsset _onMouseLeaveScript;
        private CompiledScriptAsset _onCollisionScript;
        private CompiledScriptAsset _onRoomEndScript;
        private CompiledScriptAsset _onGameEndScript;
        private CompiledScriptAsset _onDestroyScript;
        private readonly CompiledScriptAsset[] _alarmScripts = new CompiledScriptAsset[ObjectEventCatalog.AlarmCount];
        private readonly CompiledScriptAsset[] _userEventScripts = new CompiledScriptAsset[ObjectEventCatalog.UserEventCount];
        private readonly Queue<int> _pendingUserEvents = new();
        private PgslVm _vm;
        private int _executionDepth;
        private bool _drainingUserEvents;
        private bool _mouseInside;

        public string ScriptName => _scriptName;
        public PgslContext Context => _ctx;

        /// <summary>
        /// Event sources handed over by the loader, keyed by event id. When present these win over
        /// any registry lookup.
        /// </summary>
        /// <remarks>
        /// This is the T2 fix. Previously the behaviour resolved its events out of the *global*
        /// <see cref="ScriptAssetRegistry"/> by appending suffixes to a name, which meant the editor's
        /// filename convention and the runtime's lookup had to agree — and when they didn't, Create ran
        /// every frame and Draw never registered at all (NEXT-044). An object now simply carries its own
        /// event code, so there is no name to get wrong.
        /// </remarks>
        private IReadOnlyDictionary<string, string> _inlineEvents;
        private IReadOnlyDictionary<string, string> _exposedFieldOverrides;

        public PgslBehavior(string scriptName)
        {
            _scriptName = scriptName;
            _ctx = new PgslContext();
        }

        /// <summary>
        /// Bind event code directly, bypassing the script-name registry. Call before OnCreate.
        /// </summary>
        public void SetEventSources(IReadOnlyDictionary<string, string> events) => _inlineEvents = events;

        /// <summary>
        /// Supplies serialized <c>field:Name</c> values for this one VM instance. Matching
        /// file-scope declaration literals are specialized while compiling the in-memory event
        /// source, before Create runs; the shared source files remain byte-for-byte unchanged.
        /// </summary>
        public void SetExposedFieldOverrides(IReadOnlyDictionary<string, string> props)
        {
            if (props is null || props.Count == 0)
            {
                _exposedFieldOverrides = null;
                return;
            }

            Dictionary<string, string> fields = new(System.StringComparer.Ordinal);
            foreach ((string property, string value) in props)
            {
                if (property is null || !property.StartsWith(ScriptHostSystem.FieldPrefix,
                        System.StringComparison.OrdinalIgnoreCase)) continue;
                string name = property.Substring(ScriptHostSystem.FieldPrefix.Length);
                if (!string.IsNullOrWhiteSpace(name)) fields[name] = value ?? string.Empty;
            }
            _exposedFieldOverrides = fields.Count == 0 ? null : fields;
        }

        public override void OnCreate()
        {
            InitializeVm();
            SyncToContext();
            ExecuteScript(_onCreateScript, "Create");
            SyncFromContext();
        }

        public override void OnGameStart() => ExecuteLifecycle(_onGameStartScript, "GameStart");

        public override void OnRoomStart() => ExecuteLifecycle(_onRoomStartScript, "RoomStart");

        public override void OnStepBegin(float dt) => ExecuteLifecycle(_onStepBeginScript, "StepBegin");

        public override void OnUpdate(float dt)
        {
            SyncToContext();
            ExecuteScript(_onUpdateScript, "Step");
            DispatchAlarms();
            SyncFromContext();
        }

        public override void OnInputEvents()
        {
            Genesis.Runtime.Input.InputState input = Game.Input;
            if (input == null) return;

            SyncToContext();
            if (input.AnyKeyPressed) ExecuteScript(_onKeyPressedScript, "KeyPressed");
            if (input.AnyKeyDown) ExecuteScript(_onKeyHeldScript, "KeyHeld");
            if (input.AnyKeyReleased) ExecuteScript(_onKeyReleasedScript, "KeyReleased");
            if (input.WasPressed(Genesis.Runtime.Input.MouseButton.Left))
                ExecuteScript(_onMouseLeftPressedScript, "MouseLeftPressed");
            if (input.WasPressed(Genesis.Runtime.Input.MouseButton.Right))
                ExecuteScript(_onMouseRightPressedScript, "MouseRightPressed");

            bool inside = IsMouseInside(input.MousePosition.X, input.MousePosition.Y);
            if (inside && !_mouseInside) ExecuteScript(_onMouseEnterScript, "MouseEnter");
            if (!inside && _mouseInside) ExecuteScript(_onMouseLeaveScript, "MouseLeave");
            _mouseInside = inside;
            SyncFromContext();
        }

        public override void OnStepEnd(float dt) => ExecuteLifecycle(_onStepEndScript, "StepEnd");

        public override void OnCollision(Entity other)
        {
            SyncToContext();
            // Set the special Other variable in context/bridge
            _ctx.Variables["Other"] = other;
            ExecuteScript(_onCollisionScript, "Collision");
            SyncFromContext();
        }

        public override void OnDestroy()
        {
            SyncToContext();
            ExecuteScript(_onDestroyScript, "Destroy");
            SyncFromContext();
        }

        public override void OnRoomEnd() => ExecuteLifecycle(_onRoomEndScript, "RoomEnd");

        public override void OnGameEnd() => ExecuteLifecycle(_onGameEndScript, "GameEnd");

        /// <summary>Whether this behaviour has a Draw event worth running during the draw phase.</summary>
        public bool HasDrawScript => HasWorldDrawScript || HasGuiDrawScript;
        public bool HasWorldDrawScript => _onDrawScript?.CompileResult != null;
        public bool HasGuiDrawScript => _onDrawGuiScript?.CompileResult != null;

        /// <summary>
        /// Runs the object's PGSL <c>Draw</c> event against a live draw surface. Called from the
        /// render phase (which is the only place a renderer exists) rather than from Update, so
        /// script-issued draw calls land in the current frame — see
        /// <see cref="PgslRenderDrawSurface"/> and NEXT-033.
        /// </summary>
        public void OnDrawFrame(IPgslDrawSurface surface) => OnDrawGuiFrame(surface);

        public void OnDrawWorldFrame(IPgslDrawSurface surface) =>
            ExecuteDraw(_onDrawScript, "Draw", surface);

        public void OnDrawGuiFrame(IPgslDrawSurface surface) =>
            ExecuteDraw(_onDrawGuiScript, "DrawGui", surface);

        private void ExecuteDraw(CompiledScriptAsset script, string eventName, IPgslDrawSurface surface)
        {
            if (script?.CompileResult == null || surface == null) return;
            SyncToContext();
            _ctx.DrawSurface = surface;
            try
            {
                ExecuteScript(script, eventName);
            }
            finally
            {
                _ctx.DrawSurface = null;
            }

            SyncFromContext();
        }

        /// <summary>Snapshot of persistent VM variables, used by the debugger and conformance tests.</summary>
        public IReadOnlyDictionary<string, object> GetVariablesSnapshot() =>
            _vm?.GetVariables() ?? new Dictionary<string, object>();

        /// <summary>Edit the retained VM instance without replaying Create or losing its ECS bindings.</summary>
        public bool TrySetLiveValue(string name, object value)
        {
            if (_vm is null) return false;
            var previous = VMEngine.Bridge.GetContext();
            var previousCommands = PgslCommands.BindContext(_ctx);
            try
            {
                SyncToContext(); VMEngine.Bridge.SetContext(_ctx);
                bool changed = _vm.TrySetLiveVariable(name, value);
                if (changed) SyncFromContext();
                return changed;
            }
            finally { VMEngine.Bridge.SetContext(previous); PgslCommands.BindContext(previousCommands); }
        }

        private void InitializeVm()
        {
            try
            {
                if (!VMEngine.IsInitialized) VMEngine.CreateVm();
                _vm = VMEngine.CreateVm(debug: false);
                _ctx.ActiveVm = _vm;
                _ctx.UserEventCallback = QueueUserEvent;

                // Standalone project scripts are callable by filename (Helper(...) / scr_Helper(...)).
                // Load them once before any object event can execute.
                // An unwired preview has no project. Its relative "." must not turn the
                // Studio checkout (including build outputs) into a game script library.
                string projectPath = Game is NullGameContext nullContext
                    && string.IsNullOrWhiteSpace(nullContext.ProjectPath)
                    ? null : Game.ResolveAssetPath(".");
                PgslCommands.ProjectPath = projectPath;
                if (!string.IsNullOrWhiteSpace(projectPath) && System.IO.Directory.Exists(projectPath))
                    ScriptAssetRegistry.EnsureProjectLoaded(projectPath);

                if (_inlineEvents is { Count: > 0 })
                {
                    // The object carried its own event code. No global names, no suffix convention,
                    // nothing to collide with — see SetEventSources.
                    _onCreateScript = CompileInline("Create");
                    _onGameStartScript = CompileInline("GameStart");
                    _onRoomStartScript = CompileInline("RoomStart");
                    _onStepBeginScript = CompileInline("StepBegin");
                    _onUpdateScript = CompileInline("Step");
                    _onStepEndScript = CompileInline("StepEnd");
                    _onDrawScript = CompileInline("Draw");
                    _onDrawGuiScript = CompileInline("DrawGui");
                    _onKeyPressedScript = CompileInline("KeyPressed");
                    _onKeyHeldScript = CompileInline("KeyHeld");
                    _onKeyReleasedScript = CompileInline("KeyReleased");
                    _onMouseLeftPressedScript = CompileInline("MouseLeftPressed");
                    _onMouseRightPressedScript = CompileInline("MouseRightPressed");
                    _onMouseEnterScript = CompileInline("MouseEnter");
                    _onMouseLeaveScript = CompileInline("MouseLeave");
                    _onCollisionScript = CompileInline("Collision");
                    _onRoomEndScript = CompileInline("RoomEnd");
                    _onGameEndScript = CompileInline("GameEnd");
                    _onDestroyScript = CompileInline("Destroy");
                    for (int slot = 0; slot < _alarmScripts.Length; slot++)
                        _alarmScripts[slot] = CompileInline(ObjectEventCatalog.AlarmEventId(slot));
                    for (int slot = 0; slot < _userEventScripts.Length; slot++)
                        _userEventScripts[slot] = CompileInline($"UserEvent{slot}");
                    return;
                }

                // Legacy path: resolve by script name from the global registry. Retained so a project
                // whose objects predate per-object event folders still runs.
                ScriptAssetRegistry.TryGet(_scriptName + "_Create", out _onCreateScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_GameStart", out _onGameStartScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_RoomStart", out _onRoomStartScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_StepBegin", out _onStepBeginScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_Update", out _onUpdateScript);
                if (_onUpdateScript == null)
                {
                    // Fallback to the main script name as OnUpdate
                    ScriptAssetRegistry.TryGet(_scriptName, out _onUpdateScript);
                }
                ScriptAssetRegistry.TryGet(_scriptName + "_StepEnd", out _onStepEndScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_Draw", out _onDrawScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_DrawGui", out _onDrawGuiScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_KeyPressed", out _onKeyPressedScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_KeyHeld", out _onKeyHeldScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_KeyReleased", out _onKeyReleasedScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_MouseLeftPressed", out _onMouseLeftPressedScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_MouseRightPressed", out _onMouseRightPressedScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_MouseEnter", out _onMouseEnterScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_MouseLeave", out _onMouseLeaveScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_Collision", out _onCollisionScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_RoomEnd", out _onRoomEndScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_GameEnd", out _onGameEndScript);
                ScriptAssetRegistry.TryGet(_scriptName + "_Destroy", out _onDestroyScript);
                for (int slot = 0; slot < _alarmScripts.Length; slot++)
                    ScriptAssetRegistry.TryGet(_scriptName + "_Alarm" + slot, out _alarmScripts[slot]);
                for (int slot = 0; slot < _userEventScripts.Length; slot++)
                    ScriptAssetRegistry.TryGet(_scriptName + "_UserEvent" + slot, out _userEventScripts[slot]);
            }
            catch (Exception ex)
            {
                throw ex is PgslExecutionException
                    ? ex
                    : new PgslExecutionException(_scriptName, "Load", ex);
            }
        }

        /// <summary>
        /// Compile one of the object's own event scripts. Registered under a per-object cache key so
        /// two objects with an identically-named event cannot share (or overwrite) each other's code.
        /// </summary>
        private CompiledScriptAsset CompileInline(string eventId)
        {
            if (_inlineEvents is null
                || !_inlineEvents.TryGetValue(eventId, out string source)
                || string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            string effectiveSource = PgslExposedVariables.ApplyOverrides(source, _exposedFieldOverrides);
            string key = $"__object::{_scriptName}::{eventId}";
            if (!string.Equals(source, effectiveSource, System.StringComparison.Ordinal))
                key += "::" + ScriptAssetCompiler.ComputeSha256(effectiveSource).Substring(0, 12);
            ScriptAssetRegistry.Register(key, effectiveSource);
            return ScriptAssetRegistry.TryGet(key, out CompiledScriptAsset asset) ? asset : null;
        }

        private void ExecuteScript(CompiledScriptAsset asset, string eventName)
        {
            if (asset?.CompileResult == null || _vm == null) return;
            bool profiling = PgslProfiler.Enabled;
            long profileStart = profiling ? Stopwatch.GetTimestamp() : 0;
            bool failed = false;
            var previousBridge = VMEngine.Bridge.GetContext();
            var previousCommands = PgslCommands.BindContext(_ctx);
            var previousGame = PgslCommands.ActiveGameContext;
            string previousProject = PgslCommands.ProjectPath;
            _executionDepth++;
            try
            {
                // Push active context to VM static bridge
                VMEngine.Bridge.SetContext(_ctx);
                PgslCommands.SetContext(_ctx);
                PgslCommands.ActiveGameContext = Game;
                PgslCommands.ProjectPath = Game.ResolveAssetPath(".");

                _vm.Execute(asset.CompileResult.Instructions, asset.CompileResult.Constants, clearVariables: false);
            }
            catch (ReturnException)
            {
                // PgslVm uses ReturnException only to unwind a top-level event `return;`. User
                // function returns are already consumed inside the VM. Reaching here therefore
                // means the event completed intentionally; treating it as a script fault prevented
                // SyncFromContext and filled the debugger with false-positive red diagnostics.
            }
            catch (Exception ex)
            {
                failed = true;
                throw ex is PgslExecutionException
                    ? ex
                    : new PgslExecutionException(_scriptName, eventName, ex);
            }
            finally
            {
                _executionDepth--;
                VMEngine.Bridge.SetContext(previousBridge); PgslCommands.BindContext(previousCommands);
                PgslCommands.ActiveGameContext = previousGame; PgslCommands.ProjectPath = previousProject;
                if (profiling)
                {
                    PgslProfiler.Record(
                        _scriptName,
                        eventName,
                        Entity.Id,
                        Stopwatch.GetTimestamp() - profileStart,
                        failed);
                }
            }

            if (_executionDepth == 0 && !_drainingUserEvents)
                DrainUserEvents();
        }

        private void ExecuteLifecycle(CompiledScriptAsset script, string eventName)
        {
            if (script?.CompileResult == null) return;
            SyncToContext();
            ExecuteScript(script, eventName);
            SyncFromContext();
        }

        private void QueueUserEvent(int slot)
        {
            if (slot is < 0 or >= ObjectEventCatalog.UserEventCount) return;
            _pendingUserEvents.Enqueue(slot);
            if (_executionDepth == 0 && !_drainingUserEvents)
                DrainUserEvents();
        }

        private void DrainUserEvents()
        {
            if (_drainingUserEvents || _pendingUserEvents.Count == 0) return;
            _drainingUserEvents = true;
            try
            {
                int dispatched = 0;
                while (_pendingUserEvents.Count > 0)
                {
                    if (++dispatched > 1024)
                    {
                        _pendingUserEvents.Clear();
                        throw new PgslExecutionException(
                            _scriptName,
                            "UserEvent",
                            new InvalidOperationException(
                                "More than 1024 user events were queued by one dispatch; possible recursive EventUser loop."));
                    }

                    int slot = _pendingUserEvents.Dequeue();
                    ExecuteScript(_userEventScripts[slot], $"UserEvent{slot}");
                }
            }
            finally
            {
                _drainingUserEvents = false;
            }
        }

        private bool IsMouseInside(float mouseX, float mouseY)
        {
            if (World == null || !World.IsAlive(Entity) || !HasComponent<TransformComponent>())
                return false;

            ref TransformComponent transform = ref GetComponent<TransformComponent>();
            // Object sprites use a 32x32 fallback when no image metadata is available. Match that
            // deterministic footprint here; scaling expands the hit region with the visible object.
            float halfWidth = 16f * MathF.Max(0.01f, MathF.Abs(transform.ScaleX));
            float halfHeight = 16f * MathF.Max(0.01f, MathF.Abs(transform.ScaleY));
            return mouseX >= transform.X - halfWidth
                && mouseX <= transform.X + halfWidth
                && mouseY >= transform.Y - halfHeight
                && mouseY <= transform.Y + halfHeight;
        }

        private void DispatchAlarms()
        {
            // Alarms count down once per game step and execute immediately on the frame they reach
            // zero. This mirrors ObjectSandbox instead of advertising Alarm0..11 only in the editor.
            // ExecuteScript restores the caller's command context when Step returns. Alarm ticking
            // is host work, so bind this instance explicitly instead of accidentally ticking the
            // last unrelated object (or no context at all).
            PgslContext previous = PgslCommands.BindContext(_ctx);
            double firedMask;
            try { firedMask = PgslCommands.TickAlarms(1); }
            finally { PgslCommands.BindContext(previous); }
            for (int slot = 0; slot < _alarmScripts.Length; slot++)
            {
                if (PgslCommands.AlarmFired(firedMask, slot))
                    ExecuteScript(_alarmScripts[slot], ObjectEventCatalog.AlarmEventId(slot));
            }
        }

        private void SyncToContext()
        {
            _ctx.InstanceId = Entity.Id;

            if (HasComponent<TransformComponent>())
            {
                ref var t = ref GetComponent<TransformComponent>();
                _ctx.X = t.X;
                _ctx.Y = t.Y;
                _ctx.Z = t.Z;
                _ctx.ImageAngle = t.Rotation;
                _ctx.ImageXScale = t.ScaleX;
                _ctx.ImageYScale = t.ScaleY;
            }

            if (HasComponent<PhysicsComponent>())
            {
                ref var p = ref GetComponent<PhysicsComponent>();
                _ctx.HSpeed = p.HSpeed;
                _ctx.VSpeed = p.VSpeed;
                _ctx.Gravity = p.Gravity;
                _ctx.GravityDirection = p.GravityDirection;
                _ctx.Friction = p.Friction;
                _ctx.Solid = p.Solid;
            }

            if (HasComponent<SpriteComponent>())
            {
                ref var s = ref GetComponent<SpriteComponent>();
                if (ObjectDrawAssetRegistry.TryGet(Entity, out ObjectDrawAssetEntry assets)
                    && !string.IsNullOrWhiteSpace(assets.Image))
                {
                    _ctx.SpriteIndex = assets.Image;
                    _ctx.SpriteTransitionPreviousImage = assets.SpriteTransitionPreviousImage ?? string.Empty;
                    _ctx.SpriteTransitionPreviousFrame = assets.SpriteTransitionPreviousFrame;
                    _ctx.SpriteTransitionDuration = assets.SpriteTransitionDuration;
                    _ctx.SpriteTransitionElapsed = assets.SpriteTransitionElapsed;
                }
                else
                {
                    _ctx.SpriteIndex = s.SpriteIndex.ToString();
                }
                _ctx.ImageIndex = s.ImageIndex;
                _ctx.ImageSpeed = s.ImageSpeed;
                _ctx.SpriteAnimationSpeed = s.ImageSpeed;
                _ctx.SpriteAnimationActive = s.ImageSpeed != 0f;
                _ctx.ImageAlpha = s.Alpha;
                _ctx.Depth = s.Depth;

                if (TryResolveSpriteAsset(_ctx.SpriteIndex, out SpriteRuntimeAsset asset)
                    && s.AnimationTagIndex >= 0
                    && s.AnimationTagIndex < asset.Tags.Count)
                {
                    _ctx.SpriteAnimationTag = asset.Tags[s.AnimationTagIndex].Name;
                    _ctx.SpriteAnimationLoop = s.AnimationLoopOverride switch
                    {
                        0 => false,
                        1 => true,
                        _ => asset.Tags[s.AnimationTagIndex].Loop,
                    };
                }
            }

            if (HasComponent<ModelRendererComponent>())
            {
                ref var model = ref GetComponent<ModelRendererComponent>();
                _ctx.ModelAsset = !string.IsNullOrWhiteSpace(model.ModelAsset)
                    ? model.ModelAsset
                    : ObjectDrawAssetRegistry.TryGet(Entity, out ObjectDrawAssetEntry assets)
                        ? assets.Model ?? string.Empty
                        : string.Empty;
                _ctx.ModelKeepPreviousTransform = model.KeepPreviousTransform;
            }

            if (HasComponent<PointLightComponent>())
            {
                ref var light = ref GetComponent<PointLightComponent>();
                _ctx.LightEmitterEnabled = light.Enabled;
                _ctx.LightEmitterColor = light.Color;
                _ctx.LightEmitterSecondaryColor = light.SecondaryColor;
                _ctx.LightEmitterTertiaryColor = light.TertiaryColor;
                _ctx.LightEmitterOffset = light.Offset;
                _ctx.LightEmitterRadius = light.Radius;
                _ctx.LightEmitterIntensity = light.Intensity;
                _ctx.LightEmitterFalloff = light.Falloff <= 0f ? 2f : light.Falloff;
                _ctx.LightEmitterAction = light.Action.ToString();
                _ctx.LightEmitterActionSpeed = light.ActionSpeed;
                _ctx.LightEmitterActionAmount = light.ActionAmount;
                _ctx.LightEmitterPhase = light.Phase;
                _ctx.LightEmitterColorCount = Math.Clamp(light.ColorCount, 1, 3);
            }

            if (HasComponent<ModelAnimatorComponent>())
            {
                ref var animator = ref GetComponent<ModelAnimatorComponent>();
                _ctx.ModelAnimationClip = animator.ClipName ?? string.Empty;
                _ctx.AnimationController = animator.Controller;
                _ctx.ModelAnimationPreviousClip = animator.PreviousClipName ?? string.Empty;
                _ctx.ModelAnimationTime = animator.TimeSeconds;
                _ctx.ModelAnimationPreviousTime = animator.PreviousTimeSeconds;
                _ctx.ModelAnimationFps = animator.ClipFps <= 0f ? 60f : animator.ClipFps;
                _ctx.ModelAnimationSpeed = animator.PlaybackSpeed;
                _ctx.ModelAnimationLoop = animator.Loop;
                _ctx.ModelAnimationActive = animator.Playing;
                _ctx.ModelAnimationBlendDuration = animator.BlendDuration;
                _ctx.ModelAnimationBlendElapsed = animator.BlendElapsed;
            }

            if (HasComponent<Draw2DComponent>())
            {
                ref var d = ref GetComponent<Draw2DComponent>();
                _ctx.Visible = d.Visible;
                _ctx.Depth = (int)d.Depth;
            }
            else if (HasComponent<Draw3DComponent>()) _ctx.Visible = GetComponent<Draw3DComponent>().Visible;
        }

        private void SyncFromContext()
        {
            if (HasComponent<TransformComponent>())
            {
                ref var t = ref GetComponent<TransformComponent>();
                var previousPosition = new System.Numerics.Vector3(t.X, t.Y, t.Z);
                t.X = (float)_ctx.X;
                t.Y = (float)_ctx.Y;
                t.Z = (float)_ctx.Z;
                t.Rotation = (float)_ctx.ImageAngle;
                t.ScaleX = (float)_ctx.ImageXScale;
                t.ScaleY = (float)_ctx.ImageYScale;
                if (previousPosition != new System.Numerics.Vector3(t.X, t.Y, t.Z)
                    && World.Has<Genesis.Shared.ECS.Components.Transform3DComponent>(Entity))
                {
                    // Plain x/y/z assignments are teleports too. Keep the physics pose in
                    // step with PGSL so its next update cannot restore the previous position.
                    ref var pose = ref World.GetRef<Genesis.Shared.ECS.Components.Transform3DComponent>(Entity);
                    pose.Position = new(t.X, t.Y, t.Z); pose.PoseHistoryValid = 0;
                    if (Game is Genesis.Runtime.Project.ProjectGameContext project)
                        project.Scene.Physics?.SynchronizeEntityTransform(World, Entity, System.Numerics.Vector3.One);
                }
            }

            if (HasComponent<PhysicsComponent>())
            {
                ref var p = ref GetComponent<PhysicsComponent>();
                p.HSpeed = (float)_ctx.HSpeed;
                p.VSpeed = (float)_ctx.VSpeed;
                p.Gravity = (float)_ctx.Gravity;
                p.GravityDirection = (float)_ctx.GravityDirection;
                p.Friction = (float)_ctx.Friction;
                p.Solid = _ctx.Solid;
            }

            if (HasComponent<SpriteComponent>())
            {
                ref var s = ref GetComponent<SpriteComponent>();
                bool spriteChanged = false;
                if (int.TryParse(_ctx.SpriteIndex, out var idx))
                {
                    s.SpriteIndex = idx;
                }
                else if (!string.IsNullOrWhiteSpace(_ctx.SpriteIndex))
                {
                    if (ObjectDrawAssetRegistry.TryGet(Entity, out ObjectDrawAssetEntry assets)
                        && !string.Equals(assets.Image, _ctx.SpriteIndex, StringComparison.OrdinalIgnoreCase))
                    {
                        assets.Image = _ctx.SpriteIndex.Trim();
                        s.ImageIndex = 0;
                        s.PlaybackElapsedMs = 0f;
                        s.PlaybackStepDirection = 1;
                        s.AnimationTagIndex = -1;
                        spriteChanged = true;
                    }

                    if (TryResolveSpriteAsset(_ctx.SpriteIndex, out SpriteRuntimeAsset asset))
                    {
                        s.AnimationTagIndex = FindAnimationTag(asset, _ctx.SpriteAnimationTag);
                        s.AnimationLoopOverride = _ctx.SpriteAnimationActive
                            ? (_ctx.SpriteAnimationLoop ? 1 : 0)
                            : -1;
                    }
                }
                if (!spriteChanged) s.ImageIndex = (int)_ctx.ImageIndex;
                s.ImageSpeed = (float)_ctx.ImageSpeed;
                s.Alpha = (float)_ctx.ImageAlpha;
                s.Depth = _ctx.Depth;
            }

            if (_ctx.SpriteTransitionTouched)
            {
                ObjectDrawAssetEntry assets = ObjectDrawAssetRegistry.TryGet(Entity, out ObjectDrawAssetEntry existing)
                    ? existing
                    : new ObjectDrawAssetEntry();
                assets.SpriteTransitionPreviousImage = _ctx.SpriteTransitionPreviousImage?.Trim() ?? string.Empty;
                assets.SpriteTransitionPreviousFrame = (int)Math.Max(0, _ctx.SpriteTransitionPreviousFrame);
                assets.SpriteTransitionDuration = (float)Math.Max(0, _ctx.SpriteTransitionDuration);
                assets.SpriteTransitionElapsed = (float)Math.Max(0, _ctx.SpriteTransitionElapsed);
                ObjectDrawAssetRegistry.Set(Entity, assets);
            }

            if (_ctx.ModelBindingTouched || _ctx.ModelTransformPolicyTouched
                || HasComponent<ModelRendererComponent>())
            {
                if (!HasComponent<ModelRendererComponent>())
                {
                    SetComponent(new ModelRendererComponent
                    {
                        ScaleX = 1f,
                        ScaleY = 1f,
                        ScaleZ = 1f,
                        CastShadows = true,
                        ReceiveShadows = true,
                        KeepPreviousTransform = _ctx.ModelKeepPreviousTransform,
                    });
                }

                ref var model = ref GetComponent<ModelRendererComponent>();
                model.ModelAsset = _ctx.ModelAsset?.Trim() ?? string.Empty;
                model.KeepPreviousTransform = _ctx.ModelKeepPreviousTransform;
                if (model.ScaleX == 0f && model.ScaleY == 0f && model.ScaleZ == 0f)
                    model.ScaleX = model.ScaleY = model.ScaleZ = 1f;

                ObjectDrawAssetEntry assets = ObjectDrawAssetRegistry.TryGet(Entity, out ObjectDrawAssetEntry existing)
                    ? existing
                    : new ObjectDrawAssetEntry();
                assets.Model = model.ModelAsset;
                ObjectDrawAssetRegistry.Set(Entity, assets);

                if (!string.IsNullOrWhiteSpace(model.ModelAsset) && !HasComponent<Draw3DComponent>())
                {
                    SetComponent(new Draw3DComponent
                    {
                        Visible = true,
                        CastShadows = model.CastShadows,
                        ReceiveShadows = model.ReceiveShadows,
                    });
                }
            }

            if (_ctx.LightEmitterTouched || HasComponent<PointLightComponent>())
            {
                if (!HasComponent<PointLightComponent>())
                    SetComponent(new PointLightComponent());

                ref var light = ref GetComponent<PointLightComponent>();
                light.Enabled = _ctx.LightEmitterEnabled;
                light.Color = System.Numerics.Vector3.Clamp(
                    _ctx.LightEmitterColor, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
                light.SecondaryColor = System.Numerics.Vector3.Clamp(
                    _ctx.LightEmitterSecondaryColor, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
                light.TertiaryColor = System.Numerics.Vector3.Clamp(
                    _ctx.LightEmitterTertiaryColor, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
                light.Offset = _ctx.LightEmitterOffset;
                light.Radius = (float)Math.Max(0.01, _ctx.LightEmitterRadius);
                light.Intensity = (float)Math.Max(0, _ctx.LightEmitterIntensity);
                light.Falloff = (float)Math.Clamp(_ctx.LightEmitterFalloff, 0.05, 16.0);
                light.Action = Enum.TryParse(
                    _ctx.LightEmitterAction,
                    ignoreCase: true,
                    out LightEmitterAction action)
                        ? action
                        : LightEmitterAction.Steady;
                light.ActionSpeed = (float)Math.Max(0, _ctx.LightEmitterActionSpeed);
                light.ActionAmount = (float)Math.Clamp(_ctx.LightEmitterActionAmount, 0, 1);
                light.Phase = (float)_ctx.LightEmitterPhase;
                light.ColorCount = Math.Clamp(_ctx.LightEmitterColorCount, 1, 3);
            }

            if (_ctx.ModelAnimationTouched || HasComponent<ModelAnimatorComponent>())
            {
                if (!HasComponent<ModelAnimatorComponent>())
                {
                    SetComponent(new ModelAnimatorComponent
                    {
                        ClipFps = 60f,
                        PlaybackSpeed = 1f,
                        Playing = true,
                        Loop = true,
                    });
                }

                ref var animator = ref GetComponent<ModelAnimatorComponent>();
                animator.ClipName = _ctx.ModelAnimationClip ?? string.Empty;
                animator.Controller = _ctx.AnimationController;
                animator.PreviousClipName = _ctx.ModelAnimationPreviousClip ?? string.Empty;
                animator.TimeSeconds = (float)Math.Max(0, _ctx.ModelAnimationTime);
                animator.PreviousTimeSeconds = (float)Math.Max(0, _ctx.ModelAnimationPreviousTime);
                animator.ClipFps = (float)(_ctx.ModelAnimationFps <= 0 ? 60 : _ctx.ModelAnimationFps);
                animator.PlaybackSpeed = (float)_ctx.ModelAnimationSpeed;
                animator.Loop = _ctx.ModelAnimationLoop;
                animator.Playing = _ctx.ModelAnimationActive;
                animator.BlendDuration = (float)Math.Max(0, _ctx.ModelAnimationBlendDuration);
                animator.BlendElapsed = (float)Math.Max(0, _ctx.ModelAnimationBlendElapsed);
            }

            if (HasComponent<Draw2DComponent>())
            {
                ref var d = ref GetComponent<Draw2DComponent>();
                d.Visible = _ctx.Visible;
                d.Depth = _ctx.Depth;
            }
            if (HasComponent<Draw3DComponent>()) GetComponent<Draw3DComponent>().Visible = _ctx.Visible;
        }

        private static int FindAnimationTag(SpriteRuntimeAsset asset, string tagName)
        {
            if (asset == null || string.IsNullOrWhiteSpace(tagName)) return -1;
            for (int index = 0; index < asset.Tags.Count; index++)
                if (string.Equals(asset.Tags[index].Name, tagName, StringComparison.OrdinalIgnoreCase))
                    return index;
            return -1;
        }

        private static bool TryResolveSpriteAsset(string image, out SpriteRuntimeAsset asset)
        {
            asset = null;
            if (string.IsNullOrWhiteSpace(image)) return false;
            if (SpriteAssetLoader.TryGetCachedAsset(image, out asset)) return true;
            try
            {
                asset = SpriteAssetLoader.Load(PgslCommands.ProjectPath, image);
                return asset != null;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException or ArgumentException)
            {
                return false;
            }
        }
    }
}
