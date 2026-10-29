using System.Drawing;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Genesis.Application.Core.Automation;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Projects;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Rendering.Core;
using Genesis.Rendering.Particles;
using Genesis.Runtime.Particles;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>The Particle editor's module stack: a view over the runtime settings, saved in the resource.</summary>
internal static partial class ParticleWorkbenchSuite
{
    public static void RunModules(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Particle module stack");
        RunModuleCases(context, (name, test) => HeadlessHarness.RunCase(context.Report, "Editor.ParticleWorkbench." + name, test));
    }

    private static void RunModuleCases(HeadlessContext context, Action<string, Action> check)
    {
        check("Modules.PointCurvesAreWhatTheRuntimeSamples", () =>
        {
            ParticleBezierCurve straight = new() { Keys = [Key(0, 0), Key(1, 1)] };
            Assert(Math.Abs(straight.Evaluate(0.5f) - 0.5f) < 1e-5 && Math.Abs(straight.Evaluate(0.25f) - 0.25f) < 1e-5,
                "Two straight points did not give a straight line.");
            ParticleBezierCurve eased = new() { Keys = [Key(0, 0, smooth: true), Key(1, 1)] };
            Assert(Math.Abs(eased.Evaluate(0.25f) - 0.15625f) < 1e-4, $"A smooth join is not an ease: {eased.Evaluate(0.25f)}.");
            ParticleBezierCurve hump = new() { Keys = [Key(0, 0, smooth: true), Key(0.5, 1, smooth: true), Key(1, 0)] };
            float peak = Enumerable.Range(0, 201).Max(i => hump.Evaluate(i / 200f));
            Assert(Math.Abs(hump.Evaluate(0.5f) - 1f) < 1e-5 && peak <= 1.0001f, $"A smooth curve missed or overshot its points (peak {peak}).");
            ParticleBezierCurve bezier = new() { X1 = 0.1, Y1 = 0.9, X2 = 0.2, Y2 = 1 };
            float before = bezier.Evaluate(0.3f);
            Assert(new ParticleBezierCurve { X1 = 0.1, Y1 = 0.9, X2 = 0.2, Y2 = 1, Keys = null }.Evaluate(0.3f) == before,
                "A curve without points no longer follows its Bezier.");

            ParticleConfig config = ParticlePresets.Fire();
            config.UseCustomSizeCurve = true;
            config.SizeOverLifetime = hump.Clone();
            GpuParticleDefinition gpu = GpuParticleDefinitionBuilder.Build(config, Matrix4x4.Identity);
            int samples = GpuParticleProtocol.CurveSamples;
            for (int i = 0; i < samples; i += Math.Max(1, samples / 8))
            {
                float expected = hump.Evaluate(i / (float)(samples - 1));
                Assert(Math.Abs(gpu.Lookup[i].X - expected) < 1e-4, $"The GPU curve table does not sample the points at {i}: {gpu.Lookup[i].X} vs {expected}.");
            }

            ParticleConfig read = JsonSerializer.Deserialize<ParticleConfig>(Text(config), Json)!;
            Assert(read.SizeOverLifetime.Keys is { Count: 3 } keys && keys[1].Smooth && keys[1].Value == 1 && keys[2].Time == 1,
                "Curve points did not survive the saved form.");
            string plain = Text(ParticlePresets.Fire());
            Assert(!plain.Contains("\"keys\"", StringComparison.Ordinal) && !plain.Contains("\"modules\"", StringComparison.Ordinal),
                "An effect without points or a module stack changed its saved form.");
        });

        check("Modules.OlderEffectsOpenWithTheModulesTheirSettingsUse", () =>
        {
            foreach (string name in ParticlePresets.Names)
            {
                ParticleConfig preset = ParticlePresets.Create(name);
                foreach (ParticleConfig emitter in new[] { preset }.Concat(preset.Emitters.Select(layer => layer.Config)))
                {
                    ParticleModuleContext stack = new(emitter, preset, false);
                    IReadOnlyList<ParticleModuleEntry> entries = ParticleModuleStack.Entries(stack);
                    foreach (ParticleModuleDefinition module in ParticleModuleCatalog.All.Where(module => module.Available(stack)))
                    {
                        bool shown = entries.Any(entry => entry.Definition == module);
                        Assert(shown == ParticleModuleStack.IsOn(module, emitter),
                            $"{name} / {emitter.EmitterName}: {module.Title} is {(shown ? "shown" : "hidden")} but its settings say otherwise.");
                    }
                    Assert(entries.All(entry => entry.Enabled), $"{name}: a stack never edited shows a switched-off module.");
                }
            }
            string[] fire = ParticleModuleStack.Entries(ParticleModuleContext.For(ParticlePresets.Fire())).Select(entry => entry.Definition.Id).ToArray();
            foreach (string id in new[] { "spawn.rate", "init.speed", "update.gravity", "update.drag", "update.turbulence", "update.colour", "update.size" })
                Assert(fire.Contains(id), "Fire opened without its " + id + " module.");
            foreach (string id in new[] { "spawn.burst", "update.collision", "render.trail", "init.rotation" })
                Assert(!fire.Contains(id), "Fire opened with a module its settings do not use: " + id);
            string[] explosion = ParticleModuleStack.Entries(ParticleModuleContext.For(ParticlePresets.Explosion())).Select(entry => entry.Definition.Id).ToArray();
            Assert(explosion.Contains("spawn.burst") && !explosion.Contains("spawn.rate"), "The one-shot Explosion did not open as a Burst.");
            WithEditor(editor =>
            {
                editor.ShowModuleStack();
                Assert(editor.ModuleStackVisible && !editor.IsDirty && !editor.CanUndo && editor.Config.Modules is null,
                    "Opening the module stack changed the document.");
                Assert(editor.ModuleStackCards.SequenceEqual(editor.Modules.Select(entry => entry.Definition.Id)),
                    "The stack's cards are not the emitter's modules in order.");
            });
        });

        check("Modules.EverySwitchChangesWhatTheRuntimePlays", () =>
        {
            int switched = 0;
            foreach (ParticleModuleDefinition module in ParticleModuleCatalog.All.Where(module => !module.Required))
            {
                ParticleConfig emitter = ParticlePresets.Fire();
                ParticleModuleContext stack = ParticleModuleContext.For(emitter);
                Assert(module.Available(stack), module.Title + " is not offered for a 3D sprite emitter.");
                ParticleModuleStack.Add(stack, module.Id);
                if (module.Id == "render.image") ParticleModuleStack.SetField(stack, module.Id, "image", "Soft Glow");
                if (module.Id == "render.mesh") ParticleModuleStack.SetField(stack, module.Id, "mesh", "Crate");
                Assert(ParticleModuleStack.IsEnabled(stack, module.Id), module.Title + " did not switch on when added.");
                string on = Text(ParticleModuleStack.RuntimeSettings(emitter));
                GpuParticleDefinition onGpu = GpuParticleDefinitionBuilder.Build(emitter, Matrix4x4.Identity);
                ParticleConfig onSettings = emitter.CloneEmitter();

                ParticleModuleStack.SetEnabled(stack, module.Id, false);
                Assert(!ParticleModuleStack.IsEnabled(stack, module.Id) && ParticleModuleStack.Contains(stack, module.Id),
                    module.Title + " did not stay in the stack, switched off.");
                Assert(Text(ParticleModuleStack.RuntimeSettings(emitter)) != on, "Switching " + module.Title + " off changed none of the runtime settings.");
                GpuParticleDefinition offGpu = GpuParticleDefinitionBuilder.Build(emitter, Matrix4x4.Identity);
                switch (module.Id)
                {
                    // Spawning and the picture are read by the emitter's host, not the GPU definition.
                    case "spawn.rate": Assert(onSettings.Loop && !emitter.Loop, "Spawn rate off still spawns continuously."); break;
                    case "spawn.burst": Assert(onSettings.BurstCount > 0 && emitter.BurstCount == 0, "Burst off still bursts."); break;
                    case "render.image": Assert(onSettings.TexturePath.Length > 0 && emitter.TexturePath.Length == 0, "Image off still draws the picture."); break;
                    default: Assert(!SameDefinition(onGpu, offGpu), "Switching " + module.Title + " off did not change what the GPU draws."); break;
                }

                ParticleModuleStack.SetEnabled(stack, module.Id, true);
                Assert(Text(ParticleModuleStack.RuntimeSettings(emitter)) == on, "Switching " + module.Title + " back on did not give its settings back.");
                switched++;
            }
            Assert(switched >= 18, "Too few switchable modules were checked: " + switched);
        });

        check("Modules.EveryFieldWritesTheRuntimeSettings", () =>
        {
            int written = 0;
            ParticleEmitShape[] shapes = [ParticleEmitShape.Disc, ParticleEmitShape.Box, ParticleEmitShape.Ring, ParticleEmitShape.MeshSurface, ParticleEmitShape.Cone];
            foreach (ParticleModuleDefinition module in ParticleModuleCatalog.All)
            foreach (ParticleModuleField field in module.Fields.Where(field => !field.PreviewOnly))
            {
                ParticleConfig? emitter = null;
                ParticleModuleContext stack = default;
                foreach (ParticleEmitShape shape in shapes)
                {
                    ParticleConfig candidate = ParticlePresets.Fire();
                    candidate.Shape = shape;
                    ParticleModuleContext candidateStack = ParticleModuleContext.For(candidate);
                    if (!module.Required) ParticleModuleStack.Add(candidateStack, module.Id);
                    if (!field.Visible(candidateStack)) continue;
                    emitter = candidate; stack = candidateStack;
                    break;
                }
                Assert(emitter is not null, $"{module.Title} / {field.Label} is never shown.");
                string before = Text(ParticleModuleStack.RuntimeSettings(emitter!));
                object? current = field.Read(stack);
                if (field.Kind == ParticleModuleFieldKind.Curve && field.Choices.Count == 1)
                {
                    List<ParticleCurveKey> keys = ParticleModuleCurves.EnsureKeys(emitter!, field.Channel);
                    keys[0].Value = keys[0].Value > 0.5 ? 0.25 : 0.75;
                }
                else ParticleModuleStack.SetField(stack, module.Id, field.Key, OtherValue(field, current, emitter!));
                Assert(Text(ParticleModuleStack.RuntimeSettings(emitter!)) != before, $"{module.Title} / {field.Label} changed none of the runtime settings.");
                written++;
            }
            Assert(written >= 60, "Too few module fields were checked: " + written);
        });

        check("Modules.RateColourAndCurveSurviveSaveReopenAndPlayTheSame", () => WithEditor(editor =>
        {
            editor.ShowModuleStack();
            editor.SetModuleField("spawn.rate", "rate", 123d);
            List<ParticleGradientStop> stops =
            [
                new() { Position = 0, Color = new ParticleColor(1f, 1f, 0.6f, 1f) },
                new() { Position = 0.4, Color = new ParticleColor(0.2f, 0.5f, 1f, 0.8f) },
                new() { Position = 1, Color = new ParticleColor(0.1f, 0f, 0.4f, 0f) },
            ];
            editor.SetModuleField("update.colour", "gradient", stops);
            editor.SetModuleCurveKeys("update.size", "curve", [Key(0, 0, smooth: true), Key(0.35, 1, smooth: true), Key(1, 0.5)]);
            editor.SetModuleEnabled("update.turbulence", false);
            editor.AddModule("update.wind");
            Assert(editor.IsDirty && editor.CanUndo, "Module edits were not journalled.");
            string authored = Text(editor.Config);
            editor.Save();

            using (ParticleEditorControl reopened = new(editor.ResourcePath, editor.ProjectRoot))
            {
                Assert(Text(reopened.Config) == authored && !reopened.IsDirty, "Save and reopen changed the module stack or its settings.");
                Assert(reopened.Modules.Select(entry => entry.Definition.Id + entry.Enabled).SequenceEqual(editor.Modules.Select(entry => entry.Definition.Id + entry.Enabled)),
                    "The reopened stack lists other modules.");
                ParticleConfig saved = reopened.Config;
                Assert(saved.EmitRate == 123 && saved.GradientStops.Count == 3 && Math.Abs(saved.GradientStops[1].Color.B - 1f) < 1e-6
                    && saved.UseCustomSizeCurve && saved.SizeOverLifetime.Keys is { Count: 3 } && saved.WindX == 1,
                    "Rate, colour over life, the size curve or the added module did not survive.");
                Assert(!reopened.Modules.Single(entry => entry.Definition.Id == "update.turbulence").Enabled && saved.TurbulenceStrength == 0,
                    "The switched-off module did not stay off.");
                reopened.SetModuleEnabled("update.turbulence", true);
                Assert(reopened.Config.TurbulenceStrength == ParticlePresets.Fire().TurbulenceStrength,
                    "Switching the saved, switched-off module on lost its value.");
            }

            // A game reads the file with the runtime loader: it must build the effect the editor previews.
            ResourceCatalog.Invalidate(editor.ProjectRoot);
            ParticleAssetLoader.ClearCache();
            ParticleConfig game = ParticleAssetLoader.Load(editor.ProjectRoot, "Campfire");
            IReadOnlyList<(string Id, string Name, ParticleConfig Config)> playing = ParticleAssetLoader.EnumerateEnabledEmitters(game);
            ParticleConfig previewed = editor.Config.CloneEmitter();
            Assert(playing.Count == 1, "The game plays another number of emitters.");
            Assert(SameDefinition(GpuParticleDefinitionBuilder.Build(playing[0].Config, Matrix4x4.Identity),
                    GpuParticleDefinitionBuilder.Build(previewed, Matrix4x4.Identity)),
                "The game builds another GPU effect from the saved stack than the editor previews.");
            Assert(Play(playing[0].Config).SequenceEqual(Play(previewed)), "The game's simulation of the saved stack differs from the editor's.");
        }));

        check("Modules.AutomationListsAndEditsTheSameStack", () =>
        {
            string root = Path.Combine(context.Workspace, "particle-modules-automation");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            ProjectSession project = new ProjectService().CreateProject(root, "Modules", "Blank");
            AutomationContext automation = AutomationContext.For(project);
            automation.Caller = "test";
            AutomationRegistry registry = AutomationRegistry.CreateDefault();
            AutomationResult Call(string name, string json) => registry.Invoke(automation, name, JsonNode.Parse(json) as JsonObject);
            AutomationResult made = Call("particle.create", """{ "name": "Camp Fire", "preset": "Fire" }""");
            Assert(made.Ok, "particle.create failed: " + made.Message);
            string path = made.Data?["path"]?.GetValue<string>() ?? string.Empty;
            AutomationResult list = Call("particle.module", $$"""{ "path": "{{path}}" }""");
            string[] listed = (list.Data?["modules"] as JsonArray)?.Select(module => module?["id"]?.GetValue<string>() ?? "").ToArray() ?? [];
            Assert(list.Ok && listed.SequenceEqual(ParticleModuleStack.Entries(ParticleModuleContext.For(ParticlePresets.Fire())).Select(entry => entry.Definition.Id)),
                "particle.module list does not list the stack the editor shows: " + list.Message);
            string Op(string action, string module, string? field = null, string? value = null)
            {
                JsonObject input = new() { ["path"] = path, ["action"] = action, ["module"] = module };
                if (field is not null) input["field"] = field;
                if (value is not null) input["value"] = value;
                return input.ToJsonString();
            }
            foreach (string call in new[]
            {
                Op("set", "spawn.rate", "rate", "75"),
                Op("set", "update.colour", "gradient",
                    """[{"position":0,"color":{"r":1,"g":1,"b":1,"a":1}},{"position":1,"color":{"r":0,"g":0.3,"b":1,"a":0}}]"""),
                Op("set", "update.size", "curve", """[{"time":0,"value":0,"smooth":true},{"time":0.5,"value":1},{"time":1,"value":0.4}]"""),
                Op("off", "update.gravity"),
                Op("add", "render.trail"),
            })
            {
                AutomationResult result = Call("particle.module", call);
                Assert(result.Ok, "particle.module refused " + call + ": " + result.Message);
            }
            ParticleConfig saved = JsonSerializer.Deserialize<ParticleConfig>(File.ReadAllText(automation.Resolve(path)), Json)!;
            Assert(saved.EmitRate == 75 && saved.GradientStops.Count == 2 && Math.Abs(saved.GradientStops[1].Color.B - 1f) < 1e-6
                && saved.SizeOverLifetime.Keys is { Count: 3 } && saved.UseCustomSizeCurve && saved.Gravity == 0
                && saved.RendererKind == ParticleRendererKind.Trail && saved.Modules is { Count: > 0 },
                "particle.module did not write the settings the editor would.");
            Assert(!Call("particle.module", Op("set", "update.gravity", "up", "3")).Ok, "A switched-off module took a field.");
            Assert(!Call("particle.module", Op("remove", "init.lifetime")).Ok, "An always-on module was removed.");
        });

        check("Modules.StackViewEditsLiveWithUndo", () => WithPreview(RenderBackendOption.Software, editor =>
        {
            Form host = editor.FindForm()!;
            host.ClientSize = new Size(1280, 900);
            GateSuite.Pump(4, 20);
            EditorCommandBar commands = editor.Controls.OfType<EditorCommandBar>().Single();
            commands.Items.OfType<ToolStripButton>().Single(button => button.Text == "Modules").PerformClick();
            GateSuite.Pump(6, 20);
            Assert(editor.ModuleStackVisible && !editor.IsDirty, "The Modules command did not open the stack, or dirtied the effect.");
            Assert(!Field<FlowLayoutPanel>(editor, "_particleQuickSetup").Visible, "Quick setup stayed over the module stack.");
            Assert(editor.ModuleStackCards.SequenceEqual(editor.Modules.Select(entry => entry.Definition.Id)), "The stack does not show the emitter's modules in order.");

            double gravity = editor.Config.Gravity;
            CheckBox gravitySwitch = Descendants(Card(editor, "update.gravity")).OfType<CheckBox>().Single(box => box.Name == "ParticleModuleEnabled");
            gravitySwitch.Checked = false;
            GateSuite.Pump(6, 20);
            Assert(editor.Config.Gravity == 0 && editor.IsDirty, "The Gravity switch did not switch gravity off.");
            Assert(Descendants(Card(editor, "update.gravity")).OfType<NumericUpDown>().All(field => !field.Enabled),
                "A switched-off module's fields can still be edited.");
            editor.Undo();
            GateSuite.Pump(6, 20);
            Assert(editor.Config.Gravity == gravity && !editor.IsDirty
                && editor.Modules.Single(entry => entry.Definition.Id == "update.gravity").Enabled,
                "Undo did not switch Gravity back on.");

            NumericUpDown rate = Descendants(Card(editor, "spawn.rate")).OfType<NumericUpDown>().Single();
            rate.Value = 321;
            GateSuite.Pump(2, 20);
            Assert(editor.Config.EmitRate == 321 && editor.IsDirty, "The rate field in the stack did not edit the effect.");
            editor.AddModule("update.collision");
            GateSuite.Pump(6, 20);
            Assert(editor.ModuleStackCards.Contains("update.collision") && editor.Config.CollisionMode == ParticleCollisionMode.Bounce,
                "An added module did not appear, or did nothing.");
            Assert(Descendants(Field<FlowLayoutPanel>(editor, "_moduleStackList")).OfType<Button>()
                .Count(button => button.Name == "ParticleAddModule") == 4, "Each group should offer modules to add.");
            editor.Restart();
            editor.StepForTest(0.5f);
            Assert(editor.LiveParticleCount > 0, "The preview stopped playing while the stack was edited.");
            using (editor.Viewport.CaptureFrame(2)) { }
            string image = "particle-module-stack.png";
            context.Report.Images.Add(ImageResult.From("Particle — module stack", image,
                VisualCapture.CaptureOpenForm(host, Path.Combine(context.Captures, image))));
        }));
    }

