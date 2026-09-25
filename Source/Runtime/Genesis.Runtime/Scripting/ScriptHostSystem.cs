using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Genesis.Shared.Assets;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// Owns the compiled gameplay assembly and the live <see cref="EntityBehavior"/>
    /// instances. The runtime scene (or the editor sandbox) calls:
    ///
    ///   host.LoadAssembly(result.Assembly);       // after CSharpScriptCompiler
    ///   host.Attach(world, entity, "OrcBehavior"); // when spawning a prefab
    ///   host.Update(dt);                           // each frame
    ///   host.DispatchCollision(a, b);              // from the physics system
    ///   host.Detach(entity);                       // before destroying an entity
    ///
    /// This is the native-C# replacement for the per-event PGSL VM dispatch.
    /// </summary>
    public sealed class ScriptHostSystem
    {
        private readonly Dictionary<string, Type> _behaviorTypes =
            new(StringComparer.Ordinal);

        private readonly Dictionary<string, Type> _resourceBehaviorTypes = new(StringComparer.OrdinalIgnoreCase);

        // All live behaviour instances (an entity may bind more than one ScriptComponent).
        private readonly List<EntityBehavior> _instances = new();

        // Behaviours whose OnCreate is waiting for the loader to finish placing them.
        private readonly List<EntityBehavior> _deferredCreates = new();
        private bool _roomActive;
        private bool _gameActive;

        private const int MaxRecentDiagnostics = 32;
        private readonly List<ScriptDiagnostic> _recentDiagnostics = new();
        private readonly Dictionary<string, ScriptDiagnostic> _diagnosticsByFingerprint =
            new(StringComparer.Ordinal);

        // Event code for the object currently being spawned, if the loader read any.
        private IReadOnlyDictionary<string, string> _pendingEventSources;

        /// <summary>
        /// Supply the event code for the next <see cref="Attach"/> call, then clear it.
        /// </summary>
        /// <remarks>
        /// Scoped to a single spawn on purpose: the loader sets it immediately before attaching one
        /// object's ScriptComponent and clears it after, so one object's events can never leak onto
        /// the next. Use <see cref="EventSourceScope"/> rather than calling this by hand.
        /// </remarks>
        public IDisposable UseEventSources(IReadOnlyDictionary<string, string> events) =>
            new EventSourceScope(this, events);

        private sealed class EventSourceScope : IDisposable
        {
            private readonly ScriptHostSystem _host;
            private readonly IReadOnlyDictionary<string, string> _previous;

            public EventSourceScope(ScriptHostSystem host, IReadOnlyDictionary<string, string> events)
            {
                _host = host;
                _previous = host._pendingEventSources;
                _host._pendingEventSources = events;
            }

            public void Dispose() => _host._pendingEventSources = _previous;
        }

        // Reused each dispatch — avoids 3 List allocations per frame at scale.
        private readonly List<EntityBehavior> _dispatchSnapshot = new();
        private readonly ScriptRenderGate _renderGate = new();

        // The collectible ALC owning the currently-loaded script assembly, if any.
        // Tracked so a recompile can unload it (true hot-reload, no type bleed, no
        // GameScripts.dll file-lock — fixes B5). Null when the assembly was loaded
        // into the default context (legacy callers) and so cannot be unloaded.
        private ScriptAssemblyLoadContext _activeScriptAlc;

        // Engine services injected into every behaviour. Defaults to a no-op context so
        // behaviours never see a null Game even before a host wires the real one.
        private IGameContext _context = NullGameContext.Instance;

        /// <summary>Public Script resource names available to bind to Objects.</summary>
        public IEnumerable<string> AvailableBehaviors => _resourceBehaviorTypes.Count > 0 ? _resourceBehaviorTypes.Keys : _behaviorTypes.Keys;

        /// <summary>Retained recent failures, including their current repetition counts.</summary>
        public IReadOnlyList<ScriptDiagnostic> RecentDiagnostics => _recentDiagnostics;

        /// <summary>Every live scripted instance, for the debugger's instance tree.</summary>
        /// <remarks>
        /// Exposed read-only rather than copied: the debugger walks this on the game thread while
        /// building a snapshot, so there is no window in which the list can change underneath it.
        /// </remarks>
        public IReadOnlyList<EntityBehavior> Instances => _instances;

        /// <summary>Raised for a new failure and at throttled repetition milestones (2, 10, 100...).</summary>
        public event Action<ScriptDiagnostic> DiagnosticReported;

        public ScriptDiagnostic LastDiagnostic { get; private set; }

        /// <summary>
        /// Set the engine-services context handed to every behaviour. The host calls this once
        /// after it has created the scene/renderer; it is applied to existing instances too.
        /// </summary>
        public void SetContext(IGameContext context)
        {
            _context = context ?? NullGameContext.Instance;
            if (_context is Project.ProjectGameContext project) project.ScriptHost = this;
            foreach (EntityBehavior b in _instances)
                b.SetContext(_context);
        }

        /// <summary>Index every concrete EntityBehavior subclass in the compiled assembly.</summary>
        public void LoadAssembly(Assembly assembly)
            => LoadAssembly(assembly, scriptAlc: null);

        /// <summary>
        /// Index behaviour types from the compiled assembly and adopt its collectible
        /// load context. Passing a non-null <paramref name="scriptAlc"/> enables true
        /// hot-reload: on the next recompile the host unloads the previous context,
        /// releasing the old assembly's memory and types. When null (legacy callers),
        /// the assembly stays in its load context for the process lifetime.
        /// </summary>
        public void LoadAssembly(Assembly assembly, ScriptAssemblyLoadContext scriptAlc)
        {
            // Unload the previously-loaded script assembly if it was collectible.
            // This is the heart of hot-reload: old behaviour Types are released so
            // the next compile's types don't collide and the GameScripts.dll file
            // is no longer locked (B5).
            if (_activeScriptAlc != null && !ReferenceEquals(_activeScriptAlc, scriptAlc))
            {
                try { _activeScriptAlc.Unload(); } catch { /* best effort */ }
            }
            _activeScriptAlc = scriptAlc;

            _behaviorTypes.Clear();
            _resourceBehaviorTypes.Clear();
            if (assembly == null) return;

            foreach (Type t in assembly.GetTypes())
            {
                if (t.IsAbstract || !typeof(EntityBehavior).IsAssignableFrom(t))
                    continue;

                _behaviorTypes[t.FullName] = t;
                // Allow binding by short name too (the editor dropdown shows short names).
                _behaviorTypes[t.Name] = t;
            }
            foreach (ResourceBehaviorAttribute binding in assembly.GetCustomAttributes<ResourceBehaviorAttribute>())
            {
                Type type = assembly.GetType(binding.TypeName);
                if (type is null || type.IsAbstract || !typeof(EntityBehavior).IsAssignableFrom(type))
                    throw new InvalidOperationException($"Script resource '{binding.ResourceName}' has an invalid compiled behavior binding.");
                if (!_resourceBehaviorTypes.TryAdd(binding.ResourceName, type))
                    throw new InvalidOperationException($"Duplicate compiled Script resource name '{binding.ResourceName}'.");
            }
        }

        /// <summary>Prefix marking a serialized-field entry in a ScriptComponent's props.</summary>
        public const string FieldPrefix = "field:";

        /// <summary>Field types the inspector can edit and the runtime can apply.</summary>
        public static readonly Type[] EditableFieldTypes =
        {
            typeof(int), typeof(long), typeof(float), typeof(double), typeof(bool), typeof(string),
        };

        public static bool IsEditableField(Type t) => Array.IndexOf(EditableFieldTypes, t) >= 0;

        /// <summary>Instantiate and bind a behaviour to an entity, then fire OnCreate.</summary>
        public EntityBehavior Attach(EcsWorld world, Entity entity, string behaviorClass)
            => Attach(world, entity, behaviorClass, null);

        /// <summary>
        /// Instantiate and bind a behaviour, apply any <c>field:Name</c> values from
        /// <paramref name="props"/> to its public fields, then fire OnCreate.
        /// </summary>
        public EntityBehavior Attach(EcsWorld world, Entity entity, string behaviorClass, IReadOnlyDictionary<string, string> props)
        {
            if (string.IsNullOrEmpty(behaviorClass))
                return null;

            EntityBehavior behavior;
            if (_resourceBehaviorTypes.TryGetValue(behaviorClass, out Type type) || _behaviorTypes.TryGetValue(behaviorClass, out type))
            {
                behavior = (EntityBehavior)Activator.CreateInstance(type);
                behavior.World = world;
                behavior.Entity = entity;
                behavior.SetContext(_context);
                if (props != null) ApplyFields(behavior, props);
            }
            else
            {
                PgslBehavior pgsl = new(behaviorClass);

                // An object's own event code, when the loader supplied it. Set before OnCreate so the
                // behaviour compiles from the object rather than reaching into the global script-name
                // registry — see PgslBehavior.SetEventSources and NEXT-044.
                if (_pendingEventSources is not null) pgsl.SetEventSources(_pendingEventSources);

                // Room instance overrides are serialized on the ScriptComponent as field:Name.
                // The VM behaviour specializes only matching exposed PGSL declarations in memory,
                // so Create observes the instance value without changing the object event source.
                pgsl.SetExposedFieldOverrides(props);

                behavior = pgsl;
                behavior.World = world;
                behavior.Entity = entity;
                behavior.SetContext(_context);
            }

            _instances.Add(behavior);

            if (DeferCreateEvents)
                _deferredCreates.Add(behavior);
            else
                FireCreate(behavior);

            return behavior;
        }

        /// <summary>
        /// While set, <see cref="Attach"/> queues OnCreate instead of firing it inline. Room loading
        /// turns this on because it applies each instance's placement transform *after* spawning its
        /// components: firing Create inline meant the script saw x=0,y=0 instead of where the
        /// designer put it, and anything Create assigned to x/y was then overwritten by the room
        /// (NEXT-047). Deferring to the end of the load also lets Create events see their siblings.
        /// </summary>
        public bool DeferCreateEvents { get; set; }

        /// <summary>Fire every OnCreate queued while <see cref="DeferCreateEvents"/> was set.</summary>
        public void FlushDeferredCreates()
        {
            if (_deferredCreates.Count == 0) return;

            // Snapshot: a Create event may spawn more instances, which must not mutate this loop.
            var pending = new List<EntityBehavior>(_deferredCreates);
            _deferredCreates.Clear();
            foreach (EntityBehavior behavior in pending)
                FireCreate(behavior);
        }

        /// <summary>
        /// Starts the initial or a replacement room after every instance has been created. GameStart
        /// fires only once for the process; RoomStart fires once for each entered room.
        /// </summary>
        public void BeginRoom(bool beginGame = false)
        {
            bool fireGameStart = beginGame || !_gameActive;
            if (fireGameStart)
            {
                _gameActive = true;
                DispatchLifecycle(static behavior => behavior.OnGameStart(), "OnGameStart");
            }

            _roomActive = true;
            DispatchLifecycle(static behavior => behavior.OnRoomStart(), "OnRoomStart");
        }

        /// <summary>Ends the current room and optionally the game, before Destroy handlers run.</summary>
        public void EndRoom(bool endGame = false)
        {
            if (_roomActive)
            {
                DispatchLifecycle(static behavior => behavior.OnRoomEnd(), "OnRoomEnd");
                _roomActive = false;
            }

            if (endGame && _gameActive)
            {
                DispatchLifecycle(static behavior => behavior.OnGameEnd(), "OnGameEnd");
                _gameActive = false;
            }
        }

        private void DispatchLifecycle(Action<EntityBehavior> dispatch, string hook)
        {
            if (_instances.Count == 0) return;
            FillDispatchSnapshot();
            foreach (EntityBehavior behavior in _dispatchSnapshot)
            {
                if (behavior.World == null || !behavior.World.IsAlive(behavior.Entity)) continue;
                try { dispatch(behavior); }
                catch (Exception ex) { LogBehaviorError(behavior, hook, ex); }
            }
        }

        private void FireCreate(EntityBehavior behavior)
        {
            try { behavior.OnCreate(); }
            catch (Exception ex) { LogBehaviorError(behavior, "OnCreate", ex); }
        }

        /// <summary>Set the behaviour's public instance fields from "field:Name" props entries.</summary>
        public static void ApplyFields(EntityBehavior behavior, IReadOnlyDictionary<string, string> props)
        {
            if (behavior == null || props == null) return;
            Type t = behavior.GetType();
            foreach (var kv in props)
            {
                if (kv.Key == null || !kv.Key.StartsWith(FieldPrefix, StringComparison.Ordinal)) continue;
                string fieldName = kv.Key.Substring(FieldPrefix.Length);
                FieldInfo fi = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
                if (fi == null) continue;
                try
                {
                    object converted = ConvertTo(fi.FieldType, kv.Value);
                    if (converted != null) fi.SetValue(behavior, converted);
                }
                catch { /* skip a field that won't convert */ }
            }
        }

        private static object ConvertTo(Type t, string s)
        {
            if (t == typeof(string)) return s ?? "";
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (t == typeof(int))    return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (object)null;
            if (t == typeof(long))   return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : (object)null;
            if (t == typeof(float))  return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : (object)null;
            if (t == typeof(double)) return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (object)null;
            if (t == typeof(bool))   return bool.TryParse(s, out bool b) ? b : (object)null;
            return null;
        }

        public void Update(float dt)
        {
            if (_instances.Count == 0) return;
            FillDispatchSnapshot();

            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnStepBegin(dt); }
                catch (Exception ex) { LogBehaviorError(b, "OnStepBegin", ex); }
            }

            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity))
                    continue;
                try { b.OnUpdate(dt); }
                catch (Exception ex) { LogBehaviorError(b, "OnUpdate", ex); }
            }

            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnInputEvents(); }
                catch (Exception ex) { LogBehaviorError(b, "OnInputEvents", ex); }
            }

            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnStepEnd(dt); }
                catch (Exception ex) { LogBehaviorError(b, "OnStepEnd", ex); }
            }
        }

        /// <summary>Let every behaviour draw its 2D HUD/overlay for this frame.</summary>
        public void DispatchDrawHud(IHudCanvas hud)
        {
            if (_instances.Count == 0 || hud == null) return;
            FillDispatchSnapshot();
            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnDrawHud(hud); }
                catch (Exception ex) { LogBehaviorError(b, "OnDrawHud", ex); }
            }
        }

        /// <summary>
        /// Runs every PGSL object's <c>Draw</c> event with a live draw surface, so script-issued
        /// 2D drawing (shapes via the sprite batch, text via the D2D HUD canvas) actually reaches
        /// the frame. Before this existed nothing implemented <c>IPgslDrawSurface</c>, so all PGSL
        /// drawing silently no-opped — see <see cref="PgslRenderDrawSurface"/> and NEXT-033.
        /// </summary>
        public void DispatchPgslWorldDraw(IRenderController renderer, IRenderCommandSink commands, IPgslDrawSurface surfaceOverride = null)
        {
            if (_instances.Count == 0 || renderer == null) return;

            bool is3D = _context.Room?.Dimension == RoomDimension.ThreeD;
            IPgslDrawSurface surface = surfaceOverride;
            FillDispatchSnapshot();
            foreach (var b in _dispatchSnapshot)
            {
                if (b is not PgslBehavior pgsl || !pgsl.HasWorldDrawScript) continue;
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                surface ??= new PgslRenderDrawSurface(
                    renderer,
                    hud: null,
                    _context.RenderWidth > 0 ? _context.RenderWidth : 1280,
                    _context.RenderHeight > 0 ? _context.RenderHeight : 720,
                    commands,
                    _context.ResolveAssetPath("."),
                    is3DActive: is3D);
                if (_context.Camera is not null)
                {
                    pgsl.Context.View3D = _context.Camera.ViewMatrix;
                    pgsl.Context.Proj3D = _context.Camera.ProjectionMatrix;
                }
                try { pgsl.OnDrawWorldFrame(surface); }
                catch (Exception ex) { LogBehaviorError(b, "PGSL Draw", ex); }
            }
        }

        public void DispatchPgslGuiDraw(IRenderController renderer, IHudCanvas hud, IPgslDrawSurface surfaceOverride = null)
        {
            // Either sink is enough. HUD text goes to the D2D canvas, not the renderer (whose
            // DrawText is a stub — NEXT-033), so requiring a renderer here dropped every
            // HUD-only Draw event whenever one was absent.
            if (_instances.Count == 0 || (renderer == null && hud == null)) return;

            IPgslDrawSurface surface = surfaceOverride;
            FillDispatchSnapshot();
            foreach (var b in _dispatchSnapshot)
            {
                if (b is not PgslBehavior pgsl || !pgsl.HasGuiDrawScript) continue;
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                surface ??= new PgslRenderDrawSurface(
                    renderer,
                    hud,
                    hud?.Width > 0 ? hud.Width : 1280,
                    hud?.Height > 0 ? hud.Height : 720,
                    isGui: true);
                try { pgsl.OnDrawGuiFrame(surface); }
                catch (Exception ex) { LogBehaviorError(b, "PGSL DrawGui", ex); }
            }
        }

        /// <summary>Compatibility alias for callers that historically meant the overlay pass.</summary>
        public void DispatchPgslDraw(IRenderController renderer, IHudCanvas hud) =>
            DispatchPgslGuiDraw(renderer, hud);

        /// <summary>Textured HUD sprites drawn after the D2D layer (icons, software cursor).</summary>
        public void DispatchDrawHudOverlay(IRenderController renderer)
        {
            if (_instances.Count == 0 || renderer == null) return;
            FillDispatchSnapshot();
            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnDrawHudOverlay(renderer); }
                catch (Exception ex) { LogBehaviorError(b, "OnDrawHudOverlay", ex); }
            }
        }

        /// <summary>
        /// Let behaviours submit engine render hooks (lights, fog, chunk bounds).
        /// Direct mesh/sprite drawing is blocked — use Draw2D/Draw3D components instead.
        /// </summary>
        public void DispatchRenderFrame(IRenderController renderer, IRenderCommandSink commands = null)
        {
            if (_instances.Count == 0 || renderer == null) return;
            _renderGate.Bind(renderer, commands);
            FillDispatchSnapshot();
            foreach (var b in _dispatchSnapshot)
            {
                if (b.World == null || !b.World.IsAlive(b.Entity)) continue;
                try { b.OnRenderFrame(_renderGate); }
                catch (Exception ex) { LogBehaviorError(b, "OnRenderFrame", ex); }
            }
        }

        public void DispatchCollision(Entity a, Entity b)
        {
            foreach (EntityBehavior behavior in _instances)
            {
                if (behavior.Entity.Id == a.Id)
                    TryCollision(behavior, b);
                else if (behavior.Entity.Id == b.Id)
                    TryCollision(behavior, a);
            }
        }

        public void Detach(Entity entity)
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                EntityBehavior b = _instances[i];
                if (b.Entity.Id != entity.Id) continue;
                try { b.OnDestroy(); }
                catch (Exception ex) { LogBehaviorError(b, "OnDestroy", ex); }
                _instances.RemoveAt(i);
            }
        }

        public void Clear(Func<Entity, bool> keepEntity = null)
        {
            // Room swaps call EndRoom explicitly so the outgoing handlers run before unloading.
            // Defensive fallback keeps direct callers from silently skipping RoomEnd.
            if (_roomActive) EndRoom(endGame: false);
            if (keepEntity == null) DestroyAllBehaviors();
            else
            {
                foreach (var behavior in _instances.ToArray())
                    if (!keepEntity(behavior.Entity)) Detach(behavior.Entity);
                _deferredCreates.RemoveAll(behavior => !keepEntity(behavior.Entity));
            }
        }

        /// <summary>Fire RoomEnd/GameEnd, then Destroy, exactly once during player shutdown.</summary>
        public void Shutdown()
        {
            EndRoom(endGame: true);
            DestroyAllBehaviors();
        }

        private void DestroyAllBehaviors()
        {
            foreach (EntityBehavior b in _instances)
            {
                try { b.OnDestroy(); } catch { /* swallow on teardown */ }
            }
            _instances.Clear();
            _deferredCreates.Clear();
        }

        private void TryCollision(EntityBehavior b, Entity other)
        {
            if (b.World == null || !b.World.IsAlive(b.Entity)) return;
            try { b.OnCollision(other); }
            catch (Exception ex) { LogBehaviorError(b, "OnCollision", ex); }
        }

        public IReadOnlyList<EntityBehavior> Behaviors => _instances;

        public EntityBehavior FindBehaviorForEntity(Entity entity)
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                if (_instances[i].Entity.Id == entity.Id)
                    return _instances[i];
            }
            return null;
        }

        /// <summary>Most recent behaviour exception message (for the editor sandbox to display).</summary>
        public string LastError { get; private set; }

        public void ClearDiagnostics()
        {
            _recentDiagnostics.Clear();
            _diagnosticsByFingerprint.Clear();
            LastDiagnostic = null;
            LastError = null;
        }

        private void LogBehaviorError(EntityBehavior b, string hook, Exception ex)
        {
            PgslExecutionException pgslException = FindPgslException(ex);
            string behaviorName = b?.GetType().FullName ?? "UnknownBehavior";
            string objectName = pgslException?.ScriptName
                ?? (b is PgslBehavior pgsl ? pgsl.ScriptName : b?.GetType().Name)
                ?? "UnknownObject";
            string eventName = pgslException?.EventName ?? hook ?? "Unknown";
            int line = pgslException?.SourceLine ?? PgslExecutionException.FindLine(ex);
            string message = pgslException?.ScriptMessage ?? ex?.GetBaseException().Message ?? "Unknown script failure.";
            string source = pgslException?.SourceLabel ?? behaviorName;
            int entityId = b?.Entity.Id ?? 0;
            string fingerprint = $"{objectName}\u001f{entityId}\u001f{eventName}\u001f{line}\u001f{message}";

            if (_diagnosticsByFingerprint.TryGetValue(fingerprint, out ScriptDiagnostic existing))
            {
                existing.RepeatCount++;
                existing.LastOccurrenceUtc = DateTime.UtcNow;
                LastDiagnostic = existing;
                LastError = existing.ToDisplayString();
                if (ShouldPublishRepeat(existing.RepeatCount)) Publish(existing);
                return;
            }

            DateTime now = DateTime.UtcNow;
            var diagnostic = new ScriptDiagnostic
            {
                TimestampUtc = now,
                LastOccurrenceUtc = now,
                ObjectName = objectName,
                BehaviorName = behaviorName,
                EntityId = entityId,
                EventName = eventName,
                Hook = hook,
                Source = source,
                Line = line,
                Message = message,
                ExceptionType = ex?.GetType().FullName ?? "System.Exception",
                StackTrace = ex?.ToString() ?? message,
                Fingerprint = fingerprint,
            };

            _recentDiagnostics.Add(diagnostic);
            _diagnosticsByFingerprint[fingerprint] = diagnostic;
            if (_recentDiagnostics.Count > MaxRecentDiagnostics)
            {
                ScriptDiagnostic removed = _recentDiagnostics[0];
                _recentDiagnostics.RemoveAt(0);
                if (removed?.Fingerprint != null) _diagnosticsByFingerprint.Remove(removed.Fingerprint);
            }

            LastDiagnostic = diagnostic;
            LastError = diagnostic.ToDisplayString();
            Publish(diagnostic);
        }

        private void Publish(ScriptDiagnostic diagnostic)
        {
            Console.Error.WriteLine(diagnostic.ToLogLine());
            Delegate[] subscribers = DiagnosticReported?.GetInvocationList();
            if (subscribers == null) return;
            foreach (Delegate subscriber in subscribers)
            {
                try { ((Action<ScriptDiagnostic>)subscriber)(diagnostic); }
                catch (Exception callbackError)
                {
                    Console.Error.WriteLine("[Script diagnostic subscriber] " + callbackError.Message);
                }
            }
        }

        private static bool ShouldPublishRepeat(int repeatCount) =>
            repeatCount <= 2 || repeatCount == 10 || repeatCount == 100 || repeatCount % 1000 == 0;

        private static PgslExecutionException FindPgslException(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
                if (current is PgslExecutionException pgsl) return pgsl;
            return null;
        }

        private void FillDispatchSnapshot()
        {
            _dispatchSnapshot.Clear();
            _dispatchSnapshot.AddRange(_instances);
        }
    }
}
