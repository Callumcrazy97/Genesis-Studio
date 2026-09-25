using System.Drawing;
using System.Numerics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json;

namespace Genesis.Application.Headless.Suites;

internal static partial class ModelRigWizardSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Editor.Model.RigWizard");
        Editing(ctx);
        foreach (var body in Enum.GetValues<GModelBodyPlan>())
        foreach (int arms in body == GModelBodyPlan.Humanoid ? new[] { 1, 2, 4, 8 } : new[] { 1 })
        {
            HeadlessHarness.RunCase(ctx.Report, $"Editor.Model.RigWizard.{body}.{arms}ArmPairs.FitBindAndMotion", () =>
            {
                var source = Fixture(body, arms); var before = JsonConvert.SerializeObject(source);
                var options = new GModelRigWizardSetup { Body = body, ArmPairs = arms, ReuseExistingRig = false, OrientationConfirmed = true };
                var detected = ModelRigWizardWorkflow.Detect(source, options);
                int legs = body == GModelBodyPlan.Humanoid ? 2 : body == GModelBodyPlan.Quadruped ? 4 : body == GModelBodyPlan.Insect ? 6 : 8;
                Assert(detected.RigWizard.Chains.Count(c => c.Kind == GModelLimbKind.Leg) == legs, "Wrong leg topology.");
                Assert(detected.RigWizard.Chains.Count(c => c.Kind == GModelLimbKind.Arm) == (body == GModelBodyPlan.Humanoid ? arms * 2 : 0), "Wrong arm topology.");
                foreach (var chain in detected.RigWizard.Chains)
                {
                    var end = detected.RigWizard.Joints.Single(j => j.Role == chain.Roles[^1]);
                    Assert(Math.Sign(end.Position.X) == chain.Side, "Limb paired across the symmetry plane: " + chain.Name);
                    if(chain.Kind==GModelLimbKind.Arm)
                    {
                        var mesh=source.Meshes.Single(m=>m.Name==$"Arm{chain.Pair+1}.{chain.Side}");var bounds=ModelPartBuilder.Bounds(mesh.Vertices);
                        Assert(Math.Abs(end.Position.Y-(bounds.Min.Y+bounds.Max.Y)*.5f)<.065f,"An arm was fitted to the wrong pair: "+chain.Name);
                    }
                }
                Review(detected);
                var bound = ModelRigWizardWorkflow.Bind(detected);
                Assert(bound.Meshes.All(m => m.IsSkinned) && bound.Rig.TemplateName.Length == 0, "Fresh wizard rig did not bind independently of legacy templates.");
                Neutral(bound);
                var recipes = Enum.GetValues<GModelMotionKind>().Select(kind => new GModelMotionRecipe { Kind = kind, Name = kind.ToString(), BodySway = 0 }).ToArray();
                var animated = ModelRigWizardMotions.Generate(bound, recipes);
                foreach (var recipe in recipes) Measure(animated, recipe);
                Assert(JsonConvert.SerializeObject(source) == before, "Fitting or generation changed the source.");
                if (body != GModelBodyPlan.Humanoid || arms == 2) Capture(ctx, animated, $"wizard-{body.ToString().ToLowerInvariant()}-{arms}", 4);
            });
        }
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.PinsRotationAndMissingInterior", () =>
        {
            var source = Fixture(GModelBodyPlan.Humanoid, 1);
            var options = new GModelRigWizardSetup { ReuseExistingRig = false, OrientationConfirmed = true };
            var detected = ModelRigWizardWorkflow.Detect(source, options);
            string role = "Arm1.Elbow.L"; var corrected = new Vector3(-.66f, 1.43f, .04f);
            ModelRigWizardWorkflow.EditLandmark(detected, role, corrected, mirror: true);
            var refit = ModelRigWizardWorkflow.Detect(source, detected.RigWizard);
            Assert(refit.RigWizard.Joints.Single(j => j.Role == role).Position == corrected, "Refit moved a pinned correction.");
            Assert(refit.RigWizard.Joints.Single(j => j.Role == "Arm1.Elbow.R").Position == new Vector3(-corrected.X, corrected.Y, corrected.Z), "Mirrored correction was not pinned.");
            var rotation = Matrix4x4.CreateRotationX(MathF.PI / 2);
            foreach (var mesh in source.Meshes) for (int i = 0; i < mesh.Vertices.Length; i++) mesh.Vertices[i].Position = Vector3.Transform(mesh.Vertices[i].Position, rotation);
            Bounds(source); options.Up = Vector3.UnitZ; options.Forward = -Vector3.UnitY;
            var rotated = ModelRigWizardWorkflow.Detect(source, options);
            var original = ModelRigWizardWorkflow.Detect(Fixture(GModelBodyPlan.Humanoid, 1), new() { ReuseExistingRig = false });
            for (int i = 0; i < original.RigWizard.Joints.Count; i++)
                Assert(Vector3.Distance(Vector3.Transform(original.RigWizard.Joints[i].Position, rotation), rotated.RigWizard.Joints[i].Position) < .06f, "Rotated reference fitting changed anatomy.");
            var flat = new GModelAsset(); var quad = ModelPartBuilder.BuildPrimitive(ModelPrimitiveKind.Quad, new RenderColor(1, 1, 1));
            flat.Meshes.Add(new() { Vertices = quad.Vertices, Indices = quad.Indices }); Bounds(flat);
            var missing = ModelRigWizardWorkflow.Detect(flat, new() { ReuseExistingRig = false, OrientationConfirmed = true });
            Assert(missing.RigWizard.Joints.Any(j => j.Issue.Length > 0), "Unsupported interior was silently accepted.");
            Reject(() => ModelRigWizardWorkflow.Bind(missing));
        });
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.RegenerateSaveUndoAndCancellation", () => Persistence(ctx));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.AccessoriesMissingLimbsAndBendPins", () => Accessories());
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.CompleteWizardWorkingCopyApply", () => WizardUi(ctx));
        HeadlessHarness.RunCase(ctx.Report,"Editor.Model.RigWizard.SegmentedTailAndAnimation",()=>
        {
            var source=Fixture(GModelBodyPlan.Quadruped,1);
            var tail=ModelPartBuilder.Bake([new(){Position=[0,.9f,-.95f],Scale=[.14f,.14f,.8f]}]);
            source.Meshes.Add(new(){Name="Tail",Vertices=tail.Vertices,Indices=tail.Indices});Bounds(source);
            var fit=ModelRigWizardWorkflow.Detect(source,new(){Body=GModelBodyPlan.Quadruped,TailSegments=3,ReuseExistingRig=false,OrientationConfirmed=true});Review(fit);
            var bound=ModelRigWizardWorkflow.Bind(fit);var recipe=new GModelMotionRecipe{BodySway=0};var generated=ModelRigWizardMotions.Generate(bound,[recipe]);Measure(generated,recipe);
            Assert(generated.RigWizard.Chains.Single(c=>c.Kind==GModelLimbKind.Tail).Roles.Count==4,"Tail segment topology is wrong.");
            var flipped=ModelPoseWorkflow.Copy(bound);flipped.RigWizard.Chains.Single(c=>c.Kind==GModelLimbKind.Tail).BendDirection=-Vector3.UnitY;
            var changed=ModelRigWizardMotions.Generate(flipped,[recipe]);
            Assert(ModelRigWizardMotions.Hash(generated.Animations[0])!=ModelRigWizardMotions.Hash(changed.Animations[0]),"Tail bend direction did not affect the generated motion.");
            var stopped=ModelRigWizardMotions.Sample(bound,new(){Intensity=0},.4f);
            Assert(stopped.Locals.SequenceEqual(ModelPoseWorkflow.BindPose(bound)),"Zero intensity changed the rest pose.");
        });
        string fox = Environment.GetEnvironmentVariable("GENESIS_MODEL_REVIEW_SOURCE") ?? @"F:\Development\Game Development\Shared\Models\Vulipet.glb";
        string archer = Path.Combine(Environment.GetEnvironmentVariable("GENESIS_ARCHER_SOURCE") ?? @"F:\Development\Game Development\Shared\Models\Mixamo\Characters\Archer", "Stand", "Erika Archer.dae");
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.VulipetFitAndReview", () => Real(ctx, fox, false));
        HeadlessHarness.RunCase(ctx.Report, "Editor.Model.RigWizard.ArcherReuseWeightsAndRestAxes", () => Real(ctx, archer, true));
    }
    private static void Accessories()
    {
        var source=Fixture(GModelBodyPlan.Humanoid,2);var baseline=ModelRigWizardWorkflow.Detect(source,new(){ArmPairs=2,ReuseExistingRig=false});
        var bag=ModelPartBuilder.Bake([new(){Position=[2.5f,1,0],Scale=[1,1,1]}]);
        source.Meshes.Add(new(){Name="Excluded bag",Vertices=bag.Vertices,Indices=bag.Indices});Bounds(source);
        var options=new GModelRigWizardSetup{ArmPairs=2,ReuseExistingRig=false,ExcludedMeshes=[source.Meshes.Count-1]};
        var excluded=ModelRigWizardWorkflow.Detect(source,options);
        Assert(excluded.RigWizard.Joints.Zip(baseline.RigWizard.Joints).All(p=>Vector3.Distance(p.First.Position,p.Second.Position)<1e-5f),"Excluded accessory changed the fitted shape.");
        excluded.RigWizard.Chains[0].BendDirection=Vector3.UnitX;
        var refit=ModelRigWizardWorkflow.Detect(source,excluded.RigWizard);
        Assert(refit.RigWizard.Chains[0].BendDirection==Vector3.UnitX,"Refit lost a corrected bend direction.");
        source=Fixture(GModelBodyPlan.Humanoid,1);source.Meshes.RemoveAll(m=>m.Name=="Arm1.-1");Bounds(source);
        var missing=ModelRigWizardWorkflow.Detect(source,new(){ReuseExistingRig=false,OrientationConfirmed=true});
        Assert(missing.RigWizard.Joints.Any(j=>j.Role.StartsWith("Arm",StringComparison.Ordinal)&&j.Issue.Length>0),"Missing arm was silently accepted as a complete body.");
        // Missing-limb suggestions remain visible without an acknowledgement gate.
        // Invalid mappings or coincident segments are still rejected by structural validation.
        foreach(var scale in new[]{new Vector3(1.6f,.8f,1.1f),new Vector3(.8f,1.5f,.7f)})
        {
            source=Fixture(GModelBodyPlan.Quadruped,1);
            foreach(var mesh in source.Meshes)for(int i=0;i<mesh.Vertices.Length;i++)mesh.Vertices[i].Position*=scale;
            Bounds(source);var fit=ModelRigWizardWorkflow.Detect(source,new(){Body=GModelBodyPlan.Quadruped,ReuseExistingRig=false,OrientationConfirmed=true});Review(fit);
            var bound=ModelRigWizardWorkflow.Bind(fit);Neutral(bound);
            foreach(var chain in bound.RigWizard.Chains.Where(c=>c.Kind==GModelLimbKind.Leg))
            {
                var mesh=source.Meshes.Single(m=>m.Name==$"Leg{chain.Pair+1}.{chain.Side}");
                int side=chain.Side;
                foreach(var v in bound.Meshes[source.Meshes.IndexOf(mesh)].SkinnedVertices)
                for(int slot=0;slot<4;slot++)if(v.JointWeights[slot]>.05f)
                {
                    string name=bound.Rig.Bones[(int)v.JointIndices[slot]].Name;
                    Assert(!name.EndsWith(side<0?".R":".L",StringComparison.Ordinal),"Skin weights crossed to the opposite limb.");
                }
            }
        }
    }
    private static void WizardUi(HeadlessContext ctx)
    {
        var source=Fixture(GModelBodyPlan.Humanoid,1);
        string path=ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot,ResourceKind.Model,"Complete rig wizard");StudioModelResourceLoader.SaveCanonical(path,source);
        using var editor=new ModelEditorControl(path,ctx.Project!.RootPath);
        using var parent=editor.CreateAnimationStudio();using var wizard=parent.CreateRigWizard();Show(wizard);
        wizard.Setup.Body=GModelBodyPlan.Humanoid;wizard.Setup.OrientationConfirmed=true;wizard.GoToStep(1);
        Editor3DInspectionSuite.Capture(ctx,wizard,"wizard-orient");
        Await(wizard.DetectAsync());Assert(wizard.CurrentStep==2,"Detect did not advance to review.");
        foreach(var j in wizard.Setup.Joints)j.Reviewed=true;
        Await(wizard.BindAsync());Assert(wizard.CurrentStep==3&&wizard.Setup.Bound,"Bind did not complete in the working copy.");
        var recipe=new GModelMotionRecipe{Name="Wizard walk",Intensity=.5f,BodySway=0};Await(wizard.GenerateAsync([recipe]));
        Assert(parent.Result.RigWizard is null,"Wizard changed parent before applying.");
        wizard.ApplyWizard();parent.ApplyRigWizardResult(wizard.Result,wizard.SelectedClip);
        Assert(parent.Result.Animations.Any(c=>c.Name==recipe.Name),"Accepted clips did not enter the parent timeline.");
        editor.ApplyAnimationWorkspace(parent.Result,parent.SelectedClip);editor.Undo();Assert(editor.RiggedAsset!.RigWizard is null,"Outer apply was not one undoable change.");
        editor.Redo();Assert(editor.AnimationFrameCount>1,"Redo did not restore generated playback.");
    }

    private static void Measure(GModelAsset asset, GModelMotionRecipe recipe)
    {
        var setup = asset.RigWizard; var rest = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, ModelPoseWorkflow.BindPose(asset));
        ModelWizardMotionSample? previous = null;
        for (int f = 0; f < 120; f++)
        {
            float time = recipe.Duration / recipe.Tempo * f / 120;
            var sample = ModelRigWizardMotions.Sample(asset, recipe, time);
            var world = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, sample.Locals);
            for (int i = 0; i < sample.Locals.Length; i++) if (asset.Rig.Bones[i].ParentIndex < 0)
                Assert(sample.Locals[i] == asset.Rig.Bones[i].BindLocal, "Generated root motion escaped into the clip.");
            foreach (var chain in setup.Chains)
            for (int j = 1; j < chain.Roles.Count; j++)
            {
                int a = setup.Joints.Single(x => x.Role == chain.Roles[j - 1]).BoneIndex, b = setup.Joints.Single(x => x.Role == chain.Roles[j]).BoneIndex;
                float length = Vector3.Distance(rest[a].Translation, rest[b].Translation);
                float actual = Vector3.Distance(world[a].Translation, world[b].Translation);
                Assert(Math.Abs(actual - length) / length < .001f, $"{recipe.Kind}: segment length changed in {chain.Name} ({actual}/{length}).");
            }
            if (previous is not null)
            foreach (var contact in sample.Contacts.Where(c => c.Planted))
            {
                var old = previous.Contacts.Single(c => c.Chain == contact.Chain);
                if (!old.Planted) continue;
                float drift = Vector3.Distance(Vector3.Transform(contact.Position, sample.VirtualTransform), Vector3.Transform(old.Position, previous.VirtualTransform));
                if (recipe.Kind is GModelMotionKind.Walk or GModelMotionKind.Run or GModelMotionKind.TurnLeft or GModelMotionKind.TurnRight)
                    Assert(drift / contact.LegLength < .01f, $"{recipe.Kind}: stance drift {drift / contact.LegLength:P3} in {contact.Chain}.");
            }
            previous = sample;
        }
    }
    private static void Persistence(HeadlessContext ctx)
    {
        var source = Fixture(GModelBodyPlan.Quadruped, 1);
        var detected = ModelRigWizardWorkflow.Detect(source, new() { Body = GModelBodyPlan.Quadruped, ReuseExistingRig = false, OrientationConfirmed = true }); Review(detected);
        var bound = ModelRigWizardWorkflow.Bind(detected);
        var recipe = new GModelMotionRecipe { BodySway = 0 };
        var generated = ModelRigWizardMotions.Generate(bound, [recipe]);
        Assert(ModelRigWizardWorkflow.Suggest(generated).ReuseExistingRig,"Reopening a fitted rig did not default to preserving it.");
        generated.Animations.Add(new() { Name = "Unrelated" });
        var edited = ModelPoseWorkflow.Copy(generated); edited.Animations[0].Frames[2].LocalBoneTransforms[1] *= Matrix4x4.CreateRotationY(.1f);
        Reject(() => ModelRigWizardMotions.Generate(edited, [recipe]));
        var copy = ModelRigWizardMotions.Generate(edited, [recipe], true);
        Assert(copy.Animations.Count == 3 && copy.Animations[0].Frames[2].LocalBoneTransforms.SequenceEqual(edited.Animations[0].Frames[2].LocalBoneTransforms), "Copy lost manually edited frames.");
        var regenerated = ModelRigWizardMotions.Generate(generated, [recipe]);
        Assert(regenerated.Animations.Count == 2 && regenerated.Animations.Any(c => c.Name == "Unrelated"), "Regeneration changed unrelated clips.");
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, "Wizard persistence");
        StudioModelResourceLoader.SaveCanonical(path, source);
        using var editor = new ModelEditorControl(path, ctx.Project!.RootPath);
        editor.ApplyAnimationWorkspace(regenerated, recipe.Name);
        Assert(editor.RiggedAsset!.RigWizard.Motions.Count == 1, "Apply lost recipes.");
        editor.Undo(); Assert(editor.RiggedAsset!.RigWizard is null, "Undo did not restore pre-wizard state.");
        editor.Redo(); editor.Save();
        var reopened = StudioModelResourceLoader.Load(path);
        Assert(reopened.RigWizard.Motions[0].Id == recipe.Id && reopened.RigWizard.Joints.Count == regenerated.RigWizard.Joints.Count, "Save/reopen lost typed wizard metadata.");
        Assert(ModelRigWizardMotions.Hash(reopened.Animations.Single(c => c.WizardRecipeId == recipe.Id)) == reopened.RigWizard.Motions[0].LastGeneratedHash, "Save/reopen changed generated identity or frames.");
        var legacyLabel=ModelPoseWorkflow.Copy(reopened);legacyLabel.Rig.TemplateName="Humanoid/Human";legacyLabel.Rig.RigVersion=0;
        var savedRig=JsonConvert.SerializeObject(legacyLabel.Rig);GModelPrimitiveFactory.EnsureRigIntegrity(legacyLabel);
        Assert(JsonConvert.SerializeObject(legacyLabel.Rig)==savedRig,"Wizard metadata entered the legacy template replacement path.");
        var before = JsonConvert.SerializeObject(source);
        using var wizard = new ModelRigWizardDialog(path, ctx.Project.RootPath, source); Show(wizard);
        wizard.Setup.Body = GModelBodyPlan.Quadruped;
        var task = wizard.DetectAsync(); wizard.CancelWork(); Await(task, allowCancelled: true);
        Assert(!wizard.IsBusy && JsonConvert.SerializeObject(source) == before, "Cancellation left a partial application or busy worker.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { ModelRigWizardWorkflow.Bind(bound, cancelled.Token); throw new Exception("Binding ignored cancellation."); } catch (OperationCanceledException) { }
    }
    private static void Real(HeadlessContext ctx, string source, bool reuse)
    {
        Assert(File.Exists(source), "Real model fixture is missing: " + source);
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, reuse ? "Wizard Archer" : "Wizard Vulipet");
        using var viewer = new ModelViewerControl(path, ctx.Project!.RootPath); viewer.ImportExternalModel(source);
        var asset = viewer.PreviewAsset; var before = JsonConvert.SerializeObject(asset.Rig);
        var weights = asset.Meshes.Select(m => (SkinnedMeshVertex[])m.SkinnedVertices.Clone()).ToArray();
        var options = ModelRigWizardWorkflow.Suggest(asset); options.Body = reuse ? GModelBodyPlan.Humanoid : GModelBodyPlan.Quadruped;
        options.ReuseExistingRig = reuse; options.OrientationConfirmed = true;
        var fit = ModelRigWizardWorkflow.Detect(asset, options);
        string log = string.Join("\n", fit.RigWizard.Joints.Select(j => $"{j.Role}: {j.BoneIndex} {j.Position} {j.Confidence:F2} {j.Issue}"));
        File.WriteAllText(Path.Combine(ctx.OutputRoot, reuse ? "wizard-archer-fit.txt" : "wizard-vulipet-fit.txt"), log);
        Capture(ctx, fit, reuse ? "wizard-archer-review" : "wizard-vulipet-review", 2);
        Review(fit); var bound = ModelRigWizardWorkflow.Bind(fit);
        if (reuse)
        {
            Assert(JsonConvert.SerializeObject(bound.Rig) == before, "Reuse changed imported indices, rest axes or inverse binds.");
            for (int i = 0; i < weights.Length; i++) Assert(bound.Meshes[i].SkinnedVertices.SequenceEqual(weights[i]), "Reuse changed imported weights.");
        }
        Neutral(bound, reuse ? asset : null);
        var recipes=Enum.GetValues<GModelMotionKind>().Select(kind=>new GModelMotionRecipe{Kind=kind,Name=kind.ToString()}).ToArray();
        var animated=ModelRigWizardMotions.Generate(bound,recipes);foreach(var recipe in recipes)Measure(animated,recipe);
        Capture(ctx, animated, reuse ? "wizard-archer-walk" : "wizard-vulipet-walk", 4);
    }
    private static void Capture(HeadlessContext ctx, GModelAsset asset, string name, int page)
    {
        string path = ctx.Resources!.CreateResource(ctx.Resources.AssetsRoot, ResourceKind.Model, name);
        StudioModelResourceLoader.SaveCanonical(path, asset);
        using var dialog = new ModelRigWizardDialog(path, ctx.Project!.RootPath, asset); Show(dialog); dialog.GoToStep(page);
        if (page == 4) { dialog.Preview.PlayClip(asset.Animations.FirstOrDefault(c=>c.Name=="Walk")?.Name??asset.Animations.Last().Name, false); dialog.Preview.SetAnimationTime(.45f); }
        dialog.Preview.Viewport.Camera.Yaw = MathF.PI; dialog.Preview.Viewport.Camera.Pitch = -.08f;
        Editor3DInspectionSuite.Pump(); using var frame = dialog.Preview.Viewport.CaptureFrame(3); Assert(frame is not null, "Wizard viewport did not render.");
        Editor3DInspectionSuite.Capture(ctx, dialog, name);
        if (name.Contains("quadruped", StringComparison.Ordinal)) { dialog.ClientSize = new Size(1080, 740); Editor3DInspectionSuite.Capture(ctx, dialog, name + "-narrow"); }
    }
    private static void Neutral(GModelAsset asset, GModelAsset? baseline = null)
    {
        var palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, ModelPoseWorkflow.BindPose(asset));
        var original = baseline is null ? null : GModelPrimitiveFactory.EvaluateSkinPalette(baseline.Rig, ModelPoseWorkflow.BindPose(baseline));
        foreach (var mesh in asset.Meshes) foreach (var vertex in mesh.SkinnedVertices)
        {
            Vector3 p = default;
            for (int i = 0; i < 4; i++) if (vertex.JointWeights[i] > 0) p += Vector3.Transform(vertex.Position, palette[(int)vertex.JointIndices[i]]) * vertex.JointWeights[i];
            var expected = vertex.Position;
            if (original is not null)
            {
                expected = default;
                for (int i = 0; i < 4; i++) if (vertex.JointWeights[i] > 0) expected += Vector3.Transform(vertex.Position, original[(int)vertex.JointIndices[i]]) * vertex.JointWeights[i];
            }
            Assert(Vector3.Distance(p, expected) < asset.Bounds.Size.Length() * .0001f, "Neutral skin changed source geometry.");
        }
    }
    private static void Review(GModelAsset asset)
    {
        // Fixtures are symmetric and inspected in captures. Real-fixture issues are retained in the text report.
        foreach (var j in asset.RigWizard.Joints) j.Reviewed = true;
    }
    internal static GModelAsset Fixture(GModelBodyPlan body, int arms)
    {
        var asset = new GModelAsset { Name = body.ToString() };
        bool human = body == GModelBodyPlan.Humanoid;
        Add("Torso", new(0, human ? 1.18f : .7f, 0), new(human ? .48f : .65f, human ? .6f : .4f, human ? .28f : 1.3f));
        Add("Neck", new(0, human ? 1.7f : 1.02f, human ? 0 : .6f), new(.18f, .4f, .18f));
        Add("Head", new(0, human ? 1.88f : 1.3f, human ? 0 : .65f), new(.35f, .3f, .35f));
        int pairs = human ? 1 : body == GModelBodyPlan.Quadruped ? 2 : body == GModelBodyPlan.Insect ? 3 : 4;
        for (int pair = 0; pair < pairs; pair++) foreach (int side in new[] { -1, 1 })
        {
            float z = human ? 0 : .48f - pair * .96f / (pairs - 1);
            float x = side * (human ? .19f : body == GModelBodyPlan.Quadruped ? .27f : .82f);
            if (!human && body != GModelBodyPlan.Quadruped) Add("Leg attachment", new(side * .52f, .66f, z), new(.66f, .14f, .13f));
            Add($"Leg{pair + 1}.{side}", new(x, human ? .46f : .32f, z), new(.14f, human ? .93f : .66f, .16f));
            Add("Foot", new(x, .05f, z + .045f), new(.16f, .1f, .25f));
        }
        if (human) for (int pair = 0; pair < arms; pair++) foreach (int side in new[] { -1, 1 })
            Add($"Arm{pair + 1}.{side}", new(side * .61f, 1.5f - pair * .115f, 0), new(.85f, .075f, .12f));
        Bounds(asset); return asset;
        void Add(string name, Vector3 center, Vector3 size)
        {
            var mesh = ModelPartBuilder.Bake([new() { Name = name, Position = [center.X, center.Y, center.Z], Scale = [size.X, size.Y, size.Z] }]);
            asset.Meshes.Add(new() { Name = name, Vertices = mesh.Vertices, Indices = mesh.Indices });
        }
    }
    private static void Bounds(GModelAsset asset)
    {
        var bounds = ModelPartBuilder.Bounds(asset.Meshes.SelectMany(m => m.Vertices).ToArray()); asset.Bounds = new() { Min = bounds.Min, Max = bounds.Max };
    }
    private static void Show(Form form) { ThemeService.Apply(form); UnattendedWindowing.Configure(form); UnattendedWindowing.ShowWithoutFocus(form); Editor3DInspectionSuite.Pump(); }
    private static void Await(Task task, bool allowCancelled = false)
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < until) { System.Windows.Forms.Application.DoEvents(); Thread.Sleep(5); }
        Assert(task.IsCompleted, "Wizard operation timed out.");
        try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (allowCancelled) { }
    }
    private static void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Invalid operation was accepted."); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
