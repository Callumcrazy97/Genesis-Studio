using System.Globalization;
using System.Text;
using Genesis.Application.Core.Projects;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Commands;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The engine API as the standard describes it, <c>Engine.&lt;Area&gt;.&lt;Part&gt;.&lt;Value&gt;</c>, taken from the
/// live registry rather than a list kept here, so a part added later is covered by its first run:
/// for every area and every part a case reads each value's default, sets values, reads them back
/// through a script (the language path) and through the Inspector's settings (EngineSettings),
/// toggles <c>Enabled</c> where the area or part has one and sees its effect where the harness can
/// observe one; and the registry's own rules (Enabled everywhere, every value in the Inspector, every
/// command with a signature). What the headless harness cannot observe (a window, a renderer) is
/// named in the page it writes, <c>engine-api-parts.md</c>.
/// </summary>
internal static class EngineApiSuite
{
    // The standard gives every area and every part an Enabled. These can be switched and have no Enabled
    // yet: the check fails for any other area or part that lacks one (and is not one of the exceptions
    // below), and says which of these now have one. Of the 59 the coverage pass found on 29 Oct 2026, 28
    // got an Enabled that day (PgslCommands.PartSwitches.cs and beside their values), 17 are exceptions,
    // and these are left.
    private static readonly Dictionary<string, string> ScopesWithoutEnabled = new(StringComparer.Ordinal)
    {
        // Another engineer's areas: Engine.Network is being worked on (NAT punch-through), as is Engine.Lighting.Probes.
        ["Network.Replication"] = "left alone while the Engine.Network area is being changed",
        ["Lighting.Probe"] = "left alone while placed light probes (Engine.Lighting.Probes) are being added",
        // One instance's own look, drawn from its components: each needs a per-instance flag read in the model, sprite and shader draw paths.
        ["Animation"] = "per-instance: needs a flag the animation update reads",
        ["Image"] = "per-instance: needs a flag the sprite draw reads",
        ["Model"] = "per-instance: needs a flag the model draw reads",
        ["Model.Glow"] = "per-instance: needs flags the model draw reads",
        ["Model.Shadows"] = "per-instance: needs a flag the model draw reads (Cast and Receive stay as set)",
        ["Shader"] = "per-instance: needs a flag the object draw pass reads",
        // Every reader of the input state (cameras, input actions, scripts, replay) would have to follow.
        ["Input"] = "the keyboard, mouse and controller readers all need to follow it",
        ["Input.Gamepad"] = "the controller readers (buttons, sticks, actions, emulation) need to follow it",
        ["Input.Mouse"] = "the mouse readers (buttons, position, look, wheel) need to follow it",
        // The renderer's sky and the water surface's look are drawn by passes that have no switch to read yet.
        ["Sky"] = "the sky composite has no switch to read yet",
        ["Terrain.Detail"] = "the streaming and level-of-detail distances have no 'off' to fall back to yet",
        ["Water.Surface"] = "the water bodies' look has no 'off' to fall back to yet (Engine.Water.Enabled hides the water)",
    };

    // Areas and parts that have values and are not switches, each with the reason: a pure reading, a
    // configuration, or something the game cannot run without. The standard gives every area and part an
    // Enabled where it can honestly be switched (it does something when false and is on by default).
    private static readonly Dictionary<string, string> ScopesWithoutEnabledByDesign = new(StringComparer.Ordinal)
    {
        ["Camera"] = "the camera is how the game is seen; its parts have their own Enabled (Rigs, FreeFly, Orbit, Shoulder, Collision, Focus) and Engine.Camera.Rigs.Enabled hands the camera back",
        ["Camera.Rig"] = "the values of the camera rig being set up (the last made, or the one named), a configuration and not a system that acts; Engine.Camera.Rigs.Enabled switches whether the rigs drive the camera",
        ["Camera.Rig.Path"] = "the dolly rig's track, a configuration of that rig; Engine.Camera.Rigs.Enabled switches whether the rigs drive the camera",
        ["Display"] = "the window is the game's only output; hiding it would end the game for the player (its parts: Cursor, Resolution and the window's attributes)",
        ["Display.Monitor"] = "which monitor the window is on, and readings about it (its size, rate and count); there is nothing of it to switch",
        ["Display.Window"] = "the window's own attributes (place, Resizable, AlwaysOnTop, Maximized, Minimized): what it has, not a system that acts",
        ["Game.Scripts"] = "switching every script off would stop the script that switches them back on; Engine.Game.Enabled (Paused) stops the game's time",
        ["Language"] = "the current language and its texts, a setting and not a system",
        ["Navigation.Mesh"] = "the settings of the next bake (agent size, slope, step height, cell size), a configuration; Engine.Navigation.Enabled switches the agents",
        ["Physics.Character"] = "a character controller exists or not (CharacterCreate, CharacterDestroy); its values are step height and slope limit",
        ["Rendering"] = "the 3D picture itself, with a switch on each of its parts (Shadows, Fog, Ao, Bloom, Grading, Exposure, AntiAliasing...)",
        ["Rendering.Models"] = "KeepPreviousTransform is a policy for how a model keeps its transform when it changes, not a part that acts",
        ["Room"] = "a room is always there; its values describe it (name, size, grid), and its parts Change and Streaming have their own Enabled",
        ["Terrain.Collision"] = "everything would fall through; Engine.Terrain.Enabled hides the ground instead (EngineApiAreas.md)",
        ["Terrain.Water"] = "the leak tool's settings (radius, shape, discharge, angle) for punching holes, a scratchpad for authoring; Engine.Water.Enabled switches the water",
        ["View"] = "chooses which of the room's viewports the View commands address; each viewport has its own Enabled (Engine.View.Camera.Enabled)",
        ["Weather.Temperature"] = "the air's temperature follows the weather until a script sets it; Engine.Weather.Release() hands it back",
    };

