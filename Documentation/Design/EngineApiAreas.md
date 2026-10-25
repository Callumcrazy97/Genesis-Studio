# Engine API: every area, its parts, and what each can expose

24 October 2026. The plan for giving every area of the engine API the same shape as
`Engine.Lighting` now has: **Engine.Area.Part.Value**, with **Enabled** on every area and part.

- **✓** exists today (sometimes under an older flat name, given in brackets; old names keep working).
- **+** can be exposed: the engine already holds the value, or it is a small addition.
- Every property can be read and set like a variable, works in `with` and `for`, is a room/Object
  Engine setting in the Inspector, and is shown on the F6 debug screen.
- **The Enabled rule (in place now, for every area):** turning a part on turns its area on
  (`Engine.Lighting.Bounce.Enabled = true` also sets `Engine.Lighting.Enabled`), all the way up.
  Turning an area off switches everything in it off; its parts come back as they were.
- Values marked "instance" belong to the current instance (`with`, `for`); the rest belong to the world.
- The complete list of today's names, with descriptions, is `Documentation/EngineApi.md`.

**Built so far** (24 October, afternoon; each value's default is in its description and in EngineApi.md):
Engine.Display.Window (X, Y, Resizable, AlwaysOnTop, Maximized, Minimized, Focus(), Centre()),
Engine.Display.Monitor (Index, Count, Width, Height, RefreshRate), Engine.Display.Cursor.Enabled;
Engine.Camera (Fov, Near, Far), Camera.FreeFly.Enabled, Camera.Orbit (Enabled, Distance, Yaw, Pitch,
MouseLook, Sensitivity), Camera.Shoulder (X, Y, Z), Camera.Collision (Enabled, Clearance,
RecoverySpeed), Camera.Shake (Start(), Roll()); Engine.Rendering.Shadows (Enabled, Strength, Distance,
Bias, Cascades, Resolution, Contact, LampBudget), Rendering.Fog (Enabled, Start, End, Density,
HeightBase, HeightFalloff, Noise, Color, Shafts), Rendering.Ao.Enabled, Rendering.Bloom.Enabled,
Rendering.Exposure (Value, Auto), Rendering.Grading (Contrast, Saturation, Vignette),
Rendering.AntiAliasing.Mode, Rendering.MotionBlur.Strength, Rendering.Culling (Frustum, Occlusion),
Rendering.Detail (Enabled, Quality, CullSize, AnimationBySize); Engine.Lighting.Sun (Intensity, Yaw,
Pitch, Color), Lighting.Ambient (Sky, Ground, Mode, Strength), Lighting.Toon (Enabled, Steps, Wrap,
Specular, Rim, Saturation), Lighting.Glow.Strength, plus Bounce, Reflections and Probe; Engine.Sky.Time
(OfDay, DayOfYear, Latitude), Sky.Clouds (Enabled, Coverage, Density, Altitude, Thickness, Quality),
Sky.Sun (Visible, Size), Sky.Moon.Visible, Sky.Stars.Enabled; Engine.Audio.Enabled, Audio.Music
(Enabled, Volume, Track, Muffle), Audio.Effects (Enabled, Volume), Audio.Acoustics (Enabled, Place,
Amount); Engine.Physics.World (AirDrag, MaxVelocity, RecoveryVelocity), Physics.Gravity (Strength, X,
Y, Z), Physics.Sleep (Enabled, Threshold); Engine.Input.Mouse.Captured, Input.Gamepad.DeadZone;
Engine.Game.Paused, Engine.Game.Threads, Engine.Game.Name, Engine.Game.Version, Game.Scripts
(InstructionLimit, Profile); Engine.Camera.Focus (Enabled, Auto, Distance, Range, Blur); Engine.Sky.Time
(Speed, Paused); Engine.Weather (Preset, Transition, Auto, Release()), Weather.Wind (Enabled, Strength,
Direction), Weather.Rain (Enabled, Amount, Wetness), Weather.Snow (Enabled, Amount, Cover),
Weather.Lightning (Enabled, Amount), Weather.Temperature.Celsius (each follows the weather until a
script sets it, then holds); Engine.Audio.Listener.FollowCamera; Engine.Room.Change (Fade, MinimumTime,
ProgressBar, Text); Engine.Debug.Keys.Enabled; Engine.Particle.Bursts.Limit; Engine.Mesh.Upload.Budget;
Engine.Water.Reflections.Enabled; Engine.Tile.Autotile.Enabled; Engine.Model.Shadows (Cast, Receive),
Model.Glow (Strength, EmissionScale); Engine.Input.Gamepad.KeyboardEmulation; Engine.View.Camera.Zoom;
Engine.Navigation.Agent (Speed, StoppingDistance); 25 October: Engine.Display.Resolution (Scale, Sharpness, Current, Dynamic.Enabled, Dynamic.TargetFps, Dynamic.MinimumScale), Engine.Room.Streaming (Remember, Forget(), Remembered, Save(), Load(text)), Engine.Rendering (Backend, DrawCalls, Triangles, GpuMs, Instances.Drawn, Instances.Culled; read), Engine.Instance.Update.Distance, Engine.Tile.Layer (Visible, SetVisible, Alpha, SetAlpha, OffsetX, OffsetY, SetOffset by name), Engine.Physics.Joint limits (PhysicsJointHinge, PhysicsJointLimitSwing, PhysicsJointLimitTwist). Colours are "#rrggbb" text.

---

## Engine.Game

| Part | Values |
|---|---|
| Engine.Game | + Enabled (paused when false), ✓ Speed, ✓ Threads, ✓ TimeMs (read), + Time (read: seconds since start), + FrameCount (read), + DeltaTime (read), + Paused, ✓ Quit(), ✓ End(), ✓ Print(...), ✓ EventUser(n) |
| Engine.Game.Scripts | + Enabled, ✓ InstructionLimit [ScriptInstructionLimit], + Compiled (read: functions running compiled), ✓ Profile.Enabled [ScriptProfileEnable], ✓ Profile.Report(), ✓ Profile.Reset() |
| Engine.Game.Jobs | + Enabled, ✓ Threads (= Engine.Game.Threads), + Running (read), + Queued (read) |
| Engine.Game.Time | + Scale (= Speed), + FixedStep (physics step seconds), + MaxDeltaTime (largest step a slow frame may take) |

## Engine.Display (the window)

| Part | Values |
|---|---|
| Engine.Display | + Enabled (window shown), ✓ Width, ✓ Height, ✓ Mode ("windowed", "borderless", "fullscreen"), ✓ Fullscreen, ✓ VSync, ✓ MaxFps, ✓ Fps (read) [GetFps], ✓ Title, ✓ HasFocus (read), ✓ IsMinimized (read), + IsMaximized (read), ✓ SetSize(w, h) |
| Engine.Display.Window | + X, + Y (position on the desktop), + Resizable, + Borderless, + AlwaysOnTop, + MinWidth, + MinHeight, + AspectLock, + Opacity, + Icon ("Image"), + Minimize(), + Maximize(), + Restore(), + Focus(), + Flash() (taskbar attention), + Centre() |
| Engine.Display.Monitor | + Index (which monitor), + Count (read), + Width (read), + Height (read), + RefreshRate (read), + DpiScale (read) |
| Engine.Display.Cursor | + Enabled (= ✓ CursorVisible), + Image ("Image"), + HotspotX, + HotspotY, + Locked (= Engine.Input.Mouse.Captured), + Confined (kept inside the window) |
| Engine.Display.Gui | ✓ Width (read) [GetGuiWidth], ✓ Height (read), ✓ Scale (read) [GetGuiScale], ✓ X, ✓ Y (read), + ScaleMode ("fit", "stretch", "pixel-perfect"), + DesignWidth, + DesignHeight |
| Engine.Display.Resolution | ✓ Scale (render scale 0.25-2, stretched and sharpened), ✓ Sharpness, ✓ Current (read), ✓ Dynamic.Enabled (lower the scale to hold the frame rate), ✓ Dynamic.TargetFps, ✓ Dynamic.MinimumScale |
| Engine.Display.Frame | ✓ UseRoomFps, ✓ FpsTarget, ✓ FpsVsync, + Limiter.Enabled, + Limiter.Fps |

## Engine.Camera (3D)

| Part | Values |
|---|---|
| Engine.Camera | + Enabled, ✓ X, ✓ Y, ✓ Z, ✓ Yaw, ✓ Pitch, ✓ Roll, + Fov [SetFov], + Near [SetNearPlane], + Far [SetFarPlane], + Projection ("perspective", "orthographic"), + OrthoSize, ✓ SetPosition(x, y, z), ✓ SetTarget(x, y, z), ✓ MoveForward/MoveRight/MoveUp(d), + ForwardX/Y/Z (read) [GetForwardX], + RightX/Y/Z (read) |
| Engine.Camera.Target | + Enabled (look at a point or instance), + X, + Y, + Z, + Instance (id), + Smoothing |
| Engine.Camera.Follow | + Enabled, + Instance (id), + OffsetX, + OffsetY, + OffsetZ, + Lag (seconds), + Smoothing, + KeepUpright |
| Engine.Camera.Orbit | ✓ Enabled [OrbitEnabled], ✓ Distance, ✓ Yaw, ✓ Pitch, ✓ MouseLook, + MinDistance, + MaxDistance, + MinPitch, + MaxPitch, + ZoomSpeed, + TurnSpeed, + Smoothing |
| Engine.Camera.Shoulder | + Enabled, + OffsetX, + OffsetY [SetShoulderOffset], + Side (-1 left, 1 right), + SwitchSpeed |
| Engine.Camera.FreeFly | ✓ Enabled [FreeFly], + Speed, + Boost, + MouseSensitivity |
| Engine.Camera.Collision | ✓ Enabled [CollisionEnabled], + Radius, + Layers (mask), + PullInSpeed |
| Engine.Camera.Shake | + Enabled, + Strength, + Duration, + Frequency, + Roll, ✓ Shake(strength, seconds) [Shake3D], ✓ ShakeRoll(...) [ShakeRoll3D], + Stop() |
| Engine.Camera.Focus (depth of field) | + Enabled, + Distance, + Range, + Blur, + Auto (focus on what is at the centre) |
| Engine.Camera.Layers | ✓ ModelLayer.Fov/Near/Visible [ModelLayerSetFov...], ✓ ViewLayer.Fov/Near [ViewLayerSetFov] |

## Engine.View (views and split screens, in 2D and 3D rooms)

| Part | Values |
|---|---|
| Engine.View | + Enabled (views on), ✓ Active (index), ✓ Count (read) [GetCount], ✓ Find(name), ✓ Reset() |
| Engine.View.Camera (the active view's camera; ViewSet(i) chooses another) | ✓ Enabled [SetEnabled], ✓ X, ✓ Y, ✓ Z [SetPosX...], ✓ Zoom, ✓ Yaw, ✓ Pitch, ✓ Fov, ✓ Near, ✓ Far [SetFrustumNear/Far], + Angle (2D rotation) |
| Engine.View.Viewport | ✓ X, ✓ Y, ✓ Width, ✓ Height [SetViewportX...] |
| Engine.View.Bounds | + Enabled, ✓ Width, ✓ Height [SetBoundsW/H], + X, + Y |
| Engine.View.Follow | ✓ Enabled/Instance [SetFollow], ✓ BorderH, ✓ BorderV, ✓ SpeedH, ✓ SpeedV, + Smoothing, + LookAhead |
| Engine.View.Shake | + Enabled, + Strength, + Duration, ✓ Shake(...) |

## Engine.Lighting (done today, the model for the rest)

| Part | Values |
|---|---|
| Engine.Lighting | ✓ Enabled, + Sun.Enabled, + Ambient.Enabled |
| Engine.Lighting.Sun | + Enabled, + Yaw, + Pitch, + Intensity, + ColorR/G/B (or Color "#rrggbb"), + DirectionX/Y/Z, + Shadows (= Engine.Rendering.Shadows.Enabled for the sun) |
| Engine.Lighting.Ambient | + Enabled, + SkyR/G/B, + GroundR/G/B, + Strength, + Mode ("zenith", "hemisphere") [Engine.Sky.AmbientMode], + Saturation [Engine.Sky.AmbientSaturation], + Tint |
| Engine.Lighting.Bounce | ✓ Enabled, ✓ Strength, + Saturation, + Tint |
| Engine.Lighting.Reflections | ✓ Enabled, ✓ Strength, + Roughness (a floor for every surface), + Sky.Enabled (mirror the sky only, no probe) |
| Engine.Lighting.Probe | ✓ Quality, ✓ Distance, ✓ Radius, ✓ Recapture(), + Captures (read), + X/Y/Z (read: where it was captured), + Follow (false: fixed where captured) |
| Engine.Lighting.Lamps (point and spot lights) | + Enabled, + Count (read), + Limit, + Shadows.Enabled, + Shadows.Budget [Engine.Rendering.OmniShadowBudget], + Shadows.Resolution |
| Engine.Lighting.Emitter (the current instance's own light, instance) | ✓ Enabled [Enable], ✓ Intensity, ✓ Radius, ✓ Falloff, ✓ ColorCount, ✓ SetColor/SetSecondaryColor/SetTertiaryColor, ✓ SetAction("Flicker"...), ✓ SetOffset(x, y, z), + Shadows, + Spot.Enabled, + Spot.Angle, + Spot.Softness |
| Engine.Lighting.Flash | + Enabled, ✓ Flash(x, y, z, ...), ✓ Move, ✓ Remove, ✓ Clear, ✓ Count, ✓ Exists, + Limit |
| Engine.Lighting.Light2D | + Enabled, ✓ Ambient (darkness) [Light2DAmbient], ✓ Point(...), ✓ Cone(...), ✓ Blocker(...), ✓ Remove, ✓ LineClear, ✓ ContactShadow [Shadow2DContact], + Shadows.Enabled, + Shadows.Softness |
| Engine.Lighting.Toon (stylized) | + Enabled, + Steps, + Wrap, + Specular, + Rim, + Saturation |
| Engine.Lighting.Glow (global emission) | + Enabled, + Strength |

## Engine.Sky

| Part | Values |
|---|---|
| Engine.Sky | + Enabled, ✓ Quality, ✓ AtmospherePreset, ✓ Haze, ✓ Visibility, + ZenithColor, + HorizonColor |
| Engine.Sky.Time | + Enabled (time of day runs), ✓ TimeOfDay, ✓ DayOfYear, ✓ Latitude, + Speed (in-game hours per real minute), + Paused |
| Engine.Sky.Sun | ✓ DiscVisible [SunDiscVisible], ✓ Size [SunSize], + Brightness |
| Engine.Sky.Moon | ✓ DiscVisible [MoonDiscVisible], + Phase, + Brightness |
| Engine.Sky.Stars | + Enabled [CelestialExtrasEnabled], + Brightness, + Rotation |
| Engine.Sky.Clouds | + Enabled [RaymarchedCloudsEnabled], ✓ Coverage, ✓ Density, ✓ Thickness, ✓ Altitude, + Speed, + DirectionX/Z, + Quality [Engine.Rendering.CloudQuality], + Temporal.Enabled [CloudTemporalEnabled] |
| Engine.Sky.Ambient | ✓ Mode, ✓ Strength, ✓ Saturation, ✓ Scale, ✓ Tint [SetAmbientTint] |
| Engine.Sky.Fog | ✓ Scale [FogScale] |
| Engine.Sky.Layer (a picture behind the world) | + Enabled, + Image, + Scroll, + Tint |

## Engine.Weather

| Part | Values |
|---|---|
| Engine.Weather | + Enabled, ✓ Preset ("clear", "rain", "thunderstorm", "snow", "fog"...), ✓ Transition (seconds), ✓ Auto, ✓ Release() |
| Engine.Weather.Wind | ✓ Enabled, ✓ Strength, ✓ Direction (degrees), + Gusts |
| Engine.Weather.Rain | ✓ Enabled, ✓ Amount, ✓ Wetness (surfaces), + Splashes.Enabled, + Sound.Enabled |
| Engine.Weather.Snow | ✓ Enabled, ✓ Amount, ✓ Cover (snow on surfaces) |
| Engine.Weather.Lightning | ✓ Enabled, ✓ Amount (how often), ✓ Strike(x, z) [StrikeLightning], ✓ Flash() [LightningFlash], ✓ Strikes (read) |
| Engine.Weather.Temperature | ✓ Celsius |

## Engine.Water

| Part | Values |
|---|---|
| Engine.Water | + Enabled, ✓ DepthAt(x, z), ✓ SurfaceAt(x, z), ✓ FlowX/FlowZ(x, z), ✓ IsSwimmable(x, z) |
| Engine.Water.Reflections | ✓ Enabled [Reflections], + Resolution, + Distortion |
| Engine.Water.Surface | ✓ Waves, ✓ WaveSpeed, + Foam.Enabled, ✓ FoamWidth, ✓ ShallowColor, ✓ DeepColor, ✓ Clarity |
| Engine.Water.Holes (terrain water) | ✓ Radius, ✓ Shape, ✓ AngleBias, ✓ DischargeCoefficient [Engine.Terrain.Water.Hole*], ✓ Punch(x, z), ✓ Clear() |
| Engine.Water.Swimming | + Buoyancy, + Drag |

## Engine.Terrain

| Part | Values |
|---|---|
| Engine.Terrain | + Enabled (drawn), ✓ HeightAt(x, z) [GetHeight], + NormalAt(x, z), + MaterialAt(x, z), + Width (read), + Depth (read) |
| Engine.Terrain.Detail | ✓ Quality (LOD), + Distance, + Tessellation |
| Engine.Terrain.Grass | + Enabled, + Density, + Distance, + Height, + Wind |
| Engine.Terrain.Scatter (trees, rocks) | + Enabled, + Density, + Distance, + Shadows |
| Engine.Terrain.Collision | + Enabled, + Distance (how far collision is built around bodies) |
| Engine.Terrain.Edit | ✓ Raise(x, z, radius, metres), ✓ Lower(...), ✓ Flatten(x, z, radius, height), ✓ Smooth(...), ✓ Paint(x, z, radius, layer, strength) |

## Engine.Rendering

| Part | Values |
|---|---|
| Engine.Rendering | + Enabled (3D drawing), ✓ QualityTier [SetQuality], ✓ ReversedDepth, + Backend (read: "DX11", "DX12", "Vulkan", "OpenGL"), + DrawCalls (read), + Triangles (read), + GpuMs (read) |
| Engine.Rendering.Shadows | + Enabled, ✓ Resolution [ShadowResolution], ✓ Cascades [CascadeCount], + Distance, + Strength, + Bias, + Softness, + ContactShadows.Enabled [ContactShadowsEnabled] |
| Engine.Rendering.Fog | + Enabled, + Start, + End, + Density, + ColorR/G/B, + HeightBase, + HeightFalloff, + Noise, + Volumetric.Enabled, ✓ Volumetric.Quality [VolumetricFogQuality], ✓ Shafts.Anisotropy/Floor/Softness [FogShaft*], ✓ Local.Enabled [LocalVolumetricsEnabled], ✓ Local.Budget [LocalVolumetricLightBudget], ✓ Smoke.Enabled [SmokeExtinctionEnabled] |
| Engine.Rendering.Ao | ✓ Enabled [GtaoEnabled], + Strength, + Radius |
| Engine.Rendering.Bloom | ✓ Enabled [BloomEnabled], + Strength, + Threshold |
| Engine.Rendering.Exposure | ✓ Value [Exposure], ✓ Auto.Enabled [AutoExposureEnabled], ✓ Auto.Metering, + Auto.Speed [SetAutoExposureSpeed], + Auto.Min, + Auto.Max, ✓ Auto.Reset() |
| Engine.Rendering.Grading | + Enabled, ✓ Contrast, ✓ Saturation, ✓ Vignette, + Temperature, + Tint, + Lut ("Image") |
| Engine.Rendering.AntiAliasing | + Enabled, ✓ Mode [AntiAliasing] ("off", "fxaa", "smaa", "taa"), + Sharpness |
| Engine.Rendering.MotionBlur | + Enabled, ✓ Strength [MotionBlur], + Samples |
| Engine.Rendering.Culling | + Frustum.Enabled, + Occlusion.Enabled, + Distance.Enabled, + Distance.Max, + SmallObjects.Enabled, + SmallObjects.Size |
| Engine.Rendering.Detail (level of detail) | + Enabled, + Quality (distance multiplier), + Fade.Enabled, + AnimationBySize.Enabled |
| Engine.Rendering.Atmosphere | ✓ Lut.Enabled [AtmosphereLutEnabled] |
| Engine.Rendering.Outlines | + Enabled, + Width, ✓ Set/Clear via Engine.Shader.Outline |
| Engine.Rendering.Wireframe | + Enabled (debug) |

## Engine.PostEffect

| Part | Values |
|---|---|
| Engine.PostEffect | + Enabled (all of them), ✓ Add("Effect"), ✓ AddFile(path), ✓ Remove("Effect"), ✓ Clear(), ✓ IsRunning("Effect"), ✓ LastError |
| Engine.PostEffect.Effect (one by name) | + Enabled("Effect"), ✓ SetParameter("Effect", name, value), ✓ SetTexture(...), + Order("Effect", n), + Strength("Effect") |

## Engine.Decal

| Part | Values |
|---|---|
| Engine.Decal | + Enabled, ✓ Limit, ✓ Count (read), ✓ Add(...), ✓ Remove, ✓ Clear, ✓ Exists, ✓ Move, ✓ Follow/Unfollow, ✓ SetSize, ✓ SetRotation, ✓ SetColor, ✓ SetBlend, ✓ SetFade, ✓ SetLife, ✓ SetSeeThrough, + Distance (drawn up to) |

## Engine.Particle

| Part | Values |
|---|---|
| Engine.Particle | + Enabled, ✓ Count (read), ✓ Capacity (read), ✓ Create/Destroy/DestroyAll, ✓ Burst(...), ✓ BurstAt(...), ✓ BurstBatched(...), ✓ Quality |
| Engine.Particle.Bursts | + Enabled, ✓ Limit [BurstLimit], ✓ Count (read) |
| Engine.Particle.Emitter (instance) | ✓ Enabled [AttachedSetEmitting], ✓ Rate, ✓ Size, ✓ Speed, ✓ Life, ✓ Color, ✓ Gravity |
| Engine.Particle.Particles2D | + Enabled, ✓ Burst [Particle2DBurst], ✓ Flow [Particle2DFlow], ✓ Count, ✓ Pause [Effects2DPause] |
| Engine.Particle.Gpu | + Enabled, + Collision.Enabled, + Lit.Enabled |

## Engine.Physics

| Part | Values |
|---|---|
| Engine.Physics | ✓ Enabled (instance body), ✓ Available (read), + World.Enabled (simulation runs), ✓ Use("Material"), ✓ Remove() |
| Engine.Physics.World | + Enabled, + Paused, ✓ SolverIterations, + Substeps, + FixedStep, ✓ MaxVelocity, ✓ RecoveryVelocity, ✓ AirDrag, + Simulation.Distance (bodies farther than this sleep) |
| Engine.Physics.Gravity | + Enabled, ✓ Strength, ✓ X, ✓ Y, ✓ Z, ✓ SetDirection(x, y, z) |
| Engine.Physics.Sleep | ✓ Enabled [AllowSleep], ✓ Threshold [SleepThreshold] |
| Engine.Physics.Body (instance) | ✓ Type [BodyType], ✓ Shape, ✓ Mass, ✓ Weight, ✓ Friction, ✓ Bounciness, ✓ LinearDamping, ✓ AngularDamping, ✓ GravityScale, ✓ UseGravity, ✓ LockRotation, ✓ Sensor, ✓ Awake, ✓ Layer, ✓ Mask, + Ccd.Enabled (fast bodies) |
| Engine.Physics.Velocity (instance) | ✓ X, ✓ Y, ✓ Z [VelocityX...], ✓ SpinX/Y/Z, ✓ Set(x, y, z), ✓ ApplyForce, ✓ ApplyImpulse, + ApplyTorque |
| Engine.Physics.Character | ✓ Create, ✓ Destroy, ✓ Move, ✓ SetHeight, ✓ SetPosition, ✓ X/Y/Z, ✓ Grounded, ✓ GroundNormalY, ✓ Swimming, ✓ Submerged, ✓ Fits, + StepHeight, + SlopeLimit, + Radius, + PushBodies.Enabled |
| Engine.Physics.Joint | ✓ Create(...), ✓ Destroy(...), + Strength, + Breakable.Enabled, + BreakForce, + Motor.Enabled, + Motor.Speed |
| Engine.Physics.Query | ✓ Raycast, ✓ HitX/Y/Z, ✓ HitNormalX/Y/Z, ✓ HitInstance, ✓ SphereCast, ✓ CapsuleCast, ✓ OverlapCapsule, ✓ PlaceFree, ✓ PlaceMeeting, ✓ PositionEmpty, + Layers (mask for queries) |
| Engine.Physics.Collision2D | ✓ Point, ✓ Rectangle, ✓ Circle, ✓ Distance3D, ✓ RectsOverlap, ✓ CirclesOverlap, ✓ PointInRect, ✓ PointInCircle |
| Engine.Physics.Focus | ✓ Add(id) [AddCollisionFocus], ✓ Remove(id) |
| Engine.Physics.Cloth (model part) | + Enabled("Part"), + Stiffness, + Damping, + Wind |

## Engine.Audio

| Part | Values |
|---|---|
| Engine.Audio | + Enabled (all sound), ✓ MasterVolume, + Muted, ✓ StopAll() |
| Engine.Audio.Music | + Enabled, ✓ Track [Music], ✓ Volume [MusicVolume], ✓ Muffle [MusicMuffle], ✓ Play("Track"), ✓ Stop(), + FadeSeconds, + Loop, + Position (seconds), + Pitch |
| Engine.Audio.Effects (sound effects bus) | + Enabled, ✓ Volume [SfxVolume], + Pitch |
| Engine.Audio.Bus (by name) | ✓ Volume(name) [Get/SetBusVolume], ✓ Duck(name, ...) [BusDuck], ✓ DuckLevel, + Muted(name) |
| Engine.Audio.Acoustics | + Enabled, ✓ Place [Acoustics] ("outdoors", "room", "hall", "cave"), ✓ Amount [AcousticsAmount] |
| Engine.Audio.Listener | ✓ FollowCamera, ✓ Set(x, y, z, ...) [SetListener], + X, + Y, + Z |
| Engine.Audio.Sound (one playing sound) | ✓ Play(...) [PlaySound], ✓ PlayAt(...), ✓ Stop, ✓ StopFaded, ✓ Fade, ✓ Volume [SoundSetVolume], ✓ Pitch, ✓ Position, ✓ Bus, ✓ IsPlaying, ✓ Preload, ✓ Loading (read), + Pan, + Loop, + MaxDistance, + Rolloff |
| Engine.Audio.Spatial | + Enabled, + Doppler, + MaxDistance, + Rolloff |

## Engine.Model (instance)

| Part | Values |
|---|---|
| Engine.Model | + Enabled (drawn) [SetMeshVisible for all], ✓ Name, ✓ ScaleX/Y/Z, ✓ Layer, ✓ ViewLayer, + Tint [SetTint], + Alpha |
| Engine.Model.Shadows | + Enabled, ✓ Cast [CastShadows], ✓ Receive [ReceiveShadows] |
| Engine.Model.Glow | ✓ Strength [Glow], ✓ EmissionScale, ✓ Material.Emission/EmissionColor/EmissionTint |
| Engine.Model.Material (by name) | ✓ Tint, ✓ Texture, + Roughness, + Metallic, + Alpha |
| Engine.Model.Mesh (by name) | ✓ Visible [SetMeshVisible], + Count (read) |
| Engine.Model.Morph | + Enabled [MorphSetEnabled], ✓ Weight(name), ✓ Reset(name), ✓ ResetAll() |
| Engine.Model.Hair | + Enabled, ✓ Styles, ✓ Color, ✓ BeardColor, ✓ Reset(), ✓ LastError |
| Engine.Model.Node (by name) | ✓ SetRotation, ✓ SetTranslation, ✓ Clear |
| Engine.Model.Socket (by name) | ✓ Attach, ✓ AttachOffset, ✓ Detach, ✓ Exists, ✓ X/Y/Z |
| Engine.Model.Static (many copies, no instances) | + Enabled, ✓ Add, ✓ AddFromNumList, ✓ Remove, ✓ Clear, ✓ Count, ✓ DrawnCount |
| Engine.Model.Detail | + Policy (automatic, pinned level, full), + Impostor.Enabled |
| Engine.Model.Outline | + Enabled, + Color, + Width, + ThroughWalls (= Engine.Shader.Outline) |

## Engine.Animation (instance)

| Part | Values |
|---|---|
| Engine.Animation | + Enabled (posed by its animations), ✓ Speed, ✓ Loop, ✓ IsPlaying, ✓ Play("Clip"), ✓ Stop() |
| Engine.Animation.Clip | ✓ Name (read) [ModelAnimationGetClip], ✓ Time, ✓ Length (read), ✓ Progress (read), ✓ Crossed(t) |
| Engine.Animation.RootMotion | ✓ Enabled [ModelAnimationSetRootMotion], ✓ DeltaX/Y/Z (read) |
| Engine.Animation.Controller | + Enabled, ✓ Use("Controller"), ✓ Error, ✓ Bool/Float/Text/Trigger (get, set), ✓ Parameter(name) |
| Engine.Animation.State | ✓ Current, ✓ Play, ✓ Stop, ✓ Create, ✓ AddTransition, ✓ HasFinished, ✓ Speed |
| Engine.Animation.Layer | ✓ Create, ✓ Weight, ✓ Additive |
| Engine.Animation.BlendTree | ✓ Create1D, ✓ CreateDirect, ✓ AddClip, ✓ Weight, ✓ Sync |
| Engine.Animation.Bones (IK) | + Enabled, ✓ LookAt, ✓ Reach, ✓ SetRotation, ✓ SetTranslation, ✓ Clear*, ✓ LookForward, + LookAt.Strength, + Reach.Strength |
| Engine.Animation.Detail | + BySize.Enabled (fewer poses when small) |

## Engine.Image (instance sprite)

| Part | Values |
|---|---|
| Engine.Image | ✓ Visible (= Enabled), ✓ Name, ✓ Frame, ✓ Speed, ✓ Alpha, ✓ Angle, ✓ ScaleX, ✓ ScaleY, ✓ Depth, ✓ BlendR/G/B |
| Engine.Image.Direction | ✓ Direction, ✓ Count (read), ✓ Frame, ✓ Frames (read) |
| Engine.Image.Rig | + Enabled, ✓ Bind, ✓ Play, ✓ Stop, ✓ Seek, ✓ Pose, ✓ Aim, ✓ BoneRotate, ✓ JointMove, ✓ Reset, ✓ Clear, ✓ IsBound, ✓ Error |
| Engine.Image.Flip | + X, + Y |
| Engine.Image.Origin | + X, + Y |

## Engine.Instance (instance)

| Part | Values |
|---|---|
| Engine.Instance | + Enabled (updated: off = frozen), ✓ Visible, ✓ Solid, ✓ Depth, ✓ X, ✓ Y, ✓ Z, ✓ Yaw, ✓ Pitch, ✓ Roll, ✓ ScaleX/Y/Z, ✓ Create, ✓ Destroy, ✓ Exists, ✓ Count, ✓ Find, ✓ Number |
| Engine.Instance.Motion | ✓ Speed, ✓ Direction, ✓ HSpeed, ✓ VSpeed, + Friction, + Gravity, + GravityDirection |
| Engine.Instance.Parts (Objects inside Objects) | ✓ Parent, ✓ ChildCount [Count], ✓ Child(i), ✓ Attach(id, x, y, z), ✓ Detach() |
| Engine.Instance.Nearby | ✓ Nearest, ✓ NearestOther, ✓ Furthest, ✓ InRadius, ✓ FirstInRadius, ✓ DistanceTo |
| Engine.Instance.Variables | ✓ Get, ✓ Set, ✓ Exists |
| Engine.Instance.Bulk | ✓ SetPositionsFromNumList, ✓ SetTransformsFromNumList, ✓ SetTintsFromNumList, ✓ GetPositionsToNumList, ✓ SceneQueue* |
| Engine.Instance.Update | + Distance (beyond this an instance's Step is skipped), + Rate (steps a second when far) |

## Engine.Input

| Part | Values |
|---|---|
| Engine.Input | + Enabled (all input read) |
| Engine.Input.Keyboard | + Enabled, ✓ Check(key), ✓ Pressed, ✓ Released, ✓ TextTake() |
| Engine.Input.Mouse | + Enabled, ✓ Captured [MouseCaptured], ✓ Check, ✓ Pressed, ✓ Released, ✓ Wheel, ✓ LookDeltaX/Y, ✓ WorldX/Y [PointerWorldX], + X, + Y, + Sensitivity, + InvertY |
| Engine.Input.Gamepad | + Enabled, ✓ Count, ✓ Connected, ✓ Axis, ✓ AxisRaw, ✓ Check, ✓ Pressed, ✓ Released, ✓ DeadZone, ✓ Vibrate, ✓ KeyboardEmulation, + VibrationStrength |
| Engine.Input.Actions | + Enabled, ✓ Bind, ✓ Clear, ✓ Check, ✓ Pressed, ✓ Released, ✓ Value, ✓ Axis, ✓ Listen, ✓ Listening, ✓ BindingCount, ✓ BindingName, ✓ Load, ✓ Save |
| Engine.Input.Record | ✓ Start, ✓ Stop, ✓ Active |
| Engine.Input.Replay | ✓ Start, ✓ Stop, ✓ Active, ✓ Finished, ✓ Frame |
| Engine.Input.Touch | + Enabled, + Count, + X(i), + Y(i) |

## Engine.Navigation

| Part | Values |
|---|---|
| Engine.Navigation | + Enabled, ✓ Bake(), ✓ BakeGrid(...), + Baked (read) |
| Engine.Navigation.Mesh | + AgentRadius, + AgentHeight, + MaxSlope, + StepHeight, + CellSize |
| Engine.Navigation.Agent (instance) | + Enabled, ✓ Destination, ✓ Speed, ✓ Radius, ✓ HasArrived, ✓ DistanceRemaining, ✓ Link, ✓ LinkProgress, ✓ ConfigureUpdateRates |
| Engine.Navigation.Avoidance (instance) | ✓ Enabled [AgentSetAvoidanceEnabled], ✓ Configure(...), + Radius, + Priority |
| Engine.Navigation.Path | ✓ Find, ✓ Follow, ✓ Free, ✓ Seek, ✓ Stop, ✓ Wander, ✓ WaypointCount, ✓ WaypointX/Y/Z, ✓ WaypointLink |
| Engine.Navigation.Obstacles | ✓ Add, ✓ Remove |
| Engine.Navigation.Links | ✓ Add, ✓ Remove |
| Engine.Navigation.Areas | ✓ Cost(area) [Get/SetAreaCost] |

## Engine.Network

| Part | Values |
|---|---|
| Engine.Network | + Enabled, ✓ Available, ✓ Host, ✓ Connect, ✓ Disconnect, ✓ IsHost, ✓ IsConnected, ✓ ConnectState, ✓ PeerCount, ✓ PeerId, ✓ Port, ✓ LastError, ✓ LocalAddresses |
| Engine.Network.Discovery | + Enabled, ✓ Discover, ✓ FoundCount, ✓ FoundName, ✓ FoundAddress, ✓ FoundPort, ✓ FoundPlayers |
| Engine.Network.Online | ✓ HostOnline, ✓ JoinOnline, ✓ SessionCode, ✓ HostName |
| Engine.Network.Replication | + Enabled, ✓ Replicate, ✓ Own, ✓ Forget, ✓ IsCopy, ✓ CopyCount, ✓ CopyId, ✓ CopyObject, ✓ CopyOwner, ✓ InterestDistance, + Rate (updates a second), + Smoothing |
| Engine.Network.Messages | ✓ Send, ✓ SendNumber, ✓ SendText, ✓ Broadcast, ✓ Receive, ✓ Pending, ✓ From, ✓ Tag, ✓ Number, ✓ Text |
| Engine.Network.Values | ✓ GetNumber, ✓ SetNumber, ✓ GetText, ✓ SetText |

## Engine.Ui

| Part | Values |
|---|---|
| Engine.Ui | + Enabled (drawn and clickable), ✓ Theme [SetTheme], ✓ Draw() [DrawUi], ✓ Update() |
| Engine.Ui.Focus | ✓ Focused (read) [GetFocused], ✓ Set(id), ✓ Hovered (read), + Navigation.Enabled (keys/gamepad move focus) |
| Engine.Ui.Widget (by id) | ✓ Visible, ✓ Enabled, ✓ Text, ✓ Value, ✓ Selected, ✓ Scroll, ✓ Clicked, ✓ Submitted, ✓ ValueChanged, ✓ HitTest |
| Engine.Ui.Mouse | ✓ X, ✓ Y |
| Engine.Ui.Scale | + Value, + Auto.Enabled |

## Engine.Draw (2D drawing state and 3D debug shapes)

| Part | Values |
|---|---|
| Engine.Draw | + Enabled (Draw events run), ✓ Color, ✓ Alpha, ✓ OffsetX, ✓ OffsetY, ✓ BlendLinear, ✓ LineWidth, ✓ Clear |
| Engine.Draw.Text | ✓ Font, ✓ FontSize, ✓ Align [TextAlign], ✓ Tracking, ✓ Width(...), ✓ Height(...) |
| Engine.Draw.Clip | + Enabled, ✓ Set(x, y, w, h), ✓ Reset() |
| Engine.Draw.Shapes | ✓ Line, ✓ Rect/Rectangle, ✓ RoundRect, ✓ Circle, ✓ Ellipse, ✓ Triangle, ✓ Arrow, ✓ Cross, ✓ Grid, ✓ HealthBar, ✓ RectanglesFromList |
| Engine.Draw.Images | ✓ Sprite, ✓ SpriteScaled, ✓ SpritePart, ✓ SpritePartsFromList, ✓ Texture, ✓ TexturePart, ✓ Self |
| Engine.Draw.Shapes3D | + Enabled, ✓ Box, ✓ Cube, ✓ Sphere, ✓ Line, ✓ Point, ✓ Pillar, ✓ Wall, ✓ Floor, ✓ Grid, ✓ Text3D, ✓ PointLight |
| Engine.Draw.Models | ✓ Model3D, ✓ ModelTransform3D, ✓ ModelShader3D, ✓ ModelGui, ✓ ModelGuiPose |

## Engine.Mesh (meshes a script builds)

| Part | Values |
|---|---|
| Engine.Mesh | ✓ Create, ✓ Destroy, ✓ Clear, ✓ VertexCount, ✓ TriangleCount, ✓ IsUploaded |
| Engine.Mesh.Build | ✓ AddVertex, ✓ AddTriangle, ✓ AddQuad, ✓ AddQuadColors, ✓ AddCube, ✓ AddCubeTiles, ✓ AddQuadsFromList/NumList, ✓ AddVerticesFromList/NumList |
| Engine.Mesh.Draw | ✓ Draw [DrawMesh3D], ✓ DrawTransform, ✓ DrawShader, ✓ Cull, ✓ Fog, ✓ Glow, ✓ Shadows, ✓ Sky, ✓ Transparent, ✓ ResetState |
| Engine.Mesh.Static | + Enabled, ✓ Add, ✓ Move, ✓ Remove, ✓ Clear, ✓ Count, ✓ DrawnCount |
| Engine.Mesh.Upload | ✓ Budget [UploadBudget] |
| Engine.Mesh.Collider (instance) | ✓ Set [InstanceSetMeshCollider], + Enabled |

## Engine.Shader

| Part | Values |
|---|---|
| Engine.Shader (instance) | + Enabled, ✓ Name, ✓ Set("Shader"), ✓ Parameter(name) [SetParameter], ✓ Vector(name) [SetVector], ✓ Texture(name) [SetTexture] |
| Engine.Shader.Outline (instance) | + Enabled, ✓ Set(r, g, b, width) [OutlineSet], ✓ Clear(), ✓ Has (read) |

## Engine.Tile

| Part | Values |
|---|---|
| Engine.Tile | + Enabled (tile layers drawn), ✓ SetAt, ✓ IndexAt, ✓ RemoveAt, ✓ Meeting, ✓ MoveX, ✓ MoveY |
| Engine.Tile.Autotile | ✓ Enabled [Autotile], ✓ PaintAt, ✓ EraseAt |
| Engine.Tile.Layer (by name) | + Visible, + Alpha, + Depth, + Solid, + ScrollX, + ScrollY |
| Engine.Tile.Animation | + Enabled, + Speed |

## Engine.Room

| Part | Values |
|---|---|
| Engine.Room | ✓ Name (read), ✓ Width, ✓ Height, ✓ GridSize, ✓ IsThreeD, ✓ Speed, ✓ LayerCount, ✓ NodeCount, ✓ Goto("Room"), ✓ GotoWhenLoaded, ✓ Restart() |
| Engine.Room.Change (transition) | ✓ Changing (read), ✓ Fade, ✓ Colors, ✓ Text, ✓ ProgressBar, ✓ MinimumTime, ✓ Budget, + Style ("fade", "cut", "wipe") |
| Engine.Room.Preload | + Enabled, ✓ Preload("Room"), ✓ Pending, ✓ Progress [LoadProgress] |
| Engine.Room.Values | ✓ Get(name), ✓ Set(name, value) |
| Engine.Room.Layer (by name) | + Visible, + Depth |

## Engine.Save

| Part | Values |
|---|---|
| Engine.Save | + Enabled (writes allowed), ✓ GetNumber, ✓ SetNumber, ✓ TrySetNumber, ✓ Exists, ✓ Clear, ✓ Flush, ✓ LastError |
| Engine.Save.Slot | ✓ Count, ✓ Exists, ✓ Read, ✓ Write, ✓ Delete, ✓ Name, ✓ LastError, + Current |
| Engine.Save.Auto | + Enabled, + Seconds |

## Engine.Job (script work on worker threads)

| Part | Values |
|---|---|
| Engine.Job | + Enabled, ✓ ScriptCreate, ✓ ScriptStart, ✓ RunScript, ✓ Status, ✓ Take, ✓ Cancel, ✓ Release, ✓ Error, ✓ LastError, ✓ Result* |
| Engine.Job.Share | ✓ Variable, ✓ VariableText, ✓ List/ListRegion, ✓ Grid/GridRegion, ✓ Map, ✓ NumList/NumListRegion, ✓ Mesh, ✓ ShareAll |
| Engine.Job.Order | ✓ After, ✓ Priority, ✓ Urgent, ✓ Budget |
| Engine.Job.Cache | ✓ Get, ✓ Set, ✓ NumList, ✓ Reset |

## Engine.Debug

| Part | Values |
|---|---|
| Engine.Debug | + Enabled (F6 screen), ✓ Log(...), ✓ Watch(name, value), ✓ TestCheck |
| Engine.Debug.Keys | ✓ Enabled [KeysSetEnabled] |
| Engine.Debug.Budget | ✓ Enabled [FrameBudgetEnable], ✓ Set, ✓ SetFrame, ✓ PartMs, ✓ Report, ✓ Reset |
| Engine.Debug.Gpu | ✓ Enable("Shadows"...) [EnableGPUFunction], ✓ Disable(...) |
| Engine.Debug.Screenshot | ✓ Save, ✓ Pending, ✓ LastPath |
| Engine.Debug.Overlay | + Fps.Enabled, + Colliders.Enabled, + Navigation.Enabled, + Probe.Enabled (show the light probe), + Wireframe.Enabled |

## Engine.Recording

| Part | Values |
|---|---|
| Engine.Recording | + Enabled, ✓ Start, ✓ Stop, ✓ IsRecording |
| Engine.Recording.Replay | ✓ Play, ✓ Stop, ✓ IsPlaying, ✓ Progress, ✓ Draw |
| Engine.Recording.Buffer | ✓ Enabled [ReplayBufferEnable/Disable], ✓ Seconds, ✓ Save |

## Engine.Language

| Part | Values |
|---|---|
| Engine.Language | ✓ Name, ✓ Count, ✓ At(i), ✓ Get(key), ✓ Set(name) |

## Engine.Texture and Engine.Resource

| Part | Values |
|---|---|
| Engine.Texture | ✓ Create, ✓ Destroy, ✓ Exists, ✓ Width, ✓ Height, ✓ SetPixels, ✓ SetRegion, ✓ FillRectanglesFromList, ✓ Smooth [SetSmooth] |
| Engine.Resource | ✓ the resource lookups (Exists, names, kinds) |

## Areas with no Enabled (data and helpers)

These are tools, not systems, so they get no Enabled; their commands keep their names:
Engine.Alarm (10), Engine.Array (25), Engine.Grid (33), Engine.Json (4), Engine.List (31), Engine.Map (16),
Engine.Math (56), Engine.Noise (10), Engine.NumList (22), Engine.Priority (12), Engine.Queue (6),
Engine.Stack (6), Engine.Text (38), Engine.Time (9), Engine.File (6), Engine.Global (6), Engine.Variable (6).

---

## Count

- Areas: 34 sections above, plus 17 data and helper areas without Enabled.
- Parts: 210 (each row above).
- Values and commands: 767 ✓ already there, 444 + to expose. The data and helper areas' 296 commands
  are not counted.

## Suggested order

1. Display (window management) and Camera: the most asked-for, and mostly values the engine holds.
2. Rendering parts (Shadows, Fog, Bloom...) and Lighting.Sun/Ambient: the room's look from a script.
3. Audio, Physics.World/Gravity/Sleep, Input: renames into parts plus Enabled.
4. The rest, area by area, each with its own tests.
