using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.UI;
using Genesis.Application.Core.Objects;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Forms;
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


        HeadlessHarness.RunCase(context.Report, "Clear.Recipes.EveryBehaviourRecipeRunsInTheGameVm", () =>
        {
            // Validation proves the syntax; this runs each recipe's events in the real VM.
            foreach (ObjectBehaviourRecipe recipe in ObjectBehaviourRecipes.All)
            {
                ObjectSandboxResult result = ObjectSandbox.Run(recipe.Events, frames: 30);
                HeadlessHarness.Assert(result.Ok && result.FramesRun == 30,
                    $"Recipe '{recipe.Title}' failed in the VM: {string.Join("; ", result.Errors.Select(error => error.ToString()))}");
            }

            ObjectSandboxResult patrol = ObjectSandbox.Run(ObjectBehaviourRecipes.Find("Patrol")!.Events, frames: 30);
            HeadlessHarness.Assert(Math.Abs(patrol.X - 60) < 0.01, $"Patrol moved to x={patrol.X} after 30 frames, expected 60.");
            ObjectSandboxResult longPatrol = ObjectSandbox.Run(ObjectBehaviourRecipes.Find("Patrol")!.Events, frames: 200);
            HeadlessHarness.Assert(longPatrol.X is >= -98 and <= 98, $"Patrol left its 96-pixel range (x={longPatrol.X}).");
            ObjectSandboxResult shot = ObjectSandbox.Run(ObjectBehaviourRecipes.Find("Projectile")!.Events, frames: 30);
            HeadlessHarness.Assert(Math.Abs(shot.X - 240) < 0.01 && Math.Abs(shot.Y) < 0.01,
                $"Projectile travelled to ({shot.X}, {shot.Y}), expected (240, 0).");
            ObjectSandboxResult floating = ObjectSandbox.Run(ObjectBehaviourRecipes.Find("Float")!.Events, frames: 90);
            HeadlessHarness.Assert(Math.Abs(floating.Y) <= 4.01, $"Float drifted {floating.Y} pixels from its home.");
            ObjectSandboxResult timed = ObjectSandbox.Run(ObjectBehaviourRecipes.Find("Timed")!.Events, frames: 200);
            HeadlessHarness.Assert(timed.EventsFired.Contains("Alarm11"), "The three-second timer never fired its alarm.");
        });

        HeadlessHarness.RunCase(context.Report, "Clear.Guide.GettingStartedNamesRealRecipesAndSteps", () =>
        {
            string path = StudioShellForm.FindDocumentation(AppContext.BaseDirectory, GuideViewerForm.GettingStartedFile)
                ?? throw new InvalidOperationException("Documentation/GettingStarted.md was not found from the test host.");
            IReadOnlyList<GuideBlock> blocks = GuideViewerForm.Parse(File.ReadAllText(path));
            HeadlessHarness.Assert(blocks.Count(block => block.Kind == GuideBlockKind.Title) == 1
                && blocks[0].Kind == GuideBlockKind.Title,
                "The guide does not open with exactly one title.");
            string[] headings = blocks.Where(block => block.Kind == GuideBlockKind.Heading).Select(block => block.Text).ToArray();
            HeadlessHarness.Assert(headings.SequenceEqual(
                [
                    "Your first game in four steps", "Recipes you can combine", "Making a 3D scene",
                    "Where things are", "If something goes wrong",
                ]),
                "The guide's sections changed: " + string.Join(" | ", headings));

            // A guide that names a recipe the editor does not offer sends a beginner looking for nothing.
            GuideBlock[] recipeItems = Section(blocks, "Recipes you can combine")
                .Where(block => block.Kind == GuideBlockKind.Bullet).ToArray();
            string[] named = recipeItems.SelectMany(block => BoldRuns(block.Text)).ToArray();
            string[] titles = ObjectBehaviourRecipes.All.Select(recipe => recipe.Title).Distinct().ToArray();
            HeadlessHarness.Assert(named.Length == titles.Length && named.All(name => titles.Contains(name)),
                $"The guide names {named.Length} recipes ({string.Join(", ", named.Except(titles))} unknown); the editor offers {titles.Length}.");

            GuideBlock[] firstGame = Section(blocks, "Your first game in four steps")
                .Where(block => block.Kind == GuideBlockKind.Numbered).ToArray();
            HeadlessHarness.Assert(firstGame.Select(block => block.Number).SequenceEqual([1, 2, 3, 4])
                && firstGame.Select(block => BoldRuns(block.Text).First())
                    .SequenceEqual(["Draw a sprite.", "Make an Object.", "Build a Room.", "Press Run."]),
                "The first game is no longer four numbered steps matching Home.");
            HeadlessHarness.Assert(GuideViewerForm.PlainText("**Run** the *game* with `F5`") == "Run the game with F5",
                "Inline marks are not removed from displayed text.");
        });

        HeadlessHarness.RunCase(context.Report, "Clear.Guide.TheThreeDSceneStepsEndInTheFirstRoom", () =>
        {
            // Following "Making a 3D scene" must end in the 3D Room, not the empty Start Room a new project
            // begins with: the guide says the new Room takes its place, and Run plays the first Room.
            string path = StudioShellForm.FindDocumentation(AppContext.BaseDirectory, GuideViewerForm.GettingStartedFile)
                ?? throw new InvalidOperationException("Documentation/GettingStarted.md was not found from the test host.");
            GuideBlock[] steps = Section(GuideViewerForm.Parse(File.ReadAllText(path)), "Making a 3D scene")
                .Where(block => block.Kind == GuideBlockKind.Numbered).ToArray();
            GuideBlock useInGame = steps.Single(block => block.Number == 2);
            HeadlessHarness.Assert(useInGame.Text.Contains("Start", StringComparison.Ordinal) && useInGame.Text.Contains("first Room", StringComparison.Ordinal),
                "Step 2 of the 3D scene does not say what happens to the empty Start Room and the first Room.");
            HeadlessHarness.Assert(steps.Single(block => block.Number == 6).Text.Contains("first Room", StringComparison.Ordinal),
                "The Run step of the 3D scene does not tell a beginner that Run plays the first Room.");
            string[] titles = ObjectBehaviourRecipes.All.Select(recipe => recipe.Title).ToArray();
            string[] walking = BoldRuns(steps.Single(block => block.Number == 5).Text).ToArray();
            HeadlessHarness.Assert(walking.Length > 0 && walking.All(name => titles.Contains(name)),
                "The walking step names a recipe the Object editor does not offer: " + string.Join(", ", walking.Except(titles)));
        });

        HeadlessHarness.RunCase(context.Report, "Clear.Guide.CookbookPgslCompilesAndNamesRealRecipes", () =>
        {
            // The cookbook is what a person or an AI copies from: every PGSL snippet in it must pass the
            // strict check, and every recipe it names must be one the editor and automation offer.
            string path = StudioShellForm.FindDocumentation(AppContext.BaseDirectory, "Cookbook.md")
                ?? throw new InvalidOperationException("Documentation/Cookbook.md was not found from the test host.");
            string text = File.ReadAllText(path);
            var snippets = System.Text.RegularExpressions.Regex.Matches(text, "```pgsl\\r?\\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(match => match.Groups[1].Value).ToArray();
            HeadlessHarness.Assert(snippets.Length >= 6, $"The cookbook has {snippets.Length} PGSL snippets; expected at least six.");
            foreach (string snippet in snippets)
            {
                // Strict, as Run checks a game: a misspelt command is an error, not a silent no-op.
                PgslValidationReport report = PgslScriptValidator.ValidateSource(snippet, "Step", strict: true);
                HeadlessHarness.Assert(report.Errors.Count == 0, $"A cookbook snippet does not compile: {string.Join("; ", report.Errors)}\n{snippet}");
            }
            string[] ids = ObjectBehaviourRecipes.All.Select(recipe => recipe.Id).ToArray();
            string[] named = System.Text.RegularExpressions.Regex.Matches(text, "^\\| `([A-Za-z0-9]+)`", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value).ToArray();
            HeadlessHarness.Assert(named.Length >= 2 && named.All(ids.Contains),
                $"The cookbook's recipe table names recipes there are not: {string.Join(", ", named.Except(ids))}.");
        });
    }

    private static IEnumerable<GuideBlock> Section(IReadOnlyList<GuideBlock> blocks, string heading) =>
        blocks.SkipWhile(block => !(block.Kind == GuideBlockKind.Heading && block.Text == heading))
            .Skip(1)
            .TakeWhile(block => block.Kind != GuideBlockKind.Heading);

    private static IEnumerable<string> BoldRuns(string text)
    {
        for (int start = text.IndexOf("**", StringComparison.Ordinal); start >= 0;)
        {
            int end = text.IndexOf("**", start + 2, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return text[(start + 2)..end];
            start = text.IndexOf("**", end + 2, StringComparison.Ordinal);
        }
    }

    /// <summary>Editor cases: they open editors in hidden, unfocused host windows.</summary>
    public static void RunEditors(HeadlessContext context)
    {
        HeadlessHarness.BeginMajor(context.Report, "Clear editor workflows");
        GateSuite.GateFixture fixture = new(context, "Clear");

        HeadlessHarness.RunCase(context.Report, "Shell.Guide.ViewerShowsTheGuideWithoutMarkdownMarks", () =>
        {
            string path = StudioShellForm.FindDocumentation(AppContext.BaseDirectory, GuideViewerForm.GettingStartedFile)
                ?? throw new InvalidOperationException("Documentation/GettingStarted.md was not found from the test host.");
            using GuideViewerForm viewer = new("Getting started", File.ReadAllText(path));
            GateSuite.ShowHost(viewer);
            GateSuite.Pump(4, 10);
            string shown = viewer.DisplayedText;
            HeadlessHarness.Assert(shown.Contains("Getting started with Genesis Studio", StringComparison.Ordinal)
                && shown.Contains("Your first game in four steps", StringComparison.Ordinal)
                && shown.Contains("1.\tDraw a sprite.", StringComparison.Ordinal)
                && shown.Contains("\u2022\tMove with arrow keys", StringComparison.Ordinal),
                "The guide viewer does not show the guide's headings, numbered steps and bullets.");
            HeadlessHarness.Assert(!shown.Contains("**", StringComparison.Ordinal) && !shown.Contains("## ", StringComparison.Ordinal)
                && !shown.Contains('`') && !shown.Contains("\n- ", StringComparison.Ordinal),
                "The guide viewer shows raw Markdown marks.");
            Control close = viewer.Controls.Find("GuideClose", searchAllChildren: true).Single();
            HeadlessHarness.Assert(close.Visible && viewer.CancelButton == close, "The guide viewer cannot be closed with Escape.");
            // A rich text page only renders through PrintWindow, so capture the window's pixels.
            string image = "clear-guide-viewer.png";
            context.Report.Images.Add(ImageResult.From("Getting started guide viewer", image,
                VisualCapture.CaptureOpenForm(viewer, Path.Combine(context.Captures, image), includeViewports: true)));
        });

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

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.TheToolbarsSwitchTo3DOpensOnASky", () =>
        {
            // A new room made 3D with the toolbar's switch has a sunny day's sky, not a dark void,
            // and one undo takes both back.
            ProjectSession project = fixture.Blank;
            string path = fixture.Resources(project).CreateResource(fixture.Folder(project, "Rooms"), ResourceKind.Room, "Sky Switch Room");
            using Form host = GateSuite.NewHost(1440, 900);
            using RoomEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 20);
            HeadlessHarness.Assert(!editor.ViewMode3D && !editor.Room.Environment.DynamicSky, "A new room was not a 2D room without a sky.");
            // A painted look's choices outlive the sky: the environment is copied whole.
            editor.Room.Environment.Shadows = false;
            editor.Room.Environment.ToneMapping = false;
            editor.SwitchDimensionFromToolbar(threeD: true);
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(editor.ViewMode3D && editor.Room.Environment.DynamicSky && editor.Room.Environment.TimeScale == 0f
                && editor.Room.Environment.AtmospherePreset == "ClearDay",
                "Switching a room with no sky to 3D did not give it a sunny day.");
            HeadlessHarness.Assert(!editor.Room.Environment.Shadows && !editor.Room.Environment.ToneMapping,
                "Giving the room a sky turned its shadows or tone mapping back on.");
            editor.Undo();
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(!editor.ViewMode3D && !editor.Room.Environment.DynamicSky, "One undo did not take back both the switch and the sky.");
            editor.Redo();
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(editor.ViewMode3D && editor.Room.Environment.DynamicSky && !editor.Room.Environment.Shadows,
                "Redo did not bring back the 3D room and its sky as they were.");
            editor.Undo();
            editor.ApplyScenePreset("Indoor");
            editor.SwitchDimensionFromToolbar(threeD: true);
            editor.SwitchDimensionFromToolbar(threeD: false);
            editor.SwitchDimensionFromToolbar(threeD: true);
            HeadlessHarness.Assert(editor.ViewMode3D && editor.Room.Environment.DynamicSky, "Switching back to 3D lost the sky it was given.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.TerrainRoomStartsTheGameWhenTheFirstRoomIsEmpty", () =>
        {
            // A new project's only Room is an empty 2D "Start", and Run plays the first Room. Use in game on
            // a Terrain has to put its 3D Room first, or Run plays the empty Start and shows nothing.
            ProjectSession project = NewProject(fixture, "Terrain Starts The Game");
            HeadlessHarness.Assert(ProjectRoomOrder.First(project) == "Start", "A new project does not begin with its empty Start Room first.");
            (string created, string guideText) = UseTerrainInGame(project, "Starter Ground", "Starter World");

            ProjectSession reopened = new ProjectService().OpenProject(project.RootPath);
            HeadlessHarness.Assert(ProjectRoomOrder.Rooms(reopened).SequenceEqual(["Starter World", "Start"]) && reopened.Manifest.StartRoom == "Starter World",
                "The terrain's Room did not take the empty first Room's place (or the empty Room was lost): " + string.Join(", ", ProjectRoomOrder.Rooms(reopened)));
            HeadlessHarness.Assert(Genesis.Runtime.Project.ProjectRunLauncher.RoomToStart(project.RootPath, null) == "Starter World",
                "A run without a Room (automation's game.run) does not start in the terrain's Room.");
            HeadlessHarness.Assert(created.Contains("first Room", StringComparison.Ordinal) && created.Contains("Run plays it", StringComparison.Ordinal),
                "The result does not say the new Room now starts the game: " + created);

            // The steps after the button name a real recipe and no longer point at a hidden template.
            string[] recipes = ObjectBehaviourRecipes.All.Select(recipe => recipe.Title).ToArray();
            HeadlessHarness.Assert(guideText.Contains("Walk in first person", StringComparison.Ordinal) && recipes.Contains("Walk in first person")
                && !guideText.Contains("Gate Climbing", StringComparison.Ordinal) && !guideText.Contains("player controller", StringComparison.Ordinal),
                "The Terrain guide does not lead to Behaviour > Walk in first person, or still mentions the Gate Climbing template.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.TerrainRoomKeepsTheFirstRoomWhenItHasContent", () =>
        {
            // A first Room with an Object in it is somebody's game: the terrain's Room goes last, and the
            // result says how to make it the first.
            ProjectSession project = NewProject(fixture, "Terrain Keeps The Start");
            ResourceService resources = new(project);
            resources.CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.GameObject), ResourceKind.GameObject, "Start Keeper");
            string startPath = Path.Combine(project.AssetsPath, "Rooms", "Start.room.json");
            Genesis.Runtime.Scene.RoomAsset start = Genesis.Runtime.Scene.RoomAssetLoader.Parse(startPath);
            start.Nodes.Add(new Genesis.Runtime.Scene.RoomNode
            {
                Name = "Keeper", Kind = Genesis.Runtime.Scene.RoomNodeKind.GameObject, LayerId = start.Layers[0].Id,
                GameObject = new Genesis.Runtime.Scene.RoomGameObjectData { Prefab = "Start Keeper" },
            });
            Genesis.Runtime.Scene.RoomAssetLoader.Save(start, startPath);

            (string created, _) = UseTerrainInGame(project, "Hill Ground", "Hill World");

            ProjectSession reopened = new ProjectService().OpenProject(project.RootPath);
            HeadlessHarness.Assert(ProjectRoomOrder.Rooms(reopened).SequenceEqual(["Start", "Hill World"]) && reopened.Manifest.StartRoom == "Start",
                "A first Room with an Object in it was displaced by the terrain's Room: " + string.Join(", ", ProjectRoomOrder.Rooms(reopened)));
            HeadlessHarness.Assert(created.Contains("make this the first Room", StringComparison.Ordinal) && created.Contains("last", StringComparison.Ordinal),
                "The result does not say how to make the new Room the first one: " + created);
        });

        HeadlessHarness.RunCase(context.Report, "Studio.Shell.TakesTheRoomOrderAnEditorSaved", () =>
        {
            // Editors save a new Room order through their own copy of the project. The open window must
            // take it, or Run plays the old first Room and the window's next save writes the old order back.
            ProjectSession project = NewProject(fixture, "Shell Takes The Order");
            var services = new StudioServices(new SettingsService(Path.Combine(project.RootPath, "shell.settings.json")),
                new ProjectService(), new ProjectValidator(), new StudioLog(Path.Combine(context.Logs, "shell-room-order.log")));
            services.Settings.Current.Runtime.VSyncInPreview = false;
            using var shell = new StudioShellForm(services, project, persistLayout: false);
            GateSuite.ShowHost(shell);
            string world = new ResourceService(project).CreateResource(ResourceFolderPolicy.RootFor(project, ResourceKind.Room), ResourceKind.Room, "Order World");
            ProjectSession editorCopy = new ProjectService().OpenProject(project.RootPath);
            ProjectRoomOrder.Move(editorCopy, world, ProjectRoomOrder.First(editorCopy));
            GateSuite.Pump(4, 10);
            HeadlessHarness.Assert(shell.Project.Manifest.StartRoom == "Order World" && ProjectRoomOrder.First(shell.Project) == "Order World",
                "The window kept its old first Room after an editor saved a new order: " + shell.Project.Manifest.StartRoom);
            new ProjectService().Save(shell.Project);
            HeadlessHarness.Assert(new ProjectService().OpenProject(project.RootPath).Manifest.StartRoom == "Order World",
                "The window's next save wrote the old Room order back.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.TheRoomGuideMakesThisRoomTheFirstOne", () =>
        {
            // The guide's third step says "make this the first Room"; its button does it, in 2D and 3D alike.
            ProjectSession project = NewProject(fixture, "Room Guide Makes First");
            string path = new ResourceService(project).CreateResource(fixture.Folder(project, "Rooms"), ResourceKind.Room, "Second Room");
            using Form host = GateSuite.NewHost(1440, 900);
            using RoomEditorControl editor = new(path, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(8, 20);
            FlowLayoutPanel OpenGuide()
            {
                HeadlessHarness.Assert(editor.RoomWorkflow?.Activate("UseInGame") == true, "The Room has no Use in game step.");
                GateSuite.Pump(2, 10);
                return Descendants(editor).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "RoomUseInGame");
            }
            string GuideText(Control guide) => string.Join("\n", guide.Controls.OfType<Label>().Select(label => label.Text));
            string FirstRoomOnDisk() => ProjectRoomOrder.First(new ProjectService().OpenProject(project.RootPath)) ?? "";

            FlowLayoutPanel twoDGuide = OpenGuide();
            HeadlessHarness.Assert(!editor.ViewMode3D && GuideText(twoDGuide).Contains("Tilesets", StringComparison.Ordinal)
                && !GuideText(twoDGuide).Contains("Walk in first person", StringComparison.Ordinal),
                "The 2D Room guide lost its tile steps or talks about first-person walking.");
            HeadlessHarness.Assert(FirstRoomOnDisk() == "Start", "The project did not begin with Start as its first Room.");
            twoDGuide.Controls.OfType<Button>().Single(button => button.Name == "RoomGuideMakeFirst").PerformClick();
            GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(FirstRoomOnDisk() == "Second Room"
                && new ProjectService().OpenProject(project.RootPath).Manifest.StartRoom == "Second Room",
                "The guide's button did not put this Room first in the saved project.");
            HeadlessHarness.Assert(twoDGuide.Controls.OfType<Label>().Single(label => label.Name == "RoomGuideFirstRoomStatus")
                .Text.Contains("now the first Room", StringComparison.Ordinal), "The guide did not say the Room is now the first.");

            editor.ViewMode3D = true;
            GateSuite.Pump(4, 10);
            FlowLayoutPanel threeDGuide = OpenGuide();
            string threeD = GuideText(threeDGuide);
            HeadlessHarness.Assert(threeD.Contains("Terrain", StringComparison.Ordinal) && threeD.Contains("Walk in first person", StringComparison.Ordinal)
                && !threeD.Contains("Tilesets", StringComparison.Ordinal) && !threeD.Contains("platformer", StringComparison.Ordinal),
                "The 3D Room guide still speaks of tiles and platformers instead of Terrain and a first-person Object.");
            threeDGuide.Controls.OfType<Button>().Single(button => button.Name == "RoomGuideMakeFirst").PerformClick();
            GateSuite.Pump(2, 10);
            HeadlessHarness.Assert(FirstRoomOnDisk() == "Second Room" && threeDGuide.Controls.OfType<Label>()
                    .Single(label => label.Name == "RoomGuideFirstRoomStatus").Text.Contains("is the first Room", StringComparison.Ordinal),
                "Pressing the button on the Room that is already first changed the order or did not say so.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.ParticleAttachesToAnExistingObject", () =>
        {
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            string objectPath = resources.CreateResource(fixture.Folder(project, "Objects"), ResourceKind.GameObject, "Clear Torch");
            string particlePath = resources.CreateResource(fixture.Folder(project, "Particles"), ResourceKind.Particle, "Clear Sparks");
            using Form host = GateSuite.NewHost(1280, 820);
            using ParticleEditorControl editor = new(particlePath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 20);
            HeadlessHarness.Assert(editor.ParticleWorkflow?.Steps.Select(step => step.Id).SequenceEqual(["Effect", "Tune", "UseInGame"]) == true,
                "The Particle editor has no Effect › Tune › Use in game bar.");
            string attached = editor.AttachEffectToObject("Clear Torch");
            string json = File.ReadAllText(objectPath);
            HeadlessHarness.Assert(Path.GetFullPath(attached) == Path.GetFullPath(objectPath)
                && json.Contains("ParticleComponent", StringComparison.Ordinal)
                && json.Contains("Clear Sparks", StringComparison.Ordinal),
                "Attaching the effect did not add a Particle component that references it to the existing Object.");
            bool rejected = false;
            try { editor.AttachEffectToObject("No Such Object"); }
            catch (InvalidOperationException) { rejected = true; }
            HeadlessHarness.Assert(rejected, "Attaching to an Object that does not exist was not refused.");
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.ModelShaderCreatesAShadedModelObject", () =>
        {
            ProjectSession project = fixture.ThreeD;
            ResourceService resources = fixture.Resources(project);
            string modelPath = Directory.EnumerateFiles(project.AssetsPath, "*.model.json", SearchOption.AllDirectories).First();
            string shaderPath = resources.CreateResource(fixture.Folder(project, "Shaders"), ResourceKind.Shader, "Clear Toon");
            using Form host = GateSuite.NewHost(1280, 820);
            using ShaderEditorControl editor = new(shaderPath, project.RootPath);
            host.Controls.Add(editor);
            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 20);
            HeadlessHarness.Assert(editor.SelectPreset("Toon") && editor.ChoosePreviewAsset(modelPath),
                "The Toon look could not be chosen with a project Model as its preview resource.");
            string objectPath = editor.CreateShaderObject("Clear Toon Model");
            Newtonsoft.Json.Linq.JObject created = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(objectPath));
            string json = created.ToString();
            HeadlessHarness.Assert((string?)created["dimension"] == "ThreeD"
                && json.Contains("ModelRendererComponent", StringComparison.Ordinal)
                && json.Contains("ShaderComponent", StringComparison.Ordinal)
                && json.Contains("Clear Toon", StringComparison.Ordinal),
                "A model look did not create a 3D Object that draws the Model with the Shader.");
        });

        List<string> shouting = [];
        int editorsOpened = 0;
        HeadlessHarness.RunCase(context.Report, "Editor.Workflow.EveryEditorShowsItsStepsUnderTheCommandBar", () =>
        {
            // The table in Documentation/StudioClear.md, as a test: one guided bar per editor, the
            // documented steps in order, ending in Use in game, directly beneath the command bar.
            ProjectSession project = fixture.Blank;
            ResourceService resources = fixture.Resources(project);
            // Each kind is created in its own root folder; the project refuses any other.
            string New(ResourceKind kind, string name) => resources.CreateResource(
                fixture.Folder(project, ResourceFolderPolicy.Roots.First(root => root.Kind == kind).Name), kind, name);

            (string Editor, Func<Control> Create, string[] Steps)[] editors =
            [
                ("Terrain", () => new TerrainEditorControl(New(ResourceKind.Terrain, "Steps Terrain"), project.RootPath),
                    ["Shape", "Sculpt", "Paint", "Decorate", "UseInGame"]),
                ("Model viewer", () => new ModelViewerControl(New(ResourceKind.Model, "Steps Viewer Model"), project.RootPath),
                    ["Import", "Edit", "Animate", "UseInGame"]),
                ("Model editor", () => new ModelEditorControl(New(ResourceKind.Model, "Steps Editor Model"), project.RootPath),
                    ["Create", "Shape", "Surface", "Rig", "UseInGame"]),
                ("Image", () => new ImageEditorControl(new ImageDocumentSession(ImageDocument.CreateDefault(32, 32)),
                        ImageWorkspace.CreateBlank(32, 32, System.Drawing.Color.Transparent)),
                    ["Draw", "Animate", "Rig", "UseInGame"]),
                ("Shader", () => new ShaderEditorControl(New(ResourceKind.Shader, "Steps Shader"), project.RootPath),
                    ["Look", "Preview", "Tune", "UseInGame"]),
                ("Object", () => new ObjectEditorControl(New(ResourceKind.GameObject, "Steps Object"), project.RootPath),
                    ["Look", "Behaviour", "Test", "UseInGame"]),
                ("Particle", () => new ParticleEditorControl(New(ResourceKind.Particle, "Steps Particle"), project.RootPath),
                    ["Effect", "Tune", "UseInGame"]),
                ("Physics", () => new PhysicsEditorControl(New(ResourceKind.Physics, "Steps Physics"), project.RootPath),
                    ["Kind", "Setup", "Tune", "Test", "UseInGame"]),
                ("Room", () => new RoomEditorControl(New(ResourceKind.Room, "Steps Room"), project.RootPath),
                    ["Ground", "Place", "Sky", "Camera", "UseInGame"]),
                ("Audio", () => new AudioEditorControl(New(ResourceKind.Audio, "Steps Audio"), project.RootPath),
                    ["Sound", "Tune", "Listen", "UseInGame"]),
                ("Pathing", () => new PathingEditorControl(New(ResourceKind.Pathing, "Steps Pathing"), project.RootPath),
                    ["Route", "Preview", "UseInGame"]),
                ("UI", () => new UiEditorControl(New(ResourceKind.UserInterface, "Steps UI"), project.RootPath),
                    ["Start", "Design", "UseInGame"]),
                ("Script", () => new PgslScriptEditorControl(New(ResourceKind.PgslScript, "Steps Script"), project.RootPath),
                    ["Write", "Check", "UseInGame"]),
            ];

            foreach ((string name, Func<Control> create, string[] steps) in editors)
            {
                using Form host = GateSuite.NewHost(1360, 860);
                using Control editor = create();
                editor.Dock = DockStyle.Fill;
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(8, 20);
                editorsOpened++;
                shouting.AddRange(ShoutingCaptions(editor).Distinct().Select(caption => $"{name}: \"{caption}\""));

                WorkflowBar[] bars = Descendants(editor).OfType<WorkflowBar>().ToArray();
                HeadlessHarness.Assert(bars.Length == 1, $"{name}: expected one workflow bar, found {bars.Length}.");
                WorkflowBar bar = bars[0];
                HeadlessHarness.Assert(bar.Steps.Select(step => step.Id).SequenceEqual(steps),
                    $"{name}: steps are {string.Join(" > ", bar.Steps.Select(step => step.Id))}, expected {string.Join(" > ", steps)}.");
                HeadlessHarness.Assert(bar.Visible && !bar.IsAutoHidden && bar.Height > 0 && bar.Width > 400,
                    $"{name}: the workflow bar is not shown in a {host.ClientSize.Width}x{host.ClientSize.Height} editor ({bar.Bounds}).");
                HeadlessHarness.Assert(bar.CurrentStepId is not null && !string.IsNullOrWhiteSpace(bar.InstructionText)
                    && bar.Steps.All(step => !string.IsNullOrWhiteSpace(step.Title) && !string.IsNullOrWhiteSpace(step.Instruction)),
                    $"{name}: the bar has no current step or a step without a title and instruction.");
                foreach (WorkflowStep step in bar.Steps)
                {
                    Control button = bar.StepButton(step.Id);
                    HeadlessHarness.Assert(button.Visible && button.Width > 0 && bar.ClientRectangle.Contains(button.Bounds),
                        $"{name}: step '{step.Title}' is clipped or hidden ({button.Bounds} in {bar.ClientSize}).");
                }

                // Nothing above the bar except command rows: it must sit in the top band of the editor.
                System.Drawing.Rectangle inEditor = editor.RectangleToClient(bar.RectangleToScreen(bar.ClientRectangle));
                HeadlessHarness.Assert(inEditor.Top is >= 20 and <= 150,
                    $"{name}: the workflow bar is at y={inEditor.Top}, not directly under the command bar.");
                EditorCommandBar? commands = Descendants(editor).OfType<EditorCommandBar>()
                    .FirstOrDefault(candidate => candidate.Parent == bar.Parent);
                if (commands is not null)
                {
                    HeadlessHarness.Assert(bar.Top >= commands.Bottom - 1 && bar.Top <= commands.Bottom + 2,
                        $"{name}: the workflow bar (top {bar.Top}) does not touch its command bar (bottom {commands.Bottom}).");
                }
            }
        });

        HeadlessHarness.RunCase(context.Report, "Editor.Captions.NoEditorShowsAnAllCapsHeading", () =>
        {
            // Uses the editors the case above opened, including their pages that are not showing.
            HeadlessHarness.Assert(editorsOpened == 13, $"Only {editorsOpened} of 13 editors were opened, so their captions were not all read.");
            HeadlessHarness.Assert(shouting.Count == 0,
                "These captions are still ALL CAPS (use sentence case or UiTokens.DisplayHeading): " + string.Join("; ", shouting));
        });
    }

    /// <summary>
    /// Labels, buttons, group boxes and tool strip items whose text is capitals that
    /// <see cref="UiTokens.DisplayHeading"/> would rewrite. Acronyms and axis letters pass.
    /// </summary>
    private static IEnumerable<string> ShoutingCaptions(Control root)
    {
        static bool Shouts(string? text) =>
            !string.IsNullOrWhiteSpace(text)
            && text.Count(char.IsLetter) >= 4
            && !text.Any(char.IsLower)
            && UiTokens.DisplayHeading(text) != text;

        foreach (Control control in Descendants(root))
        {
            if (control is Label or ButtonBase or GroupBox && Shouts(control.Text))
            {
                yield return control.Text;
            }

            if (control is ToolStrip strip)
            {
                foreach (ToolStripItem item in strip.Items)
                {
                    if (Shouts(item.Text))
                    {
                        yield return item.Text!;
                    }
                }
            }
        }
    }

    /// <summary>A blank project of its own, so the Room order a case checks is not changed by the cases around it.</summary>
    private static ProjectSession NewProject(GateSuite.GateFixture fixture, string name)
    {
        Directory.CreateDirectory(fixture.Root);
        return new ProjectService().CreateProject(fixture.Root, name, "Blank");
    }

    /// <summary>
    /// Opens a new Terrain with a landform, presses Use in game, names the Room and presses Save and
    /// create 3D Room, as a person would. Returns the line the guide shows afterwards and the guide's
    /// whole text.
    /// </summary>
    private static (string Result, string GuideText) UseTerrainInGame(ProjectSession project, string terrainName, string roomName)
    {
        string terrainPath = new ResourceService(project).CreateResource(
            ResourceFolderPolicy.RootFor(project, ResourceKind.Terrain), ResourceKind.Terrain, terrainName);
        using Form host = GateSuite.NewHost(1280, 820);
        using TerrainEditorControl terrain = new(terrainPath, project.RootPath);
        host.Controls.Add(terrain);
        GateSuite.ShowHost(host);
        GateSuite.Pump(8, 20);
        HeadlessHarness.Assert(terrain.LandformGallery?.Choose("RollingHills") == true, "The Rolling hills landform could not be chosen.");
        Descendants(terrain).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
            .First(item => item.Text == "Use in game").PerformClick();
        GateSuite.Pump(4, 10);
        FlowLayoutPanel guide = Descendants(terrain).OfType<FlowLayoutPanel>().Single(panel => panel.Name == "TerrainUseInGame");
        Descendants(guide).OfType<TextBox>().Single(box => box.Name == "TerrainRoomName").Text = roomName;
        Descendants(guide).OfType<Button>().Single(button => button.Name == "TerrainCreateRoom").PerformClick();
        GateSuite.Pump(4, 10);
        HeadlessHarness.Assert(!terrain.IsDirty, "Use in game did not save the terrain.");
        Label[] labels = guide.Controls.OfType<Label>().ToArray();
        return (labels.Single(label => label.Name == "TerrainCreateRoomResult").Text, string.Join("\n", labels.Select(label => label.Text)));
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