    // Values the blind sweep leaves alone, each for a reason.
    private static readonly Dictionary<string, string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Engine.Game.Threads"] = "0 means automatic and reads as the number in use, so a read-and-restore cannot put it back; Engine.Pgsl.Logic.EngineApi sets and restores it",
        ["Engine.Instance.Parent"] = "names another instance; Engine.Pgsl.Logic.EngineApi covers attaching and detaching",
        ["Engine.Draw.Color"] = "a colour value, not a number, text or flag",
    };

    // Values that read their default whatever is set in the probe room, each with the reason (listed on
    // 29 Oct 2026). Every other value must read back what is set, so a value that stops doing so fails its
    // case; one that starts to is noted and can leave the list. The values that once read 0 because they
    // had a setter and no getter (Camera.Orbit*, Lighting.Intensity, Draw.Font...) have getters now.
    private static readonly Dictionary<string, string> KnownSilent = new(StringComparer.OrdinalIgnoreCase)
    {
        // Needs a window or the monitor: nothing in a headless run holds it.
        ["Engine.Display.Fullscreen"] = "the window's mode; needs a window",
        ["Engine.Display.Height"] = "the window's height; needs a window",
        ["Engine.Display.Width"] = "the window's width; needs a window",
        ["Engine.Display.MaxFps"] = "the window's frame-rate cap; needs a window",
        ["Engine.Display.VSync"] = "the window's vertical sync; needs a window",
        ["Engine.Display.Monitor.Index"] = "which monitor the window is on; needs a window",
        ["Engine.Display.Window.AlwaysOnTop"] = "a window's attribute; needs a window",
        ["Engine.Display.Window.Maximized"] = "a window's attribute; needs a window",
        ["Engine.Display.Window.Minimized"] = "a window's attribute; needs a window",
        ["Engine.Display.Window.Resizable"] = "a window's attribute; needs a window",
        ["Engine.Display.Window.X"] = "the window's place on the desktop; needs a window",
        ["Engine.Display.Window.Y"] = "the window's place on the desktop; needs a window",
        ["Engine.Room.Speed"] = "the game window's target frame rate (it has a getter); needs a window",
        ["Engine.Input.Mouse.Captured"] = "the game window captures the mouse (it has a getter); needs a window",
        ["Engine.Input.MouseCaptured"] = "the game window captures the mouse (it has a getter); needs a window",
        // Needs something the probe room does not have.
        ["Engine.Debug.Keys.Enabled"] = "the debug keys act only in a game started to be worked on",
        ["Engine.Tile.Enabled"] = "a 2D room's tile layers",
        ["Engine.Network.Replication.Rate"] = "a network session with replication",
        ["Engine.Physics.Character.SlopeLimit"] = "a character controller on the instance",
        ["Engine.Physics.Character.StepHeight"] = "a character controller on the instance",
        ["Engine.Image.Direction"] = "a directional Image on the instance (the getter reads the facing of the frame shown; ModelSpriteConversionSuite checks it)",
        ["Engine.Audio.Snapshot.Enabled"] = "true only while a blend moves; only false can be set (it holds the blend) and the probe room has no blend to hold",
    };

    // Values a camera rig, made by a script, is set up with: EngineSettings leaves them out because a
    // resource has no rig to give them to, so the Inspector's round trip does not apply to them.
    private static bool NotInspectorSetting(string name) => name.StartsWith("Engine.Camera.Rig.", StringComparison.OrdinalIgnoreCase);

    // Engine defects the sweep found and does not fix: each is listed in the page the sweep writes, and the
    // case says so when one reads right again. None is open now: Draw.Font, Draw.TextAlign and Shader.Name,
    // which were set with text and read as numbers, read back the text they were set with.
    private static readonly Dictionary<string, string> KnownDefects = new(StringComparer.OrdinalIgnoreCase);

    // What turning a part or area off and on changes, where the harness can see it without a renderer.
    private static readonly Dictionary<string, Func<EngineApiGame, bool>> Effects = new(StringComparer.Ordinal)
    {
        ["Lighting"] = game => game.Environment.LightingEnabled,
        ["Rendering.Shadows"] = game => game.Environment.ShadowsEnabled,
        ["Rendering.Fog"] = game => game.Environment.FogEnabled,
        ["Rendering.Ao"] = _ => MeshLightingDefaults.GtaoEnabled,
        ["Rendering.Bloom"] = _ => MeshLightingDefaults.BloomEnabled,
        ["Camera.Focus"] = _ => Genesis.Shared.Rendering.DepthOfField.Enabled,
        ["Camera.FreeFly"] = game => game.Scene.FreeFlyCamera == true,
        ["Display.Resolution.Dynamic"] = _ => Genesis.Shared.Rendering.RenderScale.Dynamic,
        ["Rendering.Detail"] = _ => Genesis.Runtime.Modeling.ModelLodView.Enabled,
        // The parts switched on 29 Oct 2026 whose effect is a state the engine keeps and a frame is made from.
        ["Rendering.Culling"] = _ => Genesis.Shared.Rendering.RenderAutoState.FrustumCulling,
        ["Display.Resolution"] = _ => Genesis.Shared.Rendering.RenderScale.Enabled,
        ["Physics.Gravity"] = game => game.Scene.Physics!.GetActiveGravityAcceleration().LengthSquared() > 0f,
        ["Physics.World"] = game => game.Scene.Physics!.SimulationEnabled,
        ["Instance.Update"] = game => game.Environment.SimulationDistanceEnabled,
        ["Weather"] = game =>
        {
            Genesis.Runtime.Climate.WeatherState acting = game.Scene.Climate.Overrides.Apply(new Genesis.Runtime.Climate.WeatherState { Rain = 0.5f, WindSpeed = 5f, Snow = 0.5f, Lightning = 0.5f });
            return acting.Rain > 0f || acting.WindSpeed > 0f || acting.Snow > 0f || acting.Lightning > 0f;
        },
        ["Lighting.Sun"] = game => FrameAfterPartSwitches(game, new Genesis.Shared.Interfaces.Mesh3DState { SunIntensity = 1.1f }).SunIntensity > 0f,
        ["Lighting.Ambient"] = game => FrameAfterPartSwitches(game, new Genesis.Shared.Interfaces.Mesh3DState { AmbientColor = new System.Numerics.Vector3(0.3f), SkyLightStrength = 1f }).AmbientColor != System.Numerics.Vector3.Zero,
        ["Lighting.Glow"] = game => FrameAfterPartSwitches(game, new Genesis.Shared.Interfaces.Mesh3DState { EmissiveIntensity = 1f }).EmissiveIntensity > 0f,
    };

    // A frame state after the room's lighting parts a script switched off are taken out of it.
    private static Genesis.Shared.Interfaces.Mesh3DState FrameAfterPartSwitches(EngineApiGame game, Genesis.Shared.Interfaces.Mesh3DState frame)
    {
        Genesis.Runtime.Scene.EnvironmentMapper.ApplyPartSwitches(ref frame, game.Environment);
        return frame;
    }

    private enum Kind
    {
        /// <summary>Read back what was set.</summary>
        RoundTrip,
        /// <summary>Read back something else than it was, not the default either: kept in range, rounded.</summary>
        Kept,
        /// <summary>Always read back the default: nothing here observes it (it needs a window, a renderer, a network).</summary>
        Silent,
        /// <summary>Text with no choices or colour to try: read, and set to what it was.</summary>
        Text,
        /// <summary>Can be set and not read.</summary>
        WriteOnly,
        Skipped,
        /// <summary>An engine defect found by the sweep and listed in <see cref="KnownDefects"/>.</summary>
        Defect,
    }

    private sealed record PropertyResult(string Name, Kind Kind, string Detail);

    private sealed record ScopeResult(string Scope, List<PropertyResult> Properties, string Enabled, string Effect, string Failure);

    private static readonly List<ScopeResult> Results = [];

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "EngineApi");
        Results.Clear();
        RunRegistryCases(ctx);
        RunCommandCases(ctx);

        string parent = Path.Combine(ctx.Workspace, "EngineApi");
        Directory.CreateDirectory(parent);
        ProjectSession project = new ProjectService().CreateProject(parent, "Api" + Guid.NewGuid().ToString("N")[..6], "Blank");
        EngineApiGame? created = null;
        try
        {
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Game.ARoomWithoutAWindowRunsScripts", () =>
            {
                created = new EngineApiGame(project);
                EngineApiRun run = created.Run("a = 1 + 2; b = Engine.Instance.X;");
                HeadlessHarness.Assert(!run.Failed && run.Number("a") == 3 && !double.IsNaN(run.Number("b")), "The probe room did not run a script: " + run.Errors);
            });
            if (created is not EngineApiGame game) return;

            // Engine.Camera.Rig's values are the rig being set up: a script makes one first, as a game would.
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Game.TheProbeRoomHasACameraRigToSetUp", () =>
            {
                EngineApiRun made = game.Run("ok = Engine.Camera.Rigs.Create(\"probe\", \"dolly\"); name = Engine.Camera.Rig.Name;");
                HeadlessHarness.Assert(!made.Failed && made.Number("ok") == 1 && made.Text("name") == "probe", "The probe room could not make a camera rig: " + made.Errors);
            });
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Getters.DrawFontTextAlignAndShaderNameReadBackTheTextSet", () => TextValuesReadBackAsText(game));
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Switches.PostProcessingPartsAreDrawnNeutralWhileSwitchedOffAndKeepTheirValues", () => PostProcessingPartsSwitchOff(game));
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Switches.CullingOffDrawsEverythingAndKeepsFrustumAndOcclusionForWhenItIsOn", () => CullingSwitchesOff(game));

            foreach (string area in EngineApiModel.Areas)
            {
                // Areas of commands only (Math, List) have no value to sweep.
                if (!EngineApiModel.Members.Any(member => member.IsProperty && member.Scope == area)) continue;
                HeadlessHarness.RunCase(ctx.Report, $"Engine.Api.Areas.{area}.{CaseSuffix(area)}", () => Sweep(game, area));
            }
            foreach (string part in EngineApiModel.Parts)
                HeadlessHarness.RunCase(ctx.Report, $"Engine.Api.Parts.{part}.{CaseSuffix(part)}", () => Sweep(game, part));

            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Parts.TheSweepWroteItsPage", () => WriteReport(ctx));
            HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Debug.F6ListsRealValuesThatTheInspectorCanSet", () => DebugScreenValues(game));
        }
        finally
        {
            created?.Dispose();
        }
    }

    // Engine.Draw.Font, Engine.Draw.TextAlign and Engine.Shader.Name are set with text and once read back as numbers:
    // each gives the text now (a text align by its canonical name), and an instance with no Shader reads "".
    private static void TextValuesReadBackAsText(EngineApiGame game)
    {
        EngineApiRun run = game.Run(
            "Engine.Draw.Font = \"Courier New\"; fontSet = Engine.Draw.Font; Engine.Draw.Font = \"Arial\"; fontReset = Engine.Draw.Font;\n"
            + "Engine.Draw.TextAlign = \"centre\"; alignCentre = Engine.Draw.TextAlign; Engine.Draw.TextAlign = \"END\"; alignRight = Engine.Draw.TextAlign; Engine.Draw.TextAlign = \"left\"; alignLeft = Engine.Draw.TextAlign;\n"
            + "shaderBefore = Engine.Shader.Name; Engine.Shader.Name = \"Camo\"; shaderSet = Engine.Shader.Name; Engine.Shader.Name = \"\"; shaderAfter = Engine.Shader.Name;");
        HeadlessHarness.Assert(!run.Failed, "The script failed: " + run.Errors);
        string[] names = ["fontSet", "fontReset", "alignCentre", "alignRight", "alignLeft", "shaderBefore", "shaderSet", "shaderAfter"];
        string[] got = names.Select(name => run.Variables.TryGetValue(name, out object? value) && value is string text ? text : "<" + (value?.GetType().Name ?? "nothing") + ">").ToArray();
        string[] want = ["Courier New", "Arial", "center", "right", "left", "", "Camo", ""];
        HeadlessHarness.Assert(got.SequenceEqual(want, StringComparer.Ordinal),
            $"Font, text align and shader name read back {string.Join(" | ", got)}; expected {string.Join(" | ", want)}.");
    }

    // Grading, Exposure, MotionBlur and AntiAliasing off: the frame state the renderer is given has them neutral, and
    // has them as set again when they are back on (the sweep only sees that Enabled reads back).
    private static void PostProcessingPartsSwitchOff(EngineApiGame game)
    {
        // Variable names that are not the engine's own (a script's contrast = ... would be the engine's Contrast).
        EngineApiRun original = game.Run("wasContrast = Engine.Rendering.Grading.Contrast; wasSaturation = Engine.Rendering.Grading.Saturation; wasVignette = Engine.Rendering.Grading.Vignette; "
            + "wasExposure = Engine.Rendering.Exposure.Value; wasBlur = Engine.Rendering.MotionBlur.Strength; wasSmoothing = Engine.Rendering.AntiAliasing.Mode;");
        HeadlessHarness.Assert(!original.Failed, "The script failed: " + original.Errors);
        Genesis.Shared.Interfaces.Mesh3DState Frame()
        {
            var state = new Genesis.Shared.Interfaces.Mesh3DState();
            Genesis.Shared.Interfaces.MeshLightingDefaults.Apply(ref state);
            return state;
        }
        const string Switches = "Engine.Rendering.Grading.Enabled = {0}; Engine.Rendering.Exposure.Enabled = {0}; Engine.Rendering.MotionBlur.Enabled = {0}; Engine.Rendering.AntiAliasing.Enabled = {0};";
        try
        {
            game.Run("Engine.Rendering.Grading.Contrast = 1.5; Engine.Rendering.Grading.Saturation = 0.5; Engine.Rendering.Grading.Vignette = 0.4; "
                + "Engine.Rendering.Exposure.Value = 2; Engine.Rendering.MotionBlur.Strength = 0.5; Engine.Rendering.AntiAliasing.Mode = \"fxaa\";");
            Genesis.Shared.Interfaces.Mesh3DState on = Frame();
            HeadlessHarness.Assert(on.Contrast == 1.5f && on.Saturation == 0.5f && Math.Abs(on.VignetteStrength - 0.4f) < 1e-4f && on.Exposure == 2f
                && Math.Abs(on.MotionBlur - 0.5f) < 1e-4f && on.AntiAliasing != Genesis.Shared.Interfaces.AntiAliasingMode.Off,
                $"With the parts on the frame has contrast {on.Contrast}, saturation {on.Saturation}, vignette {on.VignetteStrength}, exposure {on.Exposure}, blur {on.MotionBlur}, smoothing {on.AntiAliasing}.");
            HeadlessHarness.Assert(!game.Run(string.Format(Switches, "false")).Failed, "Switching the parts off failed.");
            Genesis.Shared.Interfaces.Mesh3DState off = Frame();
            HeadlessHarness.Assert(off.Contrast == 1f && off.Saturation == 1f && off.VignetteStrength == 0f && off.Exposure == 1f && off.MotionBlur == 0f
                && off.AntiAliasing == Genesis.Shared.Interfaces.AntiAliasingMode.Off,
                $"With the parts off the frame has contrast {off.Contrast}, saturation {off.Saturation}, vignette {off.VignetteStrength}, exposure {off.Exposure}, blur {off.MotionBlur}, smoothing {off.AntiAliasing}.");
            EngineApiRun kept = game.Run("keptContrast = Engine.Rendering.Grading.Contrast; keptExposure = Engine.Rendering.Exposure.Value; keptSmoothing = Engine.Rendering.AntiAliasing.Mode;");
            HeadlessHarness.Assert(kept.Number("keptContrast") == 1.5 && kept.Number("keptExposure") == 2 && kept.Text("keptSmoothing") == "fxaa",
                $"The values of parts switched off must be kept: contrast {kept.Number("keptContrast")}, exposure {kept.Number("keptExposure")}, smoothing {kept.Text("keptSmoothing")}.");
            game.Run(string.Format(Switches, "true"));
            Genesis.Shared.Interfaces.Mesh3DState back = Frame();
            HeadlessHarness.Assert(back.Contrast == 1.5f && back.Exposure == 2f && back.AntiAliasing != Genesis.Shared.Interfaces.AntiAliasingMode.Off,
                "Switched on again, the parts must come back as they were set.");
        }
        finally
        {
            game.Run(string.Format(Switches, "true")
                + $" Engine.Rendering.Grading.Contrast = {EngineApiGame.Literal(original.Variables["wasContrast"])}; Engine.Rendering.Grading.Saturation = {EngineApiGame.Literal(original.Variables["wasSaturation"])};"
                + $" Engine.Rendering.Grading.Vignette = {EngineApiGame.Literal(original.Variables["wasVignette"])}; Engine.Rendering.Exposure.Value = {EngineApiGame.Literal(original.Variables["wasExposure"])};"
                + $" Engine.Rendering.MotionBlur.Strength = {EngineApiGame.Literal(original.Variables["wasBlur"])}; Engine.Rendering.AntiAliasing.Mode = {EngineApiGame.Literal(original.Variables["wasSmoothing"])};");
        }
    }

    // Culling off clears the engine's four culling switches; Frustum and Occlusion read and take what is set meanwhile,
    // and come back as they are when it is on again.
    private static void CullingSwitchesOff(EngineApiGame game)
    {
        try
        {
            game.Run("Engine.Rendering.Culling.Frustum = true; Engine.Rendering.Culling.Occlusion = true; Engine.Rendering.Culling.Enabled = false;");
            HeadlessHarness.Assert(!Genesis.Shared.Rendering.RenderAutoState.FrustumCulling && !Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling
                && !Genesis.Shared.Rendering.RenderAutoState.DistanceCulling && !Genesis.Shared.Rendering.RenderAutoState.SubPixelCulling,
                "With culling off nothing may be culled.");
            EngineApiRun meanwhile = game.Run("keptFrustum = Engine.Rendering.Culling.Frustum; keptOcclusion = Engine.Rendering.Culling.Occlusion; Engine.Rendering.Culling.Occlusion = false; occlusionAfterSet = Engine.Rendering.Culling.Occlusion;");
            HeadlessHarness.Assert(meanwhile.Number("keptFrustum") == 1 && meanwhile.Number("keptOcclusion") == 1 && meanwhile.Number("occlusionAfterSet") == 0,
                "Frustum and Occlusion must read as they are kept while culling is off, and take what is set.");
            HeadlessHarness.Assert(!Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling, "Setting Occlusion while culling is off must not turn it on.");
            game.Run("Engine.Rendering.Culling.Enabled = true;");
            HeadlessHarness.Assert(Genesis.Shared.Rendering.RenderAutoState.FrustumCulling && !Genesis.Shared.Rendering.RenderAutoState.OcclusionCulling
                && Genesis.Shared.Rendering.RenderAutoState.DistanceCulling && Genesis.Shared.Rendering.RenderAutoState.SubPixelCulling,
                "Switched on again, culling must be as it was kept: frustum on, occlusion as last set (off), distance and size on.");
        }
        finally
        {
            game.Run("Engine.Rendering.Culling.Enabled = true; Engine.Rendering.Culling.Frustum = true; Engine.Rendering.Culling.Occlusion = false;");
        }
    }

    // What a case does, in its name: the round trip alone where nothing is switched, with Enabled where it is.
    private static string CaseSuffix(string scope) =>
        EngineApiModel.SettableIn(scope).Count == 0 ? "ReadsWithoutError"
        : EngineApiModel.HasEnabled(scope) ? "RoundTripAndEnabled" : "RoundTrip";

    // ── The registry's own rules ─────────────────────────────────────────────────────────

    private static void RunRegistryCases(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Registry.EveryNameIsEngineAreaNameWithASignatureAndADescription", () =>
        {
            IReadOnlyList<EngineApiMember> members = EngineApiModel.Members;
            HeadlessHarness.Assert(members.Count > 1500, $"Only {members.Count} names in the engine API.");
            var problems = new List<string>();
            foreach (EngineApiMember member in members)
            {
                if (string.IsNullOrWhiteSpace(member.Signature)) problems.Add(member.Name + ": no signature");
                if (string.IsNullOrWhiteSpace(member.Description)) problems.Add(member.Name + ": no description");
                if (member.Value.Length == 0 || member.Area.Length == 0) problems.Add(member.Name + ": empty area or value");
            }
            IEnumerable<string> duplicates = members.GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key);
            problems.AddRange(duplicates.Select(name => name + ": listed twice"));
            HeadlessHarness.Assert(problems.Count == 0, $"{problems.Count} names break the standard: " + string.Join("; ", problems.Take(8)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Registry.EveryValueThatCanBeSetHasATypeAndIsInTheInspectorsList", () =>
        {
            var problems = new List<string>();
            int checkedCount = 0;
            foreach (EngineApiMember member in EngineApiModel.Members.Where(member => member.IsProperty && member.CanSet && !NotInspectorSetting(member.Name)))
            {
                checkedCount++;
                Type? type = PgslApi.ValueType(member.Name);
                if (type is null) { problems.Add(member.Name + ": can be set but has no value type"); continue; }
                if (!EngineSettings.TryFind(member.Name, out EngineSettings.Setting setting)) { problems.Add(member.Name + ": not offered as an engine setting"); continue; }
                if (setting.Type != type) problems.Add($"{member.Name}: setting type {setting.Type.Name}, value type {type.Name}");
                if (!EngineSettings.Available(setting.Scope).Any(listed => listed.Name == setting.Name)) problems.Add(member.Name + ": missing from its scope's list");
                try { _ = EngineSettings.Parse(EngineSettings.DefaultText(setting), setting.Type); }
                catch (Exception exception) { problems.Add($"{member.Name}: default text failed ({exception.Message})"); }
            }
            HeadlessHarness.Assert(checkedCount > 300, $"Only {checkedCount} values can be set.");
            HeadlessHarness.Assert(problems.Count == 0, $"{problems.Count} of {checkedCount} settable values are not Inspector-ready: " + string.Join("; ", problems.Take(8)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Registry.EveryAreaAndPartHasAnEnabledOrIsAKnownGap", () =>
        {
            var lacking = new List<string>();
            foreach (string scope in EngineApiModel.Areas.Concat(EngineApiModel.Parts))
            {
                if (EngineApiModel.SettableIn(scope).Count == 0 || EngineApiModel.HasEnabled(scope)) continue;
                lacking.Add(scope);
            }
            string[] unexpected = lacking.Where(scope => !ScopesWithoutEnabled.ContainsKey(scope) && !ScopesWithoutEnabledByDesign.ContainsKey(scope)).ToArray();
            string[] nowHaveOne = ScopesWithoutEnabled.Keys.Concat(ScopesWithoutEnabledByDesign.Keys).Where(scope => !lacking.Contains(scope)).OrderBy(scope => scope, StringComparer.Ordinal).ToArray();
            Console.WriteLine($"Engine API: {lacking.Count} of {EngineApiModel.Areas.Count + EngineApiModel.Parts.Count} areas and parts have values and no Enabled "
                + $"({lacking.Count(scope => ScopesWithoutEnabledByDesign.ContainsKey(scope))} by design, {lacking.Count(scope => ScopesWithoutEnabled.ContainsKey(scope))} left for later)"
                + (nowHaveOne.Length == 0 ? "." : $"; these now have one and can leave the lists: {string.Join(", ", nowHaveOne)}."));
            HeadlessHarness.Assert(unexpected.Length == 0,
                $"New area or part without an Enabled (the standard gives every one an Enabled): {string.Join(", ", unexpected)}.");
        });

    }

    private static string ParentOf(string scope)
    {
        int dot = scope.LastIndexOf('.');
        return dot < 0 ? scope : scope[..dot];
    }

    // ── The command lists ────────────────────────────────────────────────────────────────

    private static void RunCommandCases(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Commands.EveryPgslCommandHasANameASignatureAndADescription", () =>
        {
            IReadOnlyList<PgslCommandInfo> catalogue = PgslCommandAutoTester.Catalogue();
            HeadlessHarness.Assert(catalogue.Count > 1000, $"Only {catalogue.Count} PGSL commands.");
            var problems = new List<string>();
            var noArgumentList = new List<string>();
            foreach (PgslCommandInfo command in catalogue)
            {
                if (string.IsNullOrWhiteSpace(command.Name)) { problems.Add("(unnamed) " + command.Signature); continue; }
                if (string.IsNullOrWhiteSpace(command.Signature)) problems.Add(command.QualifiedName + ": no signature");
                else if (!command.Signature.Contains(command.Name, StringComparison.OrdinalIgnoreCase)) problems.Add($"{command.QualifiedName}: signature '{command.Signature}' does not name it");
                else if (!command.IsProperty && !command.Signature.Contains('(', StringComparison.Ordinal)) noArgumentList.Add(command.QualifiedName);
                if (string.IsNullOrWhiteSpace(command.Description)) problems.Add(command.QualifiedName + ": no description");
                if (string.IsNullOrWhiteSpace(command.Category)) problems.Add(command.QualifiedName + ": no category");
            }
            // A command written like a value (Engine.Sky.SetHaze) shows no argument list in the dialog: noted, not failed.
            if (noArgumentList.Count > 0)
                Console.WriteLine($"Engine API: {noArgumentList.Count} commands have a signature with no argument list: {string.Join(", ", noArgumentList)}.");
            // The ten the coverage pass found now list their arguments, and stay that way.
            string[] described = ["Engine.Rendering.AutoExposure", "Engine.Sky.SetAmbientTint", "Engine.Sky.SetAmbientGroundTint", "Engine.Sky.SetAtmospherePreset", "Engine.Sky.SetCloudCoverage",
                "Engine.Sky.SetHaze", "Engine.Sky.SetLatitude", "Engine.Sky.SetTimeOfDay", "Engine.Terrain.Water.ClearHoles", "Engine.Terrain.Water.PunchHole"];
            string[] stillBare = described.Where(name => noArgumentList.Contains(name, StringComparer.OrdinalIgnoreCase) || catalogue.All(command => !string.Equals(command.QualifiedName, name, StringComparison.OrdinalIgnoreCase))).ToArray();
            HeadlessHarness.Assert(stillBare.Length == 0, "These commands show no argument list in their signature (or are missing): " + string.Join(", ", stillBare));
            HeadlessHarness.Assert(problems.Count == 0, $"{problems.Count} PGSL commands are missing something the Commands dialog shows: " + string.Join("; ", problems.Take(8)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Commands.TheSweepOfPgslCommandsReportsEachOneWithItsSignature", () =>
        {
            PgslCommandTestReport report = PgslCommandAutoTester.Run(repeats: 1);
            HeadlessHarness.Assert(report.Total == PgslCommandAutoTester.Catalogue().Count, $"The sweep ran {report.Total} commands and the catalogue lists {PgslCommandAutoTester.Catalogue().Count}.");
            PgslCommandTestResult[] unsigned = report.Results.Where(result => string.IsNullOrWhiteSpace(result.Signature) || string.IsNullOrWhiteSpace(result.Name)).ToArray();
            HeadlessHarness.Assert(unsigned.Length == 0, $"{unsigned.Length} sweep results have no name or signature: " + string.Join(", ", unsigned.Take(6).Select(result => result.Name)));
            HeadlessHarness.Assert(report.Failed == 0 && report.Unsupported == 0 && report.NotImplemented == 0,
                $"The sweep found {report.Failed} that threw, {report.Unsupported} that could not be called and {report.NotImplemented} unfinished: "
                + string.Join(", ", report.Results.Where(result => result.Outcome != PgslCommandOutcome.Callable).Take(6).Select(result => result.Name)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Commands.EveryEngineCommandHasANameASignatureAndADescription", () =>
        {
            IReadOnlyList<EngineCommandInfo> catalogue = EngineCommandAutoTester.Catalogue();
            HeadlessHarness.Assert(catalogue.Count > 50, $"Only {catalogue.Count} Engine commands.");
            var problems = new List<string>();
            foreach (EngineCommandInfo command in catalogue)
            {
                if (string.IsNullOrWhiteSpace(command.Signature)) { problems.Add("(unnamed): no signature"); continue; }
                if (string.IsNullOrWhiteSpace(command.Name) || command.Name == command.Signature) problems.Add($"{command.Signature}: no name could be read from it");
                if (string.IsNullOrWhiteSpace(command.Description)) problems.Add(command.Signature + ": no description");
                if (string.IsNullOrWhiteSpace(command.Category)) problems.Add(command.Signature + ": no category");
            }
            HeadlessHarness.Assert(problems.Count == 0, $"{problems.Count} Engine commands are missing something the Commands dialog shows: " + string.Join("; ", problems.Take(8)));
        });

        HeadlessHarness.RunCase(ctx.Report, "Engine.Api.Commands.TheSweepOfEngineCommandsReportsEachOneWithItsSignature", () =>
        {
            EngineCommandTestReport report = EngineCommandAutoTester.Run(repeats: 1);
            HeadlessHarness.Assert(report.Total == EngineCommandAutoTester.Catalogue().Count, $"The Engine sweep ran {report.Total} commands and the catalogue lists {EngineCommandAutoTester.Catalogue().Count}.");
            EngineCommandTestResult[] unsigned = report.Results.Where(result => string.IsNullOrWhiteSpace(result.Signature) || string.IsNullOrWhiteSpace(result.Name)).ToArray();
            HeadlessHarness.Assert(unsigned.Length == 0, $"{unsigned.Length} Engine sweep results have no name or signature.");
            HeadlessHarness.Assert(report.Failed == 0 && report.Unsupported == 0,
                $"The Engine sweep found {report.Failed} that threw and {report.Unsupported} that could not be called: "
                + string.Join(", ", report.Results.Where(result => result.Outcome is EngineCommandOutcome.Threw or EngineCommandOutcome.Unsupported).Take(6).Select(result => result.Name + " (" + result.Detail + ")")));
        });
    }

    // ── One area or part ─────────────────────────────────────────────────────────────────

    private static void Sweep(EngineApiGame game, string scope)
    {
        var properties = new List<PropertyResult>();
        var failures = new List<string>();
        IReadOnlyList<EngineApiMember> readable = EngineApiModel.Members
            .Where(member => member.IsProperty && !member.CanSet && member.Scope == scope).ToList();
        foreach (EngineApiMember member in readable)
        {
            EngineApiRun read = game.Run($"v = {member.Name};");
            if (read.Failed) failures.Add($"{member.Name} (read only) threw: {read.Errors}");
        }
        foreach (EngineApiMember member in EngineApiModel.SettableIn(scope))
        {
            PropertyResult result = Probe(game, member);
            properties.Add(result);
            if (result.Detail.StartsWith("FAIL ", StringComparison.Ordinal)) failures.Add(member.Name + ": " + result.Detail[5..]);
            else if (result.Kind == Kind.Silent && !KnownSilent.ContainsKey(member.Name))
                failures.Add($"{member.Name} reads its default whatever is set ({result.Detail}); the room it is swept in should hold it, or it is listed in KnownSilent with the reason");
        }

        string enabled = "-";
        string effect = "-";
        if (EngineApiModel.HasEnabled(scope))
        {
            enabled = CheckEnabled(game, scope, failures);
            if (Effects.TryGetValue(scope, out Func<EngineApiGame, bool>? seen)) effect = CheckEffect(game, scope, seen, failures);
        }
        string failure = string.Join("; ", failures);
        Results.Add(new ScopeResult(scope, properties, enabled, effect, failure));
        HeadlessHarness.Assert(failures.Count == 0, failure);
    }

    private static PropertyResult Probe(EngineApiGame game, EngineApiMember member)
    {
        string name = member.Name;
        if (Skipped.TryGetValue(name, out string? why)) return new PropertyResult(name, Kind.Skipped, why);
        EngineApiRun read = game.Run($"d = {name};");
        if (read.Failed)
        {
            return read.Errors.Contains("can be set but not read", StringComparison.Ordinal)
                ? new PropertyResult(name, Kind.WriteOnly, "")
                : new PropertyResult(name, Kind.Silent, "FAIL reading its default threw: " + read.Errors);
        }
        Type? declared = PgslApi.ValueType(name);
        // Text that was never set reads as nothing (an instance with no sprite): empty text.
        if (!read.Variables.TryGetValue("d", out object? defaultValue) || defaultValue is null)
        {
            if (declared != typeof(string)) return new PropertyResult(name, Kind.Silent, "FAIL reading its default gave nothing");
            defaultValue = "";
        }
        if (declared is not null && (declared == typeof(string)) != (defaultValue is string))
        {
            string mismatch = $"reads as {defaultValue.GetType().Name} though it is set as {declared.Name}";
            return KnownDefects.ContainsKey(name)
                ? new PropertyResult(name, Kind.Defect, KnownDefects[name] + " (" + mismatch + ")")
                : new PropertyResult(name, Kind.Silent, "FAIL " + mismatch);
        }
        if (KnownDefects.TryGetValue(name, out string? defect)) Console.WriteLine($"Engine API: {name} ({defect}) now reads as it is set; it can leave the known defects.");

        EngineSettings.TryFind(name, out EngineSettings.Setting? setting);
        List<object> candidates = Candidates(defaultValue, setting);
        string restore = $"{name} = {EngineApiGame.Literal(defaultValue)};";
        if (candidates.Count == 0)
        {
            EngineApiRun same = game.Run(restore + $" r0 = {name};");
            return same.Failed
                ? new PropertyResult(name, Kind.Text, "FAIL setting it to what it read threw: " + same.Errors)
                : new PropertyResult(name, Kind.Text, "");
        }

        var script = new StringBuilder();
        for (int i = 0; i < candidates.Count; i++)
            script.Append($"{name} = {EngineApiGame.Literal(candidates[i])}; r{i} = {name};\n");
        script.Append(restore).Append('\n').Append($"after = {name};");
        EngineApiRun run = game.Run(script.ToString());
        if (run.Failed)
        {
            game.Run(restore);
            return new PropertyResult(name, Kind.Silent, "FAIL setting it threw: " + run.Errors);
        }

        int roundTrips = 0, kept = 0;
        object? roundTripped = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            run.Variables.TryGetValue($"r{i}", out object? back);
            if (Matches(candidates[i], back)) { roundTrips++; roundTripped ??= candidates[i]; }
            else if (!Matches(defaultValue, back)) kept++;
        }
        if (!run.Variables.TryGetValue("after", out object? restored) || !Matches(defaultValue, restored))
            return new PropertyResult(name, Kind.Silent, $"FAIL set back to its default ({EngineApiGame.Literal(defaultValue)}) it reads {EngineApiGame.Literal(restored)}");

        if (roundTrips == 0)
        {
            string tried = string.Join(", ", candidates.Select((candidate, i) =>
                $"{EngineApiGame.Literal(candidate)} reads {(run.Variables.TryGetValue($"r{i}", out object? back) ? EngineApiGame.Literal(back) : "nothing")}"));
            return new PropertyResult(name, kept > 0 ? Kind.Kept : Kind.Silent, $"default {EngineApiGame.Literal(defaultValue)}; set {tried}");
        }

        if (NotInspectorSetting(name)) return new PropertyResult(name, Kind.RoundTrip, "");

        // The Inspector and the debug screen set it as text, through the same bridge.
        string text = EngineSettings.Format(roundTripped);
        int applied = EngineSettings.Apply([new KeyValuePair<string, string>(name, text)], game.Inspector);
        bool read2 = EngineSettings.TryRead(name, game.Inspector, out object? inspectorBack);
        EngineSettings.Apply([new KeyValuePair<string, string>(name, EngineSettings.Format(defaultValue))], game.Inspector);
        if (applied != 1 || !read2 || !Matches(roundTripped, inspectorBack))
            return new PropertyResult(name, Kind.RoundTrip, $"FAIL the Inspector sets {text} and reads {EngineSettings.Format(inspectorBack)} (applied {applied}); a script reads it back as set");
        return new PropertyResult(name, Kind.RoundTrip, "");
    }

    // Values to try: inside the range the description gives, then beside the default.
    private static List<object> Candidates(object defaultValue, EngineSettings.Setting? setting)
    {
        var candidates = new List<object>();
        if (defaultValue is bool flag) { candidates.Add(!flag); return candidates; }
        if (defaultValue is string text)
        {
            if (setting?.Choices is { Count: > 0 } choices)
                foreach (string choice in choices.Where(choice => !string.Equals(choice, text, StringComparison.OrdinalIgnoreCase)).Take(3)) candidates.Add(choice);
            else if (setting is not null && setting.Name.Contains("Color", StringComparison.Ordinal)) candidates.Add("#336699");
            return candidates;
        }
        double current = EngineApiGame.AsNumber(defaultValue);
        var options = new List<double>();
        if (setting is not null && EngineSettings.TryRange(setting, out double low, out double high))
        {
            options.Add(Math.Round(low + (high - low) * 0.25, 3));
            options.Add(Math.Round(low + (high - low) * 0.75, 3));
        }
        options.AddRange([Math.Round(current * 0.5 + 0.25, 3), Math.Round(current + 1, 3), 0.5, 2, 30]);
        foreach (double option in options)
        {
            if (Math.Abs(option - current) < 1e-9 || candidates.Contains(option)) continue;
            candidates.Add(option);
            if (candidates.Count == 4) break;
        }
        return candidates;
    }

    private static bool Matches(object? wanted, object? actual)
    {
        if (wanted is string expectedText) return actual is string actualText && string.Equals(expectedText, actualText, StringComparison.OrdinalIgnoreCase);
        if (actual is string || wanted is null || actual is null) return false;
        double a = EngineApiGame.AsNumber(wanted), b = EngineApiGame.AsNumber(actual);
        return Math.Abs(a - b) <= 1e-4 * Math.Max(1, Math.Abs(a));
    }

    // Enabled reads what it was set; a part turned on turns its area (the part it belongs to) on.
    private static string CheckEnabled(EngineApiGame game, string scope, List<string> failures)
    {
        string name = $"Engine.{scope}.Enabled";
        string parent = ParentOf(scope);
        bool parentHasEnabled = scope.Contains('.', StringComparison.Ordinal) && EngineApiModel.HasEnabled(parent);
        var script = new StringBuilder();
        script.Append($"d = {name};\n{name} = false; off = {name};\n{name} = true; on = {name};\n{name} = d;\n");
        if (parentHasEnabled)
        {
            string up = $"Engine.{parent}.Enabled";
            script.Append($"da = {up};\n{up} = false; {name} = true; areaOn = {up};\n{name} = d; {up} = da;\n");
        }
        EngineApiRun run = game.Run(script.ToString());
        if (run.Failed)
        {
            failures.Add($"{name} threw: {run.Errors}");
            return "threw";
        }
        // A value a window or renderer holds may read the same either way here: noted in the page, not a failure.
        bool off = run.Number("off") == 0, on = run.Number("on") == 1;
        string result = off && on ? "toggles" : on ? "does not turn off here" : off ? "does not turn on here" : "reads the same either way";
        if (parentHasEnabled && on)
        {
            bool areaOn = run.Number("areaOn") == 1;
            result += areaOn ? ", turns " + parent + " on" : ", did not turn " + parent + " on";
            if (!areaOn) failures.Add($"{name} = true did not turn {parent} on");
        }
        return result;
    }

    private static string CheckEffect(EngineApiGame game, string scope, Func<EngineApiGame, bool> seen, List<string> failures)
    {
        string name = $"Engine.{scope}.Enabled";
        EngineApiRun original = game.Run($"d = {name};");
        try
        {
            game.Run($"{name} = false;");
            bool whenOff = seen(game);
            game.Run($"{name} = true;");
            bool whenOn = seen(game);
            if (whenOff || !whenOn) failures.Add($"{name}: the engine's own state reads {(whenOff ? "on" : "off")} after false and {(whenOn ? "on" : "off")} after true");
            return whenOff || !whenOn ? "wrong" : "seen in the engine's state";
        }
        finally
        {
            game.Run($"{name} = {EngineApiGame.Literal(original.Variables.GetValueOrDefault("d"))};");
        }
    }

    // ── The page and the totals ──────────────────────────────────────────────────────────

    private static void WriteReport(HeadlessContext ctx)
    {
        var page = new StringBuilder();
        page.AppendLine("# Engine API parts");
        page.AppendLine();
        int total = Results.Sum(scope => scope.Properties.Count);
        int Count(Kind kind) => Results.Sum(scope => scope.Properties.Count(property => property.Kind == kind));
        page.AppendLine($"{Results.Count} areas and parts, {total} values that can be set: {Count(Kind.RoundTrip)} read back as set (script and Inspector), {Count(Kind.Kept)} kept in range, "
            + $"{Count(Kind.Text)} text left as it was, {Count(Kind.Silent)} read their default whatever is set (nothing here observes them), {Count(Kind.WriteOnly)} write-only, "
            + $"{Count(Kind.Defect)} known engine defects, {Count(Kind.Skipped)} skipped.");
        page.AppendLine();
        page.AppendLine("| Area or part | Round trip | Kept | Text | Silent | Enabled | Effect seen |");
        page.AppendLine("|---|---:|---:|---:|---:|---|---|");
        foreach (ScopeResult scope in Results.OrderBy(result => result.Scope, StringComparer.Ordinal))
        {
            int Of(Kind kind) => scope.Properties.Count(property => property.Kind == kind);
            page.AppendLine($"| {scope.Scope} | {Of(Kind.RoundTrip)} | {Of(Kind.Kept)} | {Of(Kind.Text)} | {Of(Kind.Silent)} | {scope.Enabled} | {scope.Effect} |");
        }
        page.AppendLine();
        page.AppendLine("## Values that read their default whatever is set");
        page.AppendLine();
        foreach (PropertyResult silent in Results.SelectMany(scope => scope.Properties).Where(property => property.Kind == Kind.Silent).OrderBy(property => property.Name, StringComparer.Ordinal))
            page.AppendLine($"- {silent.Name}: {silent.Detail}" + (KnownSilent.TryGetValue(silent.Name, out string? reason) ? $" (known: {reason})" : ""));
        page.AppendLine();
        page.AppendLine("## Engine defects found by the sweep");
        page.AppendLine();
        foreach (PropertyResult defect in Results.SelectMany(scope => scope.Properties).Where(property => property.Kind == Kind.Defect))
            page.AppendLine($"- {defect.Name}: {defect.Detail}");
        page.AppendLine();
        page.AppendLine("## Skipped");
        page.AppendLine();
        foreach (PropertyResult skipped in Results.SelectMany(scope => scope.Properties).Where(property => property.Kind == Kind.Skipped))
            page.AppendLine($"- {skipped.Name}: {skipped.Detail}");
        string[] nowObservable = KnownSilent.Keys.Where(name => Results.SelectMany(scope => scope.Properties).Any(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Kind != Kind.Silent))
            .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (nowObservable.Length > 0)
        {
            page.AppendLine();
            page.AppendLine("## Now observable (can leave KnownSilent)");
            page.AppendLine();
            foreach (string name in nowObservable) page.AppendLine("- " + name);
            Console.WriteLine($"Engine API: {nowObservable.Length} values now read back what is set and can leave KnownSilent: {string.Join(", ", nowObservable)}.");
        }
        Directory.CreateDirectory(ctx.Captures);
        File.WriteAllText(Path.Combine(ctx.Captures, "engine-api-parts.md"), page.ToString());
        Console.WriteLine($"Engine API parts: {total} values; {Count(Kind.RoundTrip)} round trip, {Count(Kind.Kept)} kept, {Count(Kind.Text)} text, {Count(Kind.Silent)} silent, {Count(Kind.WriteOnly)} write-only, {Count(Kind.Defect)} known defects, {Count(Kind.Skipped)} skipped.");
        HeadlessHarness.Assert(total > 300, $"Only {total} values were swept.");
        HeadlessHarness.Assert(Count(Kind.RoundTrip) + Count(Kind.Kept) >= total * 0.5, $"Only {Count(Kind.RoundTrip)} of {total} values read back what a script set.");
    }

    // ── F6 ───────────────────────────────────────────────────────────────────────────────

    private static void DebugScreenValues(EngineApiGame game)
    {
        var instance = EngineSettings.InstanceValues(game.Scene.World, game.Probe, game.Inspector);
        var world = EngineSettings.WorldValuesNow();
        HeadlessHarness.Assert(instance.Count >= 10 && world.Count >= 6, $"The debug screen lists {instance.Count} instance values and {world.Count} world values.");
        var known = new HashSet<string>(EngineApiModel.Members.Where(member => member.IsProperty && member.CanSet).Select(member => member.Name), StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (setting, value) in instance.Concat(world))
        {
            if (!known.Contains(setting.Name)) problems.Add(setting.Name + ": listed on F6 but not a value that can be set");
            if (Skipped.ContainsKey(setting.Name)) continue;
            string? nudged = EngineSettings.NudgedText(setting, value, 1);
            if (nudged is null) continue;
            int applied = EngineSettings.Apply([new KeyValuePair<string, string>(setting.Name, nudged)], game.Inspector);
            EngineSettings.Apply([new KeyValuePair<string, string>(setting.Name, EngineSettings.Format(value))], game.Inspector);
            if (applied != 1) problems.Add($"{setting.Name}: nudging it to {nudged} applied {applied}");
        }
        HeadlessHarness.Assert(problems.Count == 0, string.Join("; ", problems.Take(8)));
        string[] names = instance.Select(item => item.Setting.Name).ToArray();
        HeadlessHarness.Assert(names.Contains("Engine.Physics.Mass") && names.Contains("Engine.Model.Glow") && names.Contains("Engine.Image.Frame") && names.Contains("Engine.Animation.Speed"),
            "The probe's body, model, animator and image are not all listed on F6: " + string.Join(", ", names));
    }
}
