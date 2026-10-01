using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Runtime.Scripting;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// The "Clear" redesign (Documentation/StudioClear.md): shared components, guided workflow bars
/// and one-click starters. The first four cases are pure logic and open no windows.
/// </summary>
internal static class ClearWorkflowSuite
{
    public static void Run(HeadlessContext context)
    {
        RunCore(context);
        RunEditors(context);
    }

    /// <summary>The window-free cases: tokens, workflow bar, gallery and recipe validation.</summary>
    public static void RunCore(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Clear workflows");

        HeadlessHarness.RunCase(context.Report, "Clear.Tokens.DisplayHeadingKeepsAcronymsAndMixedCase", () =>
        {
            (string Input, string Expected)[] cases =
            [
                ("MATERIAL PRESETS", "Material presets"),
                ("HLSL CODE", "HLSL code"),
                ("3D PRIMITIVES", "3D primitives"),
                ("RIG → POSE → ANIMATE", "Rig → pose → animate"),
                ("Grip (friction)", "Grip (friction)"),
                ("INSTANCE FIELDS", "Instance fields"),
            ];
            foreach ((string input, string expected) in cases)
                HeadlessHarness.Assert(UiTokens.DisplayHeading(input) == expected,
                    $"DisplayHeading(\"{input}\") gave \"{UiTokens.DisplayHeading(input)}\", expected \"{expected}\".");
        });

        HeadlessHarness.RunCase(context.Report, "Clear.WorkflowBar.StepsActivateAdvanceAndTrackProgress", () =>
        {
            List<string> opened = [];
            bool secondDone = false;
            using WorkflowBar bar = new("TestWorkflow",
            [
                new("One", "One", "First instruction.", () => opened.Add("One")),
                new("Two", "Two", "Second instruction.", () => opened.Add("Two")) { IsDone = () => secondDone },
                new("Three", "Three", "Third instruction.", () => opened.Add("Three")),
            ]);
            HeadlessHarness.Assert(bar.CurrentStepId == "One" && bar.InstructionText == "First instruction.",
                "A new workflow bar does not start on its first step with that step's instruction.");
            HeadlessHarness.Assert(bar.StepButton("Two").Name == "WorkflowStep_Two"
                && bar.StepButton("Two").AccessibleName == "Step 2: Two",
                "Workflow step buttons lost their stable names or accessible captions.");
            HeadlessHarness.Assert(bar.Activate("Two") && bar.CurrentStepId == "Two" && opened.SequenceEqual(["Two"]),
                "Activating a step did not run its action and make it current.");
            HeadlessHarness.Assert(bar.GoNext() && bar.CurrentStepId == "Three" && !bar.GoNext(),
                "Next did not advance to the following step, or advanced past the last one.");
            bar.SetInstruction("A tip.");
            HeadlessHarness.Assert(bar.InstructionText == "A tip.", "A step tip did not replace the instruction.");
            bar.SetCurrent("One");
            HeadlessHarness.Assert(bar.CurrentStepId == "One" && bar.InstructionText == "First instruction." && opened.Count == 2,
                "SetCurrent must follow the editor without running the step action, and restore the instruction.");
            secondDone = true;
            bar.RefreshProgress();
            HeadlessHarness.Assert(!bar.Activate("Missing"), "An unknown step id was accepted.");
        });

        HeadlessHarness.RunCase(context.Report, "Clear.StarterGallery.ChooseSelectsAndReportsTheItem", () =>
        {
            using WorkflowBar titled = new("TitledWorkflow", [new("A", "Rig & animate", "Ampersands are literal.", () => { })]);
            HeadlessHarness.Assert(titled.StepButton("A").Text == "Rig & animate", "A step title lost its ampersand.");
            using StarterGallery gallery = new("TestGallery") { FitsContent = true, Width = 600 };
            gallery.SetItems(
            [
                new StarterItem("A", "Alpha", "First card.") { Category = "Group", Badge = "Preview only", BadgeTone = StarterBadgeTone.Warning },
                new StarterItem("B", "Beta", "Second card.") { Category = "Group" },
            ]);
            StarterItem? chosen = null;
            gallery.ItemChosen += (_, item) => chosen = item;
            HeadlessHarness.Assert(gallery.Card("B")?.Name == "Starter_B" && gallery.Choose("B")
                && chosen?.Id == "B" && gallery.SelectedId == "B" && !gallery.Choose("Missing"),
                "Choosing a starter card did not select it and report the chosen item.");
            HeadlessHarness.Assert(gallery.PreferredContentHeight > 0, "A gallery with cards reported no content height.");
        });

        HeadlessHarness.RunCase(context.Report, "Clear.Recipes.EveryBehaviourRecipeIsValidPgsl", () =>
        {
            foreach (ObjectBehaviourRecipe recipe in ObjectBehaviourRecipes.All)
            foreach ((string eventId, string block) in recipe.Events)
            {
                HeadlessHarness.Assert(ObjectEventCatalog.Exists(eventId), $"Recipe '{recipe.Title}' writes an unknown event '{eventId}'.");
                HeadlessHarness.Assert(block.StartsWith(recipe.Marker, StringComparison.Ordinal),
                    $"Recipe '{recipe.Title}' {eventId} does not start with its marker comment.");
                PgslValidationReport report = PgslScriptValidator.ValidateSource(block, eventId);
                HeadlessHarness.Assert(report.Errors.Count == 0,
                    $"Recipe '{recipe.Title}' {eventId} is not valid PGSL: {string.Join("; ", report.Errors)}");
            }

            // Every 2D recipe added to one Object must still validate: they declare distinct names.
            foreach (string eventId in ObjectBehaviourRecipes.All.SelectMany(recipe => recipe.Events.Keys).Distinct())
            {
                string combined = ObjectBehaviourRecipes.For(threeD: false)
                    .Where(recipe => recipe.Events.ContainsKey(eventId))
                    .Aggregate(string.Empty, (source, recipe) => ObjectBehaviourRecipes.Append(source, recipe.Events[eventId]));
                if (combined.Length == 0) continue;
                PgslValidationReport report = PgslScriptValidator.ValidateSource(combined, eventId);
                HeadlessHarness.Assert(report.Errors.Count == 0,
                    $"All 2D recipes together in {eventId} are not valid PGSL: {string.Join("; ", report.Errors)}");
            }
        });

    }

