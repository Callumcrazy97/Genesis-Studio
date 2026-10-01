using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.UI;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Scripts;
using Genesis.Application.Editors.Suite.Terrain;
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
