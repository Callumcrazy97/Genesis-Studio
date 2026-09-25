using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Genesis.Application.Core.Editing.Particles;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Runtime.Particles;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>Production document, simulator and WinForms control tests. No generated config replicas.</summary>
internal static class ParticleWorkbenchSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() } };

    public static void Run(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Particle Editor H21 Workbench");
        void Check(string name, Action test) => HeadlessHarness.RunCase(context.Report, "Editor.ParticleWorkbench." + name, test);
        ParticleClockCoreCases.Run(Check);
        Check("Stack.AddClonesWithoutChangingOriginal", () =>
        {
            ParticleConfig original = Envelope(); string before = Text(original);
            ParticleConfig source = ParticlePresets.Smoke(); string sourceBefore = Text(source);
            ParticleConfig next = ParticleEffectEditing.Add(original, source, "Smoke", out int selected);
            Assert(selected == 1 && ParticleEffectEditing.Count(next) == 2, "Add did not append one emitter.");
            Assert(Text(original) == before && Text(source) == sourceBefore, "Add modified an input document.");
            next.Emitters[0].Config.StartColor.R = .12f;
            Assert(Text(source) == sourceBefore, "Added emitter shares mutable colour data.");
        });
        Check("Stack.DuplicateHasFreshIdentityAndUniqueLocalName", () =>
        {
            ParticleConfig effect = Envelope(); ParticleEffectEditing.Rename(effect, 0, "Flame");
            effect = ParticleEffectEditing.Duplicate(effect, 0, out int selected);
            Assert(selected == 1 && effect.Emitters[0].Name == "Flame 2" && effect.Emitters[0].Id != effect.EmitterId,
                "Duplicate reused its sibling identity/name.");
            Assert(effect.Emitters[0].Config.EmitterId == effect.Emitters[0].Id, "Wrapper and config disagree on emitter identity.");
        });
        Check("Stack.RenameIsCaseInsensitiveAndValidated", () =>
        {
            ParticleConfig effect = ParticleEffectEditing.Add(Envelope(), ParticlePresets.Smoke(), "Smoke", out _);
            ParticleEffectEditing.Rename(effect, 0, "Flame");
            Throws<ArgumentException>(() => ParticleEffectEditing.Rename(effect, 1, "flame"));
            Throws<ArgumentException>(() => ParticleEffectEditing.Rename(effect, 1, "\n"));
            Throws<ArgumentException>(() => ParticleEffectEditing.Rename(effect, 1, new string('x', 65)));
            ParticleEffectEditing.Rename(effect, 1, "  Smoke Tail  ");
            Assert(effect.Emitters[0].Name == "Smoke Tail" && effect.Emitters[0].Config.EmitterName == "Smoke Tail", "Rename lost wrapper consistency.");
        });
        Check("Stack.MovePrimaryPreservesEnvelopeAndEveryId", () =>
        {
            ParticleConfig effect = ParticleEffectEditing.Add(Envelope(), ParticlePresets.Smoke(), "Smoke", out _);
            string first = effect.EmitterId, second = effect.Emitters[0].Id;
            ParticleConfig moved = ParticleEffectEditing.Move(effect, 0, 1);
            Assert(moved.EmitterId == second && moved.Emitters[0].Id == first, "Move regenerated identities.");
            AssertEnvelope(moved, effect);
            Assert(!ReferenceEquals(moved.Light, effect.Light), "Moved document shares its light settings with undo state.");
        });
        Check("Stack.RemovePrimaryKeepsEffectLightAndPreview", () =>
        {
            ParticleConfig effect = ParticleEffectEditing.Add(Envelope(), ParticlePresets.Smoke(), "Smoke", out _);
            ParticleConfig next = ParticleEffectEditing.Remove(effect, 0, out int selected);
            Assert(selected == 0 && next.EmitterName == "Smoke" && next.Emitters.Count == 0, "Primary promotion failed.");
            AssertEnvelope(next, effect);
        });
        Check("Stack.LastEmitterCannotBeRemoved", () =>
        { ParticleConfig effect = Envelope(); Throws<InvalidOperationException>(() => ParticleEffectEditing.Remove(effect, 0, out _)); });
        Check("Stack.DisabledStateSurvivesPromotionAndDuplicate", () =>
        {
            ParticleConfig effect = Envelope(); ParticleEffectEditing.SetEnabled(effect, 0, false);
            effect = ParticleEffectEditing.Duplicate(effect, 0, out _);
            Assert(!effect.Emitters[0].Enabled && !effect.Emitters[0].Config.EmitterEnabled, "Duplicate re-enabled the emitter.");
            effect = ParticleEffectEditing.Move(effect, 0, 1); Assert(!effect.EmitterEnabled, "Promotion re-enabled the emitter.");
        });
        Check("Stack.ReplaceKeepsEnvelopeIdentityAndNeighbours", () =>
        {
            ParticleConfig effect = ParticleEffectEditing.Add(Envelope(), ParticlePresets.Smoke(), "Smoke", out _);
            ParticleConfig next = ParticleEffectEditing.Replace(effect, 0, ParticlePresets.Magic());
            Assert(next.EmitterId == effect.EmitterId && next.Emitters[0].Id == effect.Emitters[0].Id, "Replacement erased identity/neighbours.");
            AssertEnvelope(next, effect);
        });
        Check("Stack.CapacityIsBounded", () =>
        {
            ParticleConfig effect = Envelope();
            for (int i = 1; i < ParticleEffectEditing.MaximumEmitters; i++)
                effect = ParticleEffectEditing.Add(effect, new ParticleConfig(), "Emitter", out _);
            Throws<InvalidOperationException>(() => ParticleEffectEditing.Add(effect, new ParticleConfig(), "Extra", out _));
            Throws<InvalidOperationException>(() => ParticleEffectEditing.Duplicate(effect, 0, out _));
            Assert(ParticleEffectEditing.Count(effect) == 64, "Emitter cap mutated the stack.");
        });
        Check("Stack.InvalidIndicesAreRejected", () =>
        {
            ParticleConfig effect = Envelope();
            Throws<ArgumentOutOfRangeException>(() => ParticleEffectEditing.Move(effect, 0, 9));
            Throws<ArgumentOutOfRangeException>(() => ParticleEffectEditing.Emitter(effect, -1));
        });
        Check("Definition.RoundTripPreservesPrecisionAndEscaping", () =>
        {
            ParticleConfig original = Envelope(); original.EmitterName = "Warm \"Flame\"";
            original.Speed = 1.12345678912345; original.TexturePath = "Soft Ember";
            original.StartColor = new ParticleColor(.12345678f, .9876543f, .33333334f, .5678912f);
            original.MidColor = null;
            ParticleConfig parsed = ParticleCodeCodec.Parse(ParticleCodeCodec.Serialize(original), original);
            Assert(Text(parsed) == Text(original.CloneEmitter()), "Editing text quantized unrelated colour/numeric values or escaped names.");
        });
        Check("Definition.AllShippedPresetsRoundTrip", () =>
        {
            foreach (string name in ParticlePresets.Names)
            {
                ParticleConfig preset = ParticlePresets.Create(name);
                ParticleConfig parsed = ParticleCodeCodec.Parse(ParticleCodeCodec.Serialize(preset), preset);
                Assert(Text(parsed) == Text(preset.CloneEmitter()), "Definition round-trip changed " + name);
            }
        });
        Check("Definition.UnknownKeysHaveLineDiagnostics", () =>
        {
            FormatException error = Throws<FormatException>(() => ParticleCodeCodec.Parse("particle \"Flame\" {\n unknown.value: 5\n}", Envelope()));
            Assert(error.Message.Contains("Line 2", StringComparison.Ordinal) && error.Message.Contains("unknown.value", StringComparison.Ordinal), "Unhelpful text diagnostic.");
        });
        Check("Definition.MalformedAndDuplicatePropertiesAreRejected", () =>
        {
            foreach (string text in new[] { "particle \"Flame\" {", "emission.rate: 2", "particle \"Flame\" {\nnot a property\n}",
                "particle \"Flame\" {\nemission.rate: 2\nemission.rate: 3\n}" })
                Throws<FormatException>(() => ParticleCodeCodec.Parse(text, Envelope()));
        });
        Check("Definition.NonFiniteAndUnknownEnumsAreRejected", () =>
        {
            foreach (string property in new[] { "emission.rate: NaN", "motion.speed: Infinity", "emission.shape: 500", "render.blend: mystery" })
                Throws<FormatException>(() => ParticleCodeCodec.Parse("particle \"Flame\" {\n" + property + "\n}", Envelope()));
        });
        Check("Definition.MalformedRgbaAndExtraContentAreRejected", () =>
        {
            Throws<FormatException>(() => ParticleCodeCodec.Parse("particle \"Flame\" {\nappearance.colourStart: rgba(2, 0, 0, 1)\n}", Envelope()));
            Throws<FormatException>(() => ParticleCodeCodec.Parse("particle \"Flame\" {\n}\nextra", Envelope()));
        });
        Check("Runtime.SeededRestartRepeatsRealParticlePositions", () =>
        {
            ParticleConfig config = ParticlePresets.Fire(); config.MaxParticles = 256; config.TurbulenceStrength = .5;
            ParticleSimulation simulation = new(); simulation.LoadConfig(config);
            SpriteDrawCall[] Run(int seed)
            {
                simulation.Reset(seed); for (int frame = 0; frame < 60; frame++) simulation.Step(1f / 60);
                SpriteDrawCall[] calls = new SpriteDrawCall[simulation.Capacity];
                int count = simulation.FillSpriteDrawCalls2D(calls, 0, 0, 12, default);
                return calls.Take(count).ToArray();
            }
            SpriteDrawCall[] first = Run(1337), second = Run(1337), third = Run(2021);
            Assert(first.Length > 0 && first.SequenceEqual(second), "Same seed produced different real simulator output.");
            Assert(!first.SequenceEqual(third), "Seed did not affect particle positions.");
        });
        Check("Runtime.LiveConfigUpdateDoesNotClearParticles", () =>
        {
            ParticleConfig config = ParticlePresets.Fire(); ParticleSimulation simulation = new(); simulation.LoadConfig(config);
            simulation.Step(.1f); int before = simulation.ActiveCount; config.EmitRate = 0; simulation.UpdateConfig(config);
            Assert(before > 0 && simulation.ActiveCount == before, "Live update erased the simulation.");
        });
        Check("Runtime.ExecutionPolicyRoutesEveryHardwareBackendToGpu", () =>
        {
            foreach (string backend in new[] { "Direct3D 11", "Direct3D 12", "Vulkan", "OpenGL" })
            {
                ParticleExecutionDecision decision = ParticleExecutionPolicy.Resolve(backend, true, true);
                Assert(decision.Target == ParticleExecutionTarget.Gpu && decision.UsesGpu && !decision.UsesCpu,
                    backend + " was not routed to the GPU particle target.");
            }
        });
        Check("Runtime.ExecutionPolicyUsesCpuOnlyForExplicitSoftware", () =>
        {
            ParticleExecutionDecision software = ParticleExecutionPolicy.Resolve("Software", false, false);
            Assert(software.Target == ParticleExecutionTarget.CpuSoftware && software.UsesCpu && !software.UsesGpu,
                "Software did not select the CPU particle target.");

            ParticleExecutionDecision missingCompute = ParticleExecutionPolicy.Resolve("Direct3D 11", false, true);
            ParticleExecutionDecision missingIndirect = ParticleExecutionPolicy.Resolve("Vulkan", true, false);
            Assert(missingCompute.Target == ParticleExecutionTarget.UnsupportedHardware
                && missingIndirect.Target == ParticleExecutionTarget.UnsupportedHardware,
                "A hardware backend silently acquired the CPU fallback.");
        });
        Check("Editor.WorkbenchRetainsSharedCommandsAndRealSections", () => WithEditor(editor =>
        {
            EditorCommandBar bar = editor.Controls.OfType<EditorCommandBar>().Single();
            Assert(bar.IsDocumentBound && bar.IsSavePinned && bar.SaveCommand is not null, "Workspace discarded shared Save/history chrome.");
            foreach (string caption in new[] { "Pause", "Stop", "Restart", "Step", "Burst", "Advanced", "Panels" })
                Assert(bar.Items.Cast<ToolStripItem>().Any(item => item.Text == caption), "Missing transport/workspace command " + caption);
            Assert(editor.InspectorSections.SequenceEqual(new[] { "Emission", "Forces", "Material", "Curves", "Collision", "Renderer", "Effect Light" }),
                "The inspector contract disagrees with the actual property sections.");
        }));
        Check("Editor.CleanOpenAndPreviewControlsDoNotDirtyDocument", () => WithEditor(editor =>
        {
            Assert(!editor.IsDirty && !editor.CanUndo, "Opening dirtied the resource.");
            editor.SetPreviewSeed(12); editor.SetPreviewSpeed(.5); editor.StopPreview(); editor.StepPreviewFrame(); editor.Restart();
            Assert(!editor.IsDirty && !editor.CanUndo, "Transport polluted document history.");
        }));
        Check("Editor.ScalarUpdatesReuseLiveSimulationAndRows", () => WithEditor(editor =>
        {
            editor.StepForTest(.2f); int live = editor.LiveParticleCount;
            ParticleSimulation simulation = Field<List<ParticleSimulation>>(editor, "_previewSimulations")[0];
            ListViewItem row = Field<ListView>(editor, "_emitterList").Items[0];
            Assert(editor.TryApplyInspectorValue("emitRate", 44), "Rate property was not handled.");
            Assert(ReferenceEquals(simulation, Field<List<ParticleSimulation>>(editor, "_previewSimulations")[0])
                && editor.LiveParticleCount == live && live > 0, "Scalar editing reset in-flight particles.");
            Assert(ReferenceEquals(row, Field<ListView>(editor, "_emitterList").Items[0]), "Scalar editing rebuilt emitter rows.");
        }));
        Check("Editor.UndoRedoAndSavedCheckpoint", () => WithEditor(editor =>
        {
            double original = editor.Config.EmitRate;
            editor.TryApplyInspectorValue("emitRate", 44); Assert(editor.IsDirty && editor.CanUndo, "Edit was not journalled.");
            editor.Undo(); Assert(editor.Config.EmitRate == original && !editor.IsDirty && editor.CanRedo, "Undo did not return to clean checkpoint.");
            editor.Redo(); Assert(editor.Config.EmitRate == 44 && editor.IsDirty, "Redo failed.");
            editor.Save(); Assert(!editor.IsDirty, "Save did not establish checkpoint.");
            editor.Undo(); Assert(editor.IsDirty, "Undoing a saved edit should be dirty.");
            editor.Redo(); Assert(!editor.IsDirty, "Redo to saved content should be clean.");
        }));
        Check("Editor.NoOpAndPreviewDimensionHistory", () => WithEditor(editor =>
        {
            editor.TryApplyInspectorValue("emitRate", editor.Config.EmitRate);
            Assert(!editor.CanUndo && !editor.IsDirty, "No-op added a journal entry.");
            bool original = editor.Config.Preview2D; editor.SetPreview2D(!original); editor.Undo();
            Assert(editor.Config.Preview2D == original && editor.Viewport.Mode2D == original, "Dimension undo lost viewport synchronisation.");
        }));
        Check("Editor.StackMutationsAreSingleUndoOperations", () => WithEditor(editor =>
        {
            string initial = Text(editor.Config);
            Invoke(editor, "AddEmitter", "Smoke"); Assert(editor.Config.Emitters.Count == 1, "Emitter not added.");
            editor.Undo(); Assert(Text(editor.Config) == initial && !editor.CanUndo, "Add was not one reversible operation.");
            editor.Redo(); Invoke(editor, "DuplicateSelectedEmitter"); Assert(editor.Config.Emitters.Count == 2, "Duplicate failed.");
            editor.Undo(); Assert(editor.Config.Emitters.Count == 1, "Duplicate undo failed.");
            Invoke(editor, "MoveSelectedEmitter", -1); editor.Undo();
            Assert(editor.Config.EmitterName == JsonSerializer.Deserialize<ParticleConfig>(initial, Json)!.EmitterName, "Move undo lost primary emitter.");
        }));
        Check("Editor.PresetReplacementIsUndoable", () => WithEditor(editor =>
        {
            Invoke(editor, "AddEmitter", "Smoke"); string original = Text(editor.Config);
            editor.ApplyPreset("Portal"); Assert(editor.Config.EffectName == "Portal", "Preset failed.");
            editor.Undo(); Assert(Text(editor.Config) == original, "Preset replacement lost layered effect on undo.");
        }));
        Check("Editor.CurveGestureIsOneUndoEntry", () => WithEditor(editor =>
        {
            string before = Text(editor.Config); Invoke(editor, "BeginParticleGesture");
            for (int i = 1; i <= 20; i++)
            {
                Field<ParticleConfig>(editor, "_config").SizeOverLifetime.Y1 = i / 20d;
                Invoke(editor, "ConfigChanged", false);
            }
            Invoke(editor, "FinishParticleGesture"); editor.Undo();
            Assert(Text(editor.Config) == before && !editor.CanUndo && !editor.IsDirty, "Curve drag created many history entries.");
        }));
        Check("Editor.CancellingCurveRestoresWithoutHistory", () => WithEditor(editor =>
        {
            string before = Text(editor.Config); Invoke(editor, "BeginParticleGesture");
            Field<ParticleConfig>(editor, "_config").SizeOverLifetime.Y1 = .99; Invoke(editor, "ConfigChanged", false);
            Invoke(editor, "CancelParticleGesture");
            Assert(Text(editor.Config) == before && !editor.CanUndo && !editor.IsDirty, "Cancelled gesture remained authored.");
        }));
        Check("Editor.InvalidDefinitionBlocksSaveAndKeepsBytes", () => WithEditor(editor =>
        {
            string before = File.ReadAllText(editor.ResourcePath); editor.SetAuthoringMode(ParticleAuthoringMode.Code);
            Field<CodeEditor>(editor, "_code").CodeText = "particle \"Flame\" {\nunknown.key: 42\n}";
            Throws<InvalidOperationException>(editor.Save);
            Assert(editor.DraftError?.Contains("Line 2", StringComparison.Ordinal) == true && editor.IsDirty
                && File.ReadAllText(editor.ResourcePath) == before, "Invalid draft was silently saved over good content.");
            editor.SetAuthoringMode(ParticleAuthoringMode.Properties);
            Assert(editor.AuthoringMode == ParticleAuthoringMode.Code, "Leaving text mode silently discarded errors.");
            Invoke(editor, "RevertParticleDraft"); Assert(!editor.IsDirty && editor.DraftError is null, "Revert did not restore good content.");
        }));
        Check("Editor.InvalidDefinitionBlocksSelectionAndInspector", () => WithEditor(editor =>
        {
            Invoke(editor, "AddEmitter", "Smoke"); int selected = Field<int>(editor, "_selectedEmitterIndex");
            editor.SetAuthoringMode(ParticleAuthoringMode.Code); Field<CodeEditor>(editor, "_code").CodeText = "invalid";
            Invoke(editor, "SelectEmitter", 0);
            Assert(Field<int>(editor, "_selectedEmitterIndex") == selected && !editor.TryApplyInspectorValue("emitRate", 5), "Pending draft was replaced by a selection/Inspector edit.");
            Invoke(editor, "RevertParticleDraft");
        }));
        Check("Editor.ValidDefinitionAppliesAndIsUndoable", () => WithEditor(editor =>
        {
            double before = editor.Config.EmitRate; editor.SetAuthoringMode(ParticleAuthoringMode.Code);
            Field<CodeEditor>(editor, "_code").CodeText = "particle \"Primary\" {\nemission.rate: 123\nrender.texture: \"Soft Glow\"\n}";
            Invoke(editor, "TryApplyParticleDraft");
            Assert(editor.Config.EmitRate == 123 && editor.Config.TexturePath == "Soft Glow" && editor.DraftError is null, "Valid named-resource draft failed.");
            editor.Undo(); Assert(editor.Config.EmitRate == before && !editor.IsDirty, "Draft did not use shared journal.");
        }));
        Check("Editor.FileReferencesAndUnsafeNumbersAreRejected", () => WithEditor(editor =>
        {
            foreach (string property in new[] { "render.texture: \"Assets/Sprites/Glow.image.json\"", "emission.maximum: 2000000000", "motion.gravity: 1e100, 0, 0" })
            {
                editor.SetAuthoringMode(ParticleAuthoringMode.Code);
                Field<CodeEditor>(editor, "_code").CodeText = "particle \"Primary\" {\n" + property + "\n}";
                Assert(!(bool)Invoke(editor, "TryApplyParticleDraft")!, "Unsafe draft was accepted.");
                Invoke(editor, "RevertParticleDraft");
            }
            Assert(!editor.IsDirty, "Invalid drafts changed the accepted resource.");
        }));
        Check("Editor.SeekYieldsAndCanBeCancelled", () => WithEditor(editor =>
        {
            editor.SetTimelinePosition(.5f);
            Assert(editor.PreviewSeeking && !editor.TimelinePlaying && Math.Abs(editor.TimelinePosition - .5f) < .002,
                "Timeline did not publish its target immediately.");
            Invoke(editor, "PumpPreviewSeek"); editor.StopPreview();
            Assert(!editor.PreviewSeeking && !editor.TimelinePlaying && editor.TimelinePosition == 0, "Stop left a pending replay.");
        }));
        Check("Editor.EditingDuringSeekCancelsObsoleteReplay", () => WithEditor(editor =>
        {
            editor.SetTimelinePosition(.9f); editor.TryApplyInspectorValue("emitRate", 40);
            Assert(!editor.PreviewSeeking, "Seek continued against an edited configuration.");
        }));
        Check("Editor.SaveReopenPreservesLayeredRuntimeSchema", () => WithEditor(editor =>
        {
            Invoke(editor, "AddEmitter", "Smoke"); editor.TryApplyInspectorValue("emitRate", 55);
            editor.SetPreview2D(true); editor.Save();
            using ParticleEditorControl reopened = new(editor.ResourcePath, editor.ProjectRoot);
            Assert(Text(editor.Config) == Text(reopened.Config) && !reopened.IsDirty, "Save/reopen lost authored emitter settings.");
        }));
        Check("Editor.HistoryIsBoundedAt100Operations", () => WithEditor(editor =>
        {
            for (int i = 0; i < 110; i++) editor.TryApplyInspectorValue("emitRate", 10 + i);
            int count = 0; while (editor.CanUndo && count < 120) { editor.Undo(); count++; }
            Assert(count == 100, "Particle snapshot journal is unbounded.");
        }));
        Check("Editor.InvalidLiveNumericValuesCannotPoisonConfig", () => WithEditor(editor =>
        {
            string before = Text(editor.Config);
            Assert(!editor.TryApplyInspectorValue("speed", double.NaN) && !editor.TryApplyInspectorValue("shape", "900"), "Invalid Inspector input accepted.");
            Assert(Text(editor.Config) == before, "Rejected Inspector value mutated the effect.");
        }));
        Check("Editor.CorruptResourceIsNotReplacedWithFire", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "Genesis-H21-corrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "Broken.particle.json"); File.WriteAllText(path, "broken input");
                Throws<InvalidDataException>(() => { using ParticleEditorControl editor = new(path, root); });
                Assert(File.ReadAllText(path) == "broken input", "Failed open overwrote source bytes.");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    private static ParticleConfig Envelope()
    {
        ParticleConfig result = ParticlePresets.Fire(); result.EffectName = "Composite"; result.EmitterName = "Primary";
        result.Duration = 12; result.Preview2D = true; result.PreviewTargetType = ParticlePreviewTargetType.Model;
        result.PreviewTargetAsset = "Training Statue"; result.BackdropSprite = "Backdrop";
        result.Notes = "Keep my notes"; result.Script = "Particle Logic";
        result.Light.Enabled = true; result.Light.Intensity = 3; result.Light.Radius = 7;
        return result;
    }
    private static void AssertEnvelope(ParticleConfig actual, ParticleConfig expected) => Assert(
        actual.EffectName == expected.EffectName && actual.Duration == expected.Duration && actual.Preview2D == expected.Preview2D
        && actual.PreviewTargetType == expected.PreviewTargetType && actual.PreviewTargetAsset == expected.PreviewTargetAsset
        && actual.BackdropSprite == expected.BackdropSprite && actual.Notes == expected.Notes && actual.Script == expected.Script
        && Text(actual.Light) == Text(expected.Light), "Structural operation changed effect-level metadata.");
    private static string Text<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, Private)?.GetValue(instance)
        ?? throw new InvalidOperationException("Missing field " + name));
    private static object? Invoke(object instance, string method, params object[] args)
    {
        try { return (instance.GetType().GetMethod(method, Private) ?? throw new InvalidOperationException("Missing method " + method)).Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void WithEditor(Action<ParticleEditorControl> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "Genesis-H21-particle-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            ProjectSession project = new ProjectService().CreateProject(root, "Workbench"); ResourceService resources = new(project);
            string path = resources.CreateResource(Path.Combine(project.AssetsPath, "Particles"), ResourceKind.Particle, "Campfire");
            File.WriteAllText(path, Text(ParticlePresets.Fire()));
            using ParticleEditorControl editor = new(path, project.RootPath);
            test(editor);
        }
        finally { Directory.Delete(root, true); }
    }
}