    /// <summary>Editor cases: they open editors in hidden, unfocused host windows.</summary>
    public static void RunEditors(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Clear editor workflows");
        GateSuite.GateFixture fixture = new(context, "Clear");

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.TerrainStepsFollowModesAndLandformsApply", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(fixture.Folder(project, "Terrain"), ResourceKind.Terrain, "Clear Terrain");
            using Form host = GateSuite.NewHost(1280, 820);
            using TerrainEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 20);
            WorkflowBar bar = editor.TerrainWorkflow ?? throw new InvalidOperationException("Terrain has no workflow bar.");
            EditorCommandBar commands = editor.Controls.OfType<EditorCommandBar>().Single();
            HeadlessHarness.Assert(bar.Visible && bar.Top >= commands.Bottom - 1 && bar.Steps.Select(step => step.Id)
                .SequenceEqual(["Shape", "Sculpt", "Paint", "Decorate", "UseInGame"]),
                "Terrain's workflow bar is missing, out of order, or not directly under its command bar.");
            // A short editor folds the bar away; growing it back must leave the bar beneath the
            // command bar (re-showing a hidden docked control once moved it above).
            host.ClientSize = new System.Drawing.Size(1280, 520);
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(bar.IsAutoHidden && bar.Height == 0, "A 520px-tall editor did not fold its workflow bar away.");
            host.ClientSize = new System.Drawing.Size(1280, 820);
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(!bar.IsAutoHidden && bar.Height > 0 && bar.Top >= commands.Bottom - 1,
                "The workflow bar did not return beneath the command bar after the editor grew again.");
            HeadlessHarness.Assert(bar.Activate("Sculpt") && editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Sculpt,
                "The Sculpt step did not switch the editor to Sculpt.");
            editor.SetMode(TerrainEditorControl.TerrainEditorMode.Paint);
            HeadlessHarness.Assert(bar.CurrentStepId == "Paint", "Choosing Paint on the rail did not move the workflow bar.");
            StarterGallery landforms = editor.LandformGallery ?? throw new InvalidOperationException("Terrain has no landform gallery.");
            float before = editor.Terrain.GetHeight(editor.Terrain.ResolutionX / 2, editor.Terrain.ResolutionZ / 2);
            HeadlessHarness.Assert(landforms.Items.Count == Enum.GetValues<TerrainPreset>().Length && landforms.Choose(nameof(TerrainPreset.Volcanic)),
                "The landform gallery does not offer every preset.");
            GateSuite.Pump(4, 10);
            float after = editor.Terrain.GetHeight(editor.Terrain.ResolutionX / 2, editor.Terrain.ResolutionZ / 2);
            HeadlessHarness.Assert(Math.Abs(after - before) > 0.01f && editor.IsDirty, "Choosing a landform did not regenerate the terrain.");
            editor.Undo();
            HeadlessHarness.Assert(Math.Abs(editor.Terrain.GetHeight(editor.Terrain.ResolutionX / 2, editor.Terrain.ResolutionZ / 2) - before) < 0.01f,
                "Undo did not restore the terrain from before the landform.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.ObjectRecipesAppendMarkedCodeOnce", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Clear Walker");
            using Form host = GateSuite.NewHost(1280, 820);
            using ObjectEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 20);
            HeadlessHarness.Assert(editor.ObjectWorkflow?.Steps.Count == 4, "The Object editor has no four-step workflow bar.");
            editor.ShowBehaviourRecipes();
            HeadlessHarness.Assert(editor.IsRecipePageVisible && editor.RecipeGallery?.Items.Count > 0,
                "The behaviour recipes page did not open.");
            IReadOnlyList<string> changed = editor.ApplyBehaviourRecipe("MoveArrows");
            HeadlessHarness.Assert(changed.SequenceEqual(["Create", "Step"]) && !editor.IsRecipePageVisible
                && editor.PgslEvents["Step"].Contains("// Recipe: Move with arrow keys", StringComparison.Ordinal)
                && editor.PgslEvents["Create"].Contains("moveSpeed = 4;", StringComparison.Ordinal),
                "The arrow-key recipe did not add its code to Create and Step.");
            HeadlessHarness.Assert(editor.ApplyBehaviourRecipe("MoveArrows").Count == 0,
                "Adding the same recipe twice duplicated its code.");
            editor.ApplyBehaviourRecipe("Spin");
            HeadlessHarness.Assert(editor.PgslEvents["Step"].Contains("TileMoveX", StringComparison.Ordinal)
                && editor.PgslEvents["Step"].Contains("image_angle", StringComparison.Ordinal),
                "A second recipe replaced the first instead of appending to it.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.RoomScenePresetIsOneUndoableEdit", () =>
        {
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(fixture.Folder(project, "Rooms"), ResourceKind.Room, "Clear Room");
            using Form host = GateSuite.NewHost(1440, 900);
            using RoomEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 20);
            editor.ViewMode3D = true;
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(editor.RoomWorkflow?.StepButton("Ground").Text == "Ground",
                "A 3D Room does not show the 3D steps (Ground › Place › Sky › Camera).");
            float hours = editor.Room.Environment.TimeOfDayHours;
            HeadlessHarness.Assert(editor.ApplyScenePreset("Night")
                && Math.Abs(editor.Room.Environment.TimeOfDayHours - 23.5f) < 0.01f
                && editor.Room.Environment.AtmospherePreset == "Night" && editor.Room.Environment.TimeScale == 0f,
                "The Night preset did not set the clock, atmosphere and time scale.");
            editor.Undo();
            HeadlessHarness.Assert(Math.Abs(editor.Room.Environment.TimeOfDayHours - hours) < 0.01f,
                "Undo did not restore the environment from before the scene preset.");
            editor.ViewMode3D = false;
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(editor.RoomWorkflow?.StepButton("Ground").Text == "Tiles",
                "Switching to 2D did not switch the workflow bar to the 2D steps.");
        });
    }
}
