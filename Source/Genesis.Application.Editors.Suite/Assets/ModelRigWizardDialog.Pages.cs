using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelRigWizardDialog
{
    private static readonly string[] AxisNames = ["+X", "−X", "+Y", "−Y", "+Z", "−Z"];
    private static readonly Vector3[] Axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    private void BodyPage()
    {
        Heading("CHOOSE A BODY");
        Hint("Choose the anatomy to fit. Check the suggested joints where limbs overlap or clothing hides the shape.");
        Row(Choice(Enum.GetNames<GModelBodyPlan>(), (int)Setup.Body, index => { Setup.Body = (GModelBodyPlan)index; InvalidateFit(); GoToStep(0); }));
        if (Setup.Body == GModelBodyPlan.Humanoid) Number("Arm pairs", Setup.ArmPairs, 1, 8, v => { Setup.ArmPairs = (int)v; InvalidateFit(); }, 0);
        else Hint(Setup.Body == GModelBodyPlan.Quadruped ? "Four legs" : Setup.Body == GModelBodyPlan.Insect ? "Six legs · tripod gait" : "Eight legs · alternating groups");
        Number("Tail segments (0 = none)", Setup.TailSegments, 0, 8, v => { Setup.TailSegments = (int)v; InvalidateFit(); }, 0);
        var reuse = Check("Reuse existing rig and skin weights", Setup.ReuseExistingRig, value => { Setup.ReuseExistingRig = value; Setup.Bound=false; _apply.Enabled=false; });
        reuse.Enabled = _source.Rig.IsValid; Row(reuse);
        Hint(_source.Rig.IsValid ? $"Reuse keeps the existing skin and clips. Fresh fitting replaces {_source.Rig.Bones.Count} joints, {_source.Poses.Count} poses and {_source.Animations.Count} clips in this working copy. Cancel work discards it; editor Undo restores the previous model."
            : "A fresh rig is fitted to the model. Source vertices and resource transforms stay unchanged.");
        Row(ActionButton("Next · Orient", () => GoToStep(1)));
    }
    private void OrientPage()
    {
        Heading("ORIENT THE FITTING SPACE");
        Hint("Set the model's up and forward directions. These controls change the fitting reference only.");
        Row(new Label { Text = "Up", Width = 90 }, Choice(AxisNames, Array.IndexOf(Axes, Setup.Up), i => { Setup.Up = Axes[i]; AlignmentChanged(); }));
        Row(new Label { Text = "Forward", Width = 90 }, Choice(AxisNames, Array.IndexOf(Axes, Setup.Forward), i => { Setup.Forward = Axes[i]; AlignmentChanged(); }));
        Row(ActionButton("Flip forward", () => { Setup.Forward = -Setup.Forward; AlignmentChanged(); GoToStep(1); }),
            ActionButton("Rotate 90°", () => { Setup.Forward = Vector3.Cross(Setup.Up, Setup.Forward); AlignmentChanged(); GoToStep(1); }));
        Number("Ground (along up)", Setup.GroundHeight, -1000000, 1000000, v => { Setup.GroundHeight = v; Setup.OrientationConfirmed = false; InvalidateFit(); }, 4);
        Number("Symmetry plane offset", Setup.SymmetryOffset, -1000000, 1000000, v => { Setup.SymmetryOffset = v; Setup.OrientationConfirmed = false; InvalidateFit(); }, 4);
        Row(ActionButton("Front view", () => SetView(false)), ActionButton("Side view", () => SetView(true)));
        Heading("SHAPE DETECTION MESHES");
        Hint("Untick accessories such as weapons, bags or separate clothing. Excluded meshes are kept in the model and bound later.");
        var meshes = new CheckedListBox { Width = 328, Height = 150, CheckOnClick = true, IntegralHeight = false, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text };
        for (int i = 0; i < _source.Meshes.Count; i++) meshes.Items.Add($"{i + 1} · {_source.Meshes[i].Name}", !Setup.ExcludedMeshes.Contains(i));
        meshes.ItemCheck += (_, e) => { if (e.NewValue == CheckState.Checked) Setup.ExcludedMeshes.Remove(e.Index); else if (!Setup.ExcludedMeshes.Contains(e.Index)) Setup.ExcludedMeshes.Add(e.Index); InvalidateFit(); };
        _fields.Controls.Add(meshes);
        Row(AsyncButton("Detect joints", DetectAsync));
    }
    private void AlignmentChanged()
    {
        Setup.OrientationConfirmed = false; InvalidateFit();
        var vertices = _source.Meshes.Where((_, i) => !Setup.ExcludedMeshes.Contains(i)).SelectMany(ModelRigWizardSpace.Positions).ToArray();
        if (vertices.Length == 0 || Vector3.Cross(Setup.Up, Setup.Forward).LengthSquared() < .1f) return;
        Setup.GroundHeight = vertices.Min(v => Vector3.Dot(v, Setup.Up));
        var right = Vector3.Normalize(Vector3.Cross(Setup.Up, Setup.Forward));
        Setup.SymmetryOffset = (vertices.Min(v => Vector3.Dot(v, right)) + vertices.Max(v => Vector3.Dot(v, right))) * .5f;
    }
    private void InvalidateFit() { Setup.Joints.Clear(); Setup.Chains.Clear(); Setup.Bound = false; _apply.Enabled = false; }
    private void SetView(bool side)
    {
        var direction = -(side ? Vector3.Normalize(Vector3.Cross(Setup.Up, Setup.Forward)) : Setup.Forward);
        _preview.Viewport.Camera.Yaw = MathF.Atan2(direction.X, direction.Z);
        _preview.Viewport.Camera.Pitch = -MathF.Asin(Math.Clamp(direction.Y, -.999f, .999f));
        _preview.Viewport.CameraOverrideFactory = () =>
        {
            var camera = _preview.Viewport.Camera; var eye = camera.Target - direction * camera.Distance;
            return new(Matrix4x4.CreateLookAt(eye, camera.Target, Setup.Up), Matrix4x4.CreatePerspectiveFieldOfView(
                _preview.Viewport.FieldOfViewDegrees * MathF.PI / 180, _preview.Viewport.SurfaceWidth / (float)_preview.Viewport.SurfaceHeight,
                _preview.Viewport.NearPlane, _preview.Viewport.FarPlane), eye, direction);
        };
        _status.Text = side ? "Side view · check limb depth and bend directions." : "Front view · check left/right pairs and symmetry.";
    }
    private void ReviewPage()
    {
        Heading("DETECT AND REVIEW");
        if (Setup.Joints.Count == 0) { Hint("Set the orientation, then detect a rig."); Row(AsyncButton("Detect joints", DetectAsync)); return; }
        Hint(Setup.ReuseExistingRig ? "Review the bone mapping. Amber entries are suggestions to check; choose another bone if needed. Imported rest shape is preserved."
            : "Drag a joint to adjust only that joint. Mirroring is optional. Amber entries are suggestions to check. Bend handles set how knees and elbows fold.");
        Row(ActionButton("Front", () => SetView(false)), ActionButton("Side", () => SetView(true)), AsyncButton("Refit unpinned", DetectAsync));
        _landmarks = new ListBox { Name = "RigWizardLandmarks", Width = 328, Height = 220, IntegralHeight = false, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text };
        foreach (var j in Setup.Joints) _landmarks.Items.Add((j.Issue.Length > 0 && !j.Reviewed ? "! " : j.Pinned ? "● " : "✓ ") + j.Role);
        _landmarks.SelectedIndex = Math.Max(0, Setup.Joints.FindIndex(j => j.Role == _selectedRole));
        _selectedRole = Setup.Joints[_landmarks.SelectedIndex].Role;
        _landmarks.SelectedIndexChanged += (_, _) => { if (!_updating && _landmarks.SelectedIndex >= 0) { _selectedRole = Setup.Joints[_landmarks.SelectedIndex].Role; GoToStep(2); } };
        _fields.Controls.Add(_landmarks);
        var joint = Setup.Joints.Single(j => j.Role == _selectedRole);
        Hint(joint.Issue.Length == 0 ? joint.Role + " · review placement and depth" : joint.Role + " · " + joint.Issue);
        if (Setup.ReuseExistingRig)
        {
            Row(Choice(_draft.Rig.Bones.Select((b, i) => $"{i} · {b.Name}"), joint.BoneIndex, i =>
            {
                var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(_draft.Rig.Bones, ModelPoseWorkflow.BindPose(_draft));
                joint.BoneIndex = i; joint.Position = worlds[i].Translation; joint.Pinned = joint.Reviewed = true; Setup.Bound = false; GoToStep(2);
            }));
        }
        else
        {
            var pin = Check("Pin joint", joint.Pinned, v => joint.Pinned = v);
            for (int axis = 0; axis < 3; axis++)
            {
                int component = axis;
                Number("XYZ"[axis].ToString(), joint.Position[axis], -1000000, 1000000, value =>
                {
                    var p = joint.Position; p[component] = value; ModelRigWizardWorkflow.EditLandmark(_draft, joint.Role, p, _mirror); pin.Checked = joint.Pinned; AdoptPreview();
                }, 4);
            }
            Row(Check("Mirror corrections", _mirror, v => _mirror = v), pin);
        }
        var chain = Setup.Chains.FirstOrDefault(c => c.Roles.Contains(_selectedRole));
        if (chain is not null)
        {
            Row(Check("Edit bend handle", _bendMode, v => _bendMode = v), ActionButton("Flip bend", () => chain.BendDirection = -chain.BendDirection));
            Hint("Orange handle: drag in the view plane to set the bend direction. Use a side view to check it.");
        }
        Row(AsyncButton("Bind To Mesh", BindAsync));
    }
    private void BindPage()
    {
        Heading("BIND AND TEST");
        Hint(Setup.Bound ? "Skin is bound. Try a restrained walk before creating clips. Return to joint placement at any time." : "Bind To Mesh attaches the rig. Use Rig also lets you continue with the normal rigging controls without binding here.");
        Row(AsyncButton("Bind To Mesh", BindAsync), ActionButton("Back to joints", () => GoToStep(2)));
        if (Setup.Bound)
        {
            Row(Check("Show virtual travel in preview", _showTravel, v => _showTravel = v));
            Row(AsyncButton("Gentle movement test", async () =>
            {
                var test = new GModelMotionRecipe { Name = "Gentle movement test", Intensity = .25f, Stride = .2f, BodySway = 0 };
                GModelAsset? preview = null;
                await RunOperation((token, progress) => { preview = ModelRigWizardMotions.Generate(_draft, [test], false, token, progress); return _draft; }, 3);
                if (preview is not null && !IsDisposed) { _preview.AdoptAnimationAsset(preview, test.Name); _preview.PlayClip(test.Name); }
            }), ActionButton("Stop test", AdoptPreview));
            Row(ActionButton("Next · Animate", () => GoToStep(4)));
        }
    }
    private void MotionPage()
    {
        Heading("GENERATE IN-PLACE ANIMATIONS");
        Hint("Choose movements and adjust their recipe. Generated clips appear in the normal animation dropdown and timeline.");
        if (!Setup.Bound) { Hint("Bind the reviewed rig first."); Row(ActionButton("Back to binding", () => GoToStep(3))); return; }
        if (Setup.Motions.Count > 0)
        {
            var recipes = Setup.Motions.ToArray();
            Row(Choice(recipes.Select(r => r.Name), Array.FindIndex(recipes, r => r.Id == _controls.Id), i =>
            { _controls = ModelPoseWorkflow.Copy(recipes[i]); _selectedMotions.Clear(); _selectedMotions.Add(_controls.Kind); GoToStep(4); }));
        }
        var kinds = Enum.GetValues<GModelMotionKind>();
        var choose = new CheckedListBox { Width = 328, Height = 128, CheckOnClick = true, IntegralHeight = false, BackColor = EditorChrome.Canvas, ForeColor = EditorChrome.Text };
        foreach (var kind in kinds) choose.Items.Add(kind.ToString(), _selectedMotions.Contains(kind));
        choose.ItemCheck += (_, e) => { if (e.NewValue == CheckState.Checked) _selectedMotions.Add(kinds[e.Index]); else _selectedMotions.Remove(kinds[e.Index]); };
        _fields.Controls.Add(choose);
        Row(AsyncButton("Preview / generate clips", GenerateSelected));
        Row(Check("Show virtual travel in preview", _showTravel, v => _showTravel = v));
        Number("Tempo", _controls.Tempo, .25m, 4, v => _controls.Tempo = v);
        Slider(_controls.Tempo, .25f, 4, v => { _controls.Tempo = v; });
        Number("Duration (seconds)", _controls.Duration, .25m, 20, v => _controls.Duration = v);
        Number("Stride / leg length", _controls.Stride, 0, .8m, v => _controls.Stride = v);
        Slider(_controls.Stride, 0, .8f, v => { _controls.Stride = v; });
        Number("Step height / leg length", _controls.StepHeight, 0, .4m, v => _controls.StepHeight = v);
        Number("Stance width", _controls.StanceWidth, .7m, 1.3m, v => _controls.StanceWidth = v);
        Number("Body sway", _controls.BodySway, 0, .1m, v => _controls.BodySway = v);
        Number("Intensity", _controls.Intensity, 0, 1, v => _controls.Intensity = v);
        Slider(_controls.Intensity, 0, 1, v => { _controls.Intensity = v; });
        Number("Crouch depth / leg length", _controls.CrouchDepth, 0, .5m, v => _controls.CrouchDepth = v);
        Number("Jump height / leg length", _controls.JumpHeight, 0, .6m, v => _controls.JumpHeight = v);
        Row(AsyncButton("Preview / generate clips", GenerateSelected));
        Row(ActionButton("Play", () => _preview.PlayClip(SelectedClip)), ActionButton("Stop", () => _preview.PlayClip(SelectedClip, false)));
        Hint("The root stays fixed. Walk and run use a virtual travel path for foot contacts; it is not saved as root motion.");
    }
    private void Slider(float value, float min, float max, Action<float> changed)
    {
        var row = _fields.Controls.OfType<FlowLayoutPanel>().Last(); var number = row.Controls.OfType<NumericUpDown>().Single();
        var slider = new TrackBar { Width = 320, Height = 30, Minimum = 0, Maximum = 100, TickStyle = TickStyle.None, Value = (int)Math.Clamp((value - min) / (max - min) * 100, 0, 100), AutoSize = false };
        bool syncing=false;
        slider.ValueChanged += (_, _) =>
        {
            if(syncing)return;syncing=true;
            try{number.Value=Math.Clamp(Math.Round((decimal)(min+(max-min)*slider.Value/100),number.DecimalPlaces),number.Minimum,number.Maximum);changed((float)number.Value);}
            finally{syncing=false;}
        };
        number.ValueChanged += (_, _) =>
        {
            if(syncing)return;syncing=true;
            try{slider.Value=(int)Math.Clamp(Math.Round(((float)number.Value-min)/(max-min)*100),0,100);}
            finally{syncing=false;}
        };
        row.Controls.Add(slider);
    }
    private async Task GenerateSelected()
    {
        if (_selectedMotions.Count == 0) throw new InvalidOperationException("Choose at least one movement.");
        var recipes = _selectedMotions.Select(kind =>
        {
            var recipe = ModelPoseWorkflow.Copy(_controls); recipe.Kind = kind;
            var existing = Setup.Motions.FirstOrDefault(r => r.Kind == kind && (r.Id == _controls.Id || r.Name == kind.ToString()));
            recipe.Id = existing?.Id ?? Guid.NewGuid().ToString("N"); recipe.Name = existing?.Name ?? kind.ToString(); return recipe;
        }).ToArray();
        try { await GenerateAsync(recipes); }
        catch (ModelWizardEditedClipException ex)
        {
            if (MessageBox.Show(this, ex.Message + "\n\nCreate a new copy?", "Keep edited animation", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await GenerateAsync(recipes, true);
        }
        if (!IsDisposed) _preview.PlayClip(SelectedClip);
    }
}