    private static ParticleCurveKey Key(double time, double value, bool smooth = false) => new() { Time = time, Value = value, Smooth = smooth };

    private static bool SameDefinition(GpuParticleDefinition left, GpuParticleDefinition right) =>
        left.Parameters.Equals(right.Parameters) && left.Lookup.SequenceEqual(right.Lookup)
        && left.BlendMode == right.BlendMode && left.PointSampling == right.PointSampling && left.Capacity == right.Capacity;

    private static SpriteDrawCall[] Play(ParticleConfig config)
    {
        ParticleSimulation simulation = new();
        simulation.LoadConfig(config);
        simulation.Reset(1337);
        for (int frame = 0; frame < 60; frame++) simulation.Step(1f / 60);
        SpriteDrawCall[] calls = new SpriteDrawCall[simulation.Capacity];
        int count = simulation.FillSpriteDrawCalls2D(calls, 0, 0, 12, default);
        return calls.Take(count).ToArray();
    }

    /// <summary>A valid value for the field that differs from what it holds now.</summary>
    private static object? OtherValue(ParticleModuleField field, object? current, ParticleConfig emitter)
    {
        switch (field.Kind)
        {
            case ParticleModuleFieldKind.Number:
            {
                double value = Convert.ToDouble(current);
                double step = Math.Max(field.Decimals == 0 ? 1 : 0.1, (field.Maximum - field.Minimum) * 0.01);
                double next = value + step <= field.Maximum ? value + step : value - step;
                return Math.Round(Math.Clamp(next, field.Minimum, field.Maximum), Math.Max(0, field.Decimals));
            }
            case ParticleModuleFieldKind.Toggle: return !(bool)current!;
            case ParticleModuleFieldKind.Choice: return field.Choices.First(choice => choice != (string?)current);
            case ParticleModuleFieldKind.Colour:
                return ((ParticleColor)current!).R > 0.5f ? new ParticleColor(0.2f, 0.4f, 0.6f, 1f) : new ParticleColor(0.9f, 0.4f, 0.2f, 1f);
            case ParticleModuleFieldKind.Asset: return string.IsNullOrEmpty((string?)current) ? "Soft Glow" : string.Empty;
            case ParticleModuleFieldKind.Range:
            {
                ParticleValueRange range = (ParticleValueRange)current!;
                return range.IsConstant
                    ? new ParticleValueRange(range.Minimum, range.Minimum * 1.5 + 0.1)
                    : new ParticleValueRange(range.Minimum + 0.1, range.Maximum + 0.3);
            }
            case ParticleModuleFieldKind.Curve:
                return ParticleModuleCurves.ModesFor(emitter, field.Channel).First(mode => mode != (string?)current);
            default:
            {
                List<ParticleGradientStop> stops = ((List<ParticleGradientStop>)current!).Select(stop => stop.Clone()).ToList();
                if (stops.Count < 2)
                {
                    stops = [new() { Position = 0, Color = emitter.StartColor.Clone() }, new() { Position = 1, Color = emitter.EndColor.Clone() }];
                }
                stops.Add(new ParticleGradientStop { Position = 0.5, Color = new ParticleColor(0.1f, 0.9f, 0.3f, 0.5f) });
                return stops;
            }
        }
    }

    private static Control Card(ParticleEditorControl editor, string id) =>
        Field<FlowLayoutPanel>(editor, "_moduleStackList").Controls.Cast<Control>()
            .Single(control => control.Name == "ParticleModuleCard" && (string?)control.Tag == id);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
}
