using System.Drawing;
using System.Numerics;
using Genesis.Application.Editors.Image;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>A cancellable working copy. Only an accepted result crosses into the parent editor's undo operation.</summary>
public sealed partial class ModelRigWizardDialog : DpiAwareForm
{
    private readonly GModelAsset _source;
    private GModelAsset _draft;
    private readonly ModelRigViewportControl _preview;
    private readonly FlowLayoutPanel _fields = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(12) };
    private readonly FlowLayoutPanel _steps = new() { Dock = DockStyle.Top, Height = 46, Padding = new(8), WrapContents = false };
    private readonly Label _status = new() { Dock = DockStyle.Fill, Padding = new(12, 8, 6, 2), AutoEllipsis = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Bottom, Height = 4, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly Button _apply;
    private readonly GModelAnimationClip _working = new() { Name = "Wizard joint review", Fps = 30, Loop = false };
    private CancellationTokenSource? _operation;
    private bool _busy, _updating;
    private bool _cancelled;
    private string _selectedRole = "Pelvis";
    private ListBox? _landmarks;
    private bool _mirror, _bendMode, _dragging;
    private bool _showTravel;
    private Vector3 _dragOrigin, _dragNormal;
    private readonly HashSet<GModelMotionKind> _selectedMotions = [GModelMotionKind.Idle, GModelMotionKind.Walk, GModelMotionKind.Run];
    private GModelMotionRecipe _controls = new();
    public int CurrentStep { get; private set; }
    public bool IsBusy => _busy;
    public string StatusText => _status.Text;
    public ModelRigViewportControl Preview => _preview;
    public GModelRigWizardSetup Setup => _draft.RigWizard!;
    public GModelAsset Result => ModelPoseWorkflow.Copy(_draft);
    public string SelectedClip { get; private set; } = "";

    public ModelRigWizardDialog(string resourcePath, string projectRoot, GModelAsset source)
    {
        _source = ModelPoseWorkflow.Copy(source); _draft = ModelPoseWorkflow.Copy(source);
        _draft.RigWizard = ModelRigWizardWorkflow.Suggest(source);
        if (Setup.Motions.LastOrDefault() is { } savedRecipe)
        {
            _controls = ModelPoseWorkflow.Copy(savedRecipe); _selectedMotions.Clear(); _selectedMotions.Add(savedRecipe.Kind);
            SelectedClip = _draft.Animations.FirstOrDefault(c => c.WizardRecipeId == savedRecipe.Id)?.Name ?? "";
        }
        Text = "Templates · Guided rigging and animation";
        ClientSize = new(1440, 920); MinimumSize = new(1080, 740); StartPosition = FormStartPosition.CenterParent;
        BackColor = EditorChrome.Canvas; ForeColor = EditorChrome.Text; Font = EditorChrome.BaseFont;
        _preview = new(resourcePath, projectRoot); _preview.ConfigureAnimationDraft(); _preview.PoseEditingEnabled = false;
        _preview.PreviewWorldTransform = time =>
        {
            if (!_showTravel || CurrentStep < 3 || _preview.RiggedAsset is not { } model) return Matrix4x4.Identity;
            var clip = model.Animations.FirstOrDefault(c => c.Name == _preview.ActiveClip);
            var recipe = model.RigWizard?.Motions.FirstOrDefault(r => r.Id == clip?.WizardRecipeId);
            return recipe is null ? Matrix4x4.Identity : ModelRigWizardMotions.Sample(model, recipe, time).VirtualTransform;
        };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.ColumnStyles.Add(new(SizeType.Absolute, 380));
        body.RowStyles.Add(new(SizeType.Percent, 100)); body.Controls.Add(_preview, 0, 0); body.Controls.Add(_fields, 1, 0);
        _fields.BackColor = EditorChrome.Surface;
        string[] titles = ["1 · Choose body", "2 · Orient", "3 · Detect and review", "4 · Bind", "5 · Animate"];
        for (int i = 0; i < titles.Length; i++) { int step = i; _steps.Controls.Add(ActionButton(titles[i], () => GoToStep(step))); }
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 66, BackColor = EditorChrome.Raised };
        _apply = ActionButton("Use Rig", ApplyWizard); _apply.Name = "ApplyRigWizard"; _apply.Dock = DockStyle.Right; _apply.Width = 150;
        var cancel = ActionButton("Cancel work", CancelWork); cancel.Name = "CancelRigWizard"; cancel.Dock = DockStyle.Right; cancel.Width = 125;
        footer.Controls.Add(_status); footer.Controls.Add(cancel); footer.Controls.Add(_apply); footer.Controls.Add(_progress);
        Controls.Add(body); Controls.Add(_steps); Controls.Add(footer);
        _preview.Viewport.DrawOverlay += DrawReview;
        _preview.Viewport.Host.MouseDown += ReviewDown;
        _preview.Viewport.Host.MouseMove += ReviewMove;
        _preview.Viewport.Host.MouseUp += ReviewUp;
        FormClosing += (_, _) => { _cancelled = true; _operation?.Cancel(); };
        _preview.Viewport.ViewChanged += () => _preview.Viewport.CameraOverrideFactory = null;
        Shown += (_, _) => _preview.FrameModelForTest();
        GoToStep(0); AdoptPreview();
    }

    public void GoToStep(int step)
    {
        if (_busy) return;
        if (step >= 3 && Setup.Joints.Count == 0) throw new InvalidOperationException("Detect and review the joints first.");
        CurrentStep = Math.Clamp(step, 0, 4); _preview.PauseAnimation();
        _updating = true;
        try
        {
            foreach (Control old in _fields.Controls.Cast<Control>().ToArray()) old.Dispose();
            _fields.Controls.Clear(); _landmarks = null;
            for (int i = 0; i < _steps.Controls.Count; i++) _steps.Controls[i].BackColor = i == CurrentStep ? EditorChrome.Hover : EditorChrome.Raised;
            switch (CurrentStep) { case 0: BodyPage(); break; case 1: OrientPage(); break; case 2: ReviewPage(); break; case 3: BindPage(); break; default: MotionPage(); break; }
        }
        finally { _updating = false; }
        _apply.Enabled = Setup.Joints.Count > 0 && _draft.Rig.IsValid; AdoptPreview();
    }

    public async Task DetectAsync()
    {
        var options = ModelPoseWorkflow.Copy(Setup);
        // Always detect from the original asset. A previous fresh draft must not masquerade as an imported rig.
        await RunOperation((token, progress) => ModelRigWizardWorkflow.Detect(_source, options, token, progress), 2);
    }
    public Task BindAsync() => RunOperation((token, progress) => ModelRigWizardWorkflow.Bind(_draft, token, progress), 3);
    public Task GenerateAsync(IEnumerable<GModelMotionRecipe> recipes, bool copyEdited = false)
    {
        var requested = recipes.Select(ModelPoseWorkflow.Copy).ToArray();
        return RunOperation((token, progress) => ModelRigWizardMotions.Generate(_draft, requested, copyEdited, token, progress), 4);
    }
    private async Task RunOperation(Func<CancellationToken, IProgress<string>, GModelAsset> operation, int next)
    {
        if (_busy) return;
        _ = Handle;
        _busy = true; _fields.Enabled = _steps.Enabled = _apply.Enabled = false; _progress.Visible = true;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        var progress = new Progress<string>(text => { if (!cancellation.IsCancellationRequested) _ = OnUi(() => _status.Text = text); });
        try
        {
            var completed = await Task.Run(() => operation(cancellation.Token, progress), cancellation.Token);
            if (cancellation.IsCancellationRequested || IsDisposed || _cancelled) return;
            await OnUi(() =>
            {
                _draft = completed;
                SelectedClip = _draft.Animations.LastOrDefault(c => c.WizardRecipeId.Length > 0)?.Name ?? "";
                _status.Text = next == 2 ? "Drag a joint to adjust it independently. Amber placements are suggestions to check. Use Rig returns to the normal rigging controls."
                    : next == 3 ? "Binding complete. Preview a gentle test, or return to joint placement." : "Clips generated. Preview them below, then Use Rig.";
            });
        }
        finally
        {
            _operation = null; _busy = false;
            await OnUi(() => { _fields.Enabled = _steps.Enabled = true; _progress.Visible = false; GoToStep(next); });
        }
    }
    private Task OnUi(Action action)
    {
        if (IsDisposed || _cancelled) return Task.CompletedTask;
        if (InvokeRequired) return InvokeAsync(() => { if (!IsDisposed && !_cancelled) action(); });
        action(); return Task.CompletedTask;
    }
    public void CancelWork() { _operation?.Cancel(); DialogResult = DialogResult.Cancel; Close(); }
    public void ApplyWizard()
    {
        if (_busy) return;
        if (Setup.Joints.Count == 0 || !_draft.Rig.IsValid) throw new InvalidOperationException("Detect a rig first.");
        ModelPoseWorkflow.ValidatePose(_draft, ModelPoseWorkflow.BindPose(_draft));
        DialogResult = DialogResult.OK; Close();
    }
    private void AdoptPreview()
    {
        var preview = ModelPoseWorkflow.Copy(_draft);
        _preview.PreviewRigLayout = CurrentStep <= 2; _preview.ShowBones = CurrentStep >= 2;
        string clip = SelectedClip;
        if (CurrentStep == 2 && preview.Rig.IsValid)
        {
            _working.Frames = [new() { LocalBoneTransforms = ModelPoseWorkflow.BindPose(preview) }];
            preview.Animations.Add(_working); clip = _working.Name;
        }
        _preview.AdoptAnimationAsset(preview, clip);
    }
    private Button ActionButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, Padding = new(6, 2, 6, 2), BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text };
        button.FlatAppearance.BorderColor = EditorChrome.Border;
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError(ex); } }; return button;
    }
    private Button AsyncButton(string text, Func<Task> action)
    {
        var button = ActionButton(text, () => { });
        button.Click += async (_, _) => { try { await action(); } catch (OperationCanceledException) { } catch (Exception ex) { if (!IsDisposed) ShowError(ex); } }; return button;
    }
    private void ShowError(Exception ex) { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "Rigging wizard", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    private void Heading(string text) => _fields.Controls.Add(new Label { Text = text, Width = 332, Height = 32, Font = new Font(Font, FontStyle.Bold) });
    private void Hint(string text) => _fields.Controls.Add(new Label { Text = text, Width = 332, AutoSize = false, Height = Math.Max(42, (text.Length / 43 + 1) * 19), ForeColor = EditorChrome.Muted });
    private void Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { Width = 332, AutoSize = true, WrapContents = true, Margin = new(0, 3, 0, 5) };
        row.Controls.AddRange(controls); _fields.Controls.Add(row);
    }
    private ComboBox Choice(IEnumerable<string> values, int index, Action<int> changed)
    {
        var combo = new ComboBox { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text, FlatStyle = FlatStyle.Flat };
        combo.Items.AddRange(values.Cast<object>().ToArray()); combo.SelectedIndex = Math.Clamp(index, -1, combo.Items.Count - 1);
        combo.SelectedIndexChanged += (_, _) => { if (!_updating && combo.SelectedIndex >= 0) changed(combo.SelectedIndex); }; return combo;
    }
    private void Number(string label, float value, decimal min, decimal max, Action<float> changed, int decimals = 2)
    {
        var input = new NumericUpDown { Width = 110, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals == 0 ? 1 : .01m, Value = Math.Clamp((decimal)value, min, max), BackColor = EditorChrome.Raised, ForeColor = EditorChrome.Text };
        input.ValueChanged += (_, _) => { if (!_updating) changed((float)input.Value); };
        Row(new Label { Text = label, Width = 190, Height = 28, Padding = new(0, 5, 0, 0) }, input);
    }
    private CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true, MaximumSize = new(328, 0) };
        box.CheckedChanged += (_, _) => { if (!_updating) changed(box.Checked); }; return box;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { if (_dragging) { CancelReviewDrag(); } else CancelWork(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
