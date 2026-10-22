using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Animation;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Assets;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Animation Controller resources: a new one starts as idle, move (a blend by speed) and jump; the
/// editor adds states and transitions with undo, and saves; a game builds it into an instance's
/// state machine, whose transitions follow the parameters scripts set.
/// </summary>
internal static class AnimationControllerSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Editor.AnimationController.EditedSavedAndRunInAGame", () =>
        {
            string parent = Path.Combine(ctx.Workspace, "AnimationControllers");
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            Directory.CreateDirectory(parent);
            ProjectSession project = new ProjectService().CreateProject(parent, "Animator Game", "Blank");
            ResourceService resources = new(project);
            string folder = Path.Combine(project.AssetsPath, "Animators");
            Directory.CreateDirectory(folder);
            string path = resources.CreateResource(folder, ResourceKind.AnimationController, "Hero Animator");
            HeadlessHarness.Assert(path.EndsWith(".animator.json", StringComparison.OrdinalIgnoreCase), "The controller was made as " + path);
            AnimationControllerAsset fresh = AnimationControllerAsset.Load(path);
            HeadlessHarness.Assert(fresh.States.Count == 3 && fresh.Parameters.Count == 2 && fresh.DefaultState == "Idle" && fresh.Transitions.Count == 4,
                $"A new controller has {fresh.States.Count} states, {fresh.Parameters.Count} parameters, {fresh.Transitions.Count} transitions, default '{fresh.DefaultState}'.");
            ResourceCatalog.Invalidate(project.RootPath);
            HeadlessHarness.Assert(ResourceCatalog.Resolve(project.RootPath, "Hero Animator", ResourceType.AnimationController) is { } found
                && Path.GetFullPath(found) == Path.GetFullPath(path), "A game could not find the controller by its name.");

            // The editor: a new state and a transition to it, undone and done again, then saved.
            using (var editor = new AnimationControllerEditorControl(path, project.RootPath))
            {
                AnimationControllerState state = editor.AddState(new System.Drawing.PointF(300, 300));
                state.Name = "Wave";
                AnimationControllerTransition? transition = editor.AddTransition("Idle", "Wave");
                HeadlessHarness.Assert(transition != null && editor.Asset.Transitions.Count == 5 && editor.IsDirty, "The editor did not add the transition.");
                editor.Undo();
                HeadlessHarness.Assert(editor.Asset.Transitions.Count == 4, "Undo did not take the transition away.");
                editor.Redo();
                HeadlessHarness.Assert(editor.Asset.Transitions.Count == 5, "Redo did not put the transition back.");
                editor.Asset.Transitions[^1].Condition = "wave";
                // A picture of the editor: the graph, the parameters and the selected state.
                editor.Size = new System.Drawing.Size(1100, 560);
                editor.CreateControl();
                editor.PerformLayout();
                using (var picture = new System.Drawing.Bitmap(1100, 560))
                {
                    editor.DrawToBitmap(picture, new System.Drawing.Rectangle(0, 0, 1100, 560));
                    picture.Save(Path.Combine(ctx.Captures, "animation-controller-editor.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                editor.Save();
                HeadlessHarness.Assert(!editor.IsDirty, "Saving left the editor marked changed.");
            }
            AnimationControllerAsset saved = AnimationControllerAsset.Load(path);
            HeadlessHarness.Assert(saved.States.Exists(s => s.Name == "Wave") && saved.Transitions.Exists(t => t.From == "Idle" && t.To == "Wave" && t.Condition == "wave"),
                "The saved controller lost the new state or transition.");

            // In a game: the default state, then transitions on the parameters.
            var graph = new AnimationController(null);
            saved.ApplyTo(graph);
            AnimationStateMachine machine = graph.Layers[0].StateMachine;
            string start = machine.CurrentState;
            graph.Parameters["speed"] = 2.0;
            graph.Advance(0.1f, 1f, null);
            string moving = machine.CurrentState;
            graph.Parameters["speed"] = 0.0;
            graph.Advance(0.1f, 1f, null);
            string stopped = machine.CurrentState;
            graph.Parameters["jump"] = true;
            graph.Advance(0.1f, 1f, null);
            string jumped = machine.CurrentState;
            HeadlessHarness.Assert(start == "Idle" && moving == "Move" && stopped == "Idle" && jumped == "Jump",
                $"The controller's states went {start} → {moving} → {stopped} → {jumped}, not Idle → Move → Idle → Jump.");
        });
    }
}
