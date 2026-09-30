using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Core.UI;

/// <summary>One numbered step of an editor's guided workflow.</summary>
/// <param name="Id">Stable identifier; the step button is named <c>WorkflowStep_&lt;Id&gt;</c>.</param>
/// <param name="Title">Short verb-first caption shown on the bar ("Shape", "Tune", "Use in game").</param>
/// <param name="Instruction">One plain sentence telling the user what to do in this step.</param>
/// <param name="Open">Brings the editor to this step (switches page, mode or panel).</param>
public sealed record WorkflowStep(string Id, string Title, string Instruction, Action Open)
{
    /// <summary>Optional completion test; completed steps show a check mark.</summary>
    public Func<bool>? IsDone { get; init; }
}

/// <summary>
/// The guided-workflow bar every editor shows under its command bar: numbered, clickable steps,
/// the current step's instruction, and a Next button.
/// </summary>
/// <remarks>
/// It replaces the one-line grey "1. … 2. … 3." hint each editor used to build for itself, which
/// was small, truncated in narrow windows and could not be clicked. The steps are real focusable
/// controls (Tab, Enter, Space) with accessible names, so the bar is also how keyboard and
/// screen-reader users find their way through an editor.
/// </remarks>
public sealed class WorkflowBar : Panel
{
    public const int LogicalHeight = 40;

    private readonly List<WorkflowStep> _steps;
    private readonly List<WorkflowStepButton> _buttons = [];
    private readonly Label _instruction;
    private readonly WorkflowNextButton _next;
    private readonly ToolTip _tips = new() { ShowAlways = true };
    private string? _instructionOverride;
    private bool _numbersOnly;

    public WorkflowBar(string name, IEnumerable<WorkflowStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToList();
        if (_steps.Count == 0)
        {
            throw new ArgumentException("A workflow needs at least one step.", nameof(steps));
        }

        if (_steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != _steps.Count)
        {
            throw new ArgumentException("Workflow step ids must be unique.", nameof(steps));
        }

        Name = name;
        Dock = DockStyle.Top;
        Height = LogicalHeight;
        DoubleBuffered = true;
        Tag = "surface";
        AccessibleRole = AccessibleRole.ToolBar;
        AccessibleName = "Workflow steps";
        BackColor = UiTokens.Surface;

        _instruction = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Name = name + "Instruction",
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
            AccessibleName = "Current step instruction",
        };
        Controls.Add(_instruction);

        for (int i = 0; i < _steps.Count; i++)
        {
            WorkflowStepButton button = new(this, _steps[i], i + 1)
            {
                ShowChevron = i < _steps.Count - 1,
            };
            _buttons.Add(button);
            Controls.Add(button);
        }

        _next = new WorkflowNextButton(this) { Name = "WorkflowNext" };
        Controls.Add(_next);

        CurrentStepId = _steps[0].Id;
        ApplyTokens();
        UpdateCurrentState();
        UiTokens.Changed += OnTokensChanged;
        Disposed += (_, _) =>
        {
            UiTokens.Changed -= OnTokensChanged;
            _tips.Dispose();
        };
    }

    /// <summary>Raised after a step is activated by the user or <see cref="Activate"/>.</summary>
    public event EventHandler<WorkflowStep>? StepActivated;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<WorkflowStep> Steps => _steps;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CurrentStepId { get; private set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public WorkflowStep CurrentStep => _steps.First(step => step.Id == CurrentStepId);

    /// <summary>The instruction currently shown (the override, or the step's own sentence).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string InstructionText => _instruction.Text;

    /// <summary>True when the instruction is hidden because the bar is too narrow.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsCompact => !_instruction.Visible;

    public Control StepButton(string id) => _buttons.First(button => button.Step.Id == id);

    /// <summary>Marks a step as current without running its action (the editor already moved).</summary>
    public void SetCurrent(string id)
    {
        if (_steps.All(step => step.Id != id) || id == CurrentStepId && _instructionOverride is null)
        {
            if (_steps.Any(step => step.Id == id))
            {
                RefreshProgress();
            }

            return;
        }

        CurrentStepId = id;
        _instructionOverride = null;
        UpdateCurrentState();
    }

    /// <summary>Runs a step's action and makes it current.</summary>
    public bool Activate(string id)
    {
        WorkflowStep? step = _steps.FirstOrDefault(candidate => candidate.Id == id);
        if (step is null)
        {
            return false;
        }

        CurrentStepId = id;
        _instructionOverride = null;
        UpdateCurrentState();
        step.Open();
        StepActivated?.Invoke(this, step);
        RefreshProgress();
        return true;
    }

    /// <summary>Moves to the step after the current one, if there is one.</summary>
    public bool GoNext()
    {
        int index = _steps.FindIndex(step => step.Id == CurrentStepId);
        return index >= 0 && index < _steps.Count - 1 && Activate(_steps[index + 1].Id);
    }

    /// <summary>Temporarily replaces the instruction (for example a mode-specific tip); null restores it.</summary>
    public void SetInstruction(string? text)
    {
        _instructionOverride = string.IsNullOrWhiteSpace(text) ? null : text;
        UpdateInstruction();
    }

    /// <summary>Re-evaluates every step's completion test.</summary>
    public void RefreshProgress()
    {
        foreach (WorkflowStepButton button in _buttons)
        {
            bool done;
            try
            {
                done = button.Step.IsDone?.Invoke() ?? false;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException)
            {
                done = false;
            }

            if (button.IsDone != done)
            {
                button.IsDone = done;
                button.Invalidate();
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_buttons.Count == 0)
        {
            return;
        }

        int padLeft = Scale(8);
        int padRight = Scale(6);
        int top = Scale(4);
        int height = Math.Max(1, ClientSize.Height - top * 2 - 1);
        int nextWidth = _next.Visible ? _next.PreferredWidth() : 0;
        int fullSteps = _buttons.Sum(button => button.PreferredWidth(numberOnly: false));
        int available = ClientSize.Width - padLeft - padRight - nextWidth;
        int minimumInstruction = Scale(200);

        bool showInstruction = available - fullSteps >= minimumInstruction;
        bool numbersOnly = !showInstruction && fullSteps > available;
        if (numbersOnly != _numbersOnly)
        {
            _numbersOnly = numbersOnly;
            foreach (WorkflowStepButton button in _buttons)
            {
                button.NumberOnly = numbersOnly && !button.IsCurrent;
                button.Invalidate();
            }
        }

        int x = padLeft;
        foreach (WorkflowStepButton button in _buttons)
        {
            button.NumberOnly = _numbersOnly && !button.IsCurrent;
            int width = button.PreferredWidth(button.NumberOnly);
            button.SetBounds(x, top, width, height);
            x += width;
        }

        if (_next.Visible)
        {
            _next.SetBounds(ClientSize.Width - padRight - nextWidth, top, nextWidth, height);
        }

        _instruction.Visible = showInstruction;
        int instructionLeft = x + Scale(10);
        int instructionRight = _next.Visible ? _next.Left - Scale(8) : ClientSize.Width - padRight;
        _instruction.SetBounds(instructionLeft, top, Math.Max(0, instructionRight - instructionLeft), height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using Pen rule = new(UiTokens.Blend(UiTokens.Border, UiTokens.Surface, 0.7f));
        e.Graphics.DrawLine(rule, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        PerformLayout();
    }

    internal int Scale(int logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    internal string NextCaption()
    {
        int index = _steps.FindIndex(step => step.Id == CurrentStepId);
        return index >= 0 && index < _steps.Count - 1 ? "Next: " + _steps[index + 1].Title : string.Empty;
    }

    private void UpdateCurrentState()
    {
        foreach (WorkflowStepButton button in _buttons)
        {
            button.IsCurrent = button.Step.Id == CurrentStepId;
            button.AccessibleDescription = button.Step.Instruction;
            _tips.SetToolTip(button, button.Step.Instruction);
            button.Invalidate();
        }

        _next.Visible = NextCaption().Length > 0;
        _next.AccessibleName = NextCaption();
        _next.Invalidate();
        UpdateInstruction();
        PerformLayout();
    }

    private void UpdateInstruction()
    {
        string text = _instructionOverride ?? CurrentStep.Instruction;
        _instruction.Text = text;
        _tips.SetToolTip(_instruction, text);
    }

    private void OnTokensChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyTokens();
        PerformLayout();
        Invalidate(true);
    }

    private void ApplyTokens()
    {
        BackColor = UiTokens.Surface;
        _instruction.Font = UiTokens.BaseFont;
        _instruction.ForeColor = UiTokens.Text;
    }

    /// <summary>One clickable step: numbered circle (or check), title, and a chevron to the next.</summary>
    private sealed class WorkflowStepButton : Control
    {
        private readonly WorkflowBar _owner;
        private bool _hovered;

        public WorkflowStepButton(WorkflowBar owner, WorkflowStep step, int number)
        {
            _owner = owner;
            Step = step;
            Number = number;
            Name = "WorkflowStep_" + step.Id;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = $"Step {number}: {step.Title}";
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        public WorkflowStep Step { get; }

        public int Number { get; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsCurrent { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsDone { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool NumberOnly { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowChevron { get; set; }

        [AllowNull]
        public override string Text
        {
            get => Step.Title;
            set { }
        }

        public int PreferredWidth(bool numberOnly)
        {
            int width = _owner.Scale(8) + Circle() + _owner.Scale(8);
            if (!numberOnly)
            {
                Font font = IsCurrent ? UiTokens.StrongFont : UiTokens.BaseFont;
                width += _owner.Scale(6) + TextRenderer.MeasureText(Step.Title, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            }

            return width + (ShowChevron ? _owner.Scale(18) : 0);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _owner.Activate(Step.Id);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int chevron = ShowChevron ? _owner.Scale(18) : 0;
            Rectangle pill = new(0, 0, Math.Max(1, Width - chevron), Height);
            if (IsCurrent || _hovered)
            {
                PillToolStripRenderer.FillRound(
                    g,
                    Rectangle.Inflate(pill, 0, -_owner.Scale(1)),
                    IsCurrent ? UiTokens.AccentSoft : UiTokens.Hover,
                    _owner.Scale(PillToolStripRenderer.PillRadius));
            }

            if (Focused && ShowFocusCues)
            {
                PillToolStripRenderer.StrokeRound(g, Rectangle.Inflate(pill, 0, -_owner.Scale(1)), UiTokens.Accent, _owner.Scale(PillToolStripRenderer.PillRadius));
            }

            int diameter = Circle();
            Rectangle circle = new(_owner.Scale(8), (Height - diameter) / 2, diameter, diameter);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            if (IsCurrent || IsDone)
            {
                using SolidBrush fill = new(IsCurrent ? UiTokens.Accent : UiTokens.Success);
                g.FillEllipse(fill, circle);
            }
            else
            {
                using Pen ring = new(UiTokens.Blend(UiTokens.Muted, UiTokens.Surface, 0.7f), Math.Max(1f, _owner.Scale(1)));
                g.DrawEllipse(ring, circle);
            }

            string mark = IsDone && !IsCurrent ? "✓" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Color markColour = IsCurrent || IsDone ? UiTokens.OnAccent : UiTokens.Muted;
            TextRenderer.DrawText(g, mark, UiTokens.SmallFont, circle, markColour,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            if (!NumberOnly)
            {
                Font font = IsCurrent ? UiTokens.StrongFont : UiTokens.BaseFont;
                Color text = IsCurrent || IsDone || _hovered ? UiTokens.Text : UiTokens.Muted;
                Rectangle title = new(circle.Right + _owner.Scale(6), 0, Math.Max(1, pill.Width - circle.Right - _owner.Scale(6)), Height);
                TextRenderer.DrawText(g, Step.Title, font, title, text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            if (ShowChevron)
            {
                Rectangle arrow = new(Width - chevron, 0, chevron, Height);
                TextRenderer.DrawText(g, "›", UiTokens.BaseFont, arrow, UiTokens.Blend(UiTokens.Muted, UiTokens.Surface, 0.7f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        private int Circle() => _owner.Scale(20);
    }

    /// <summary>"Next: &lt;step&gt; ›" — the one obvious way forward.</summary>
    private sealed class WorkflowNextButton : Control
    {
        private readonly WorkflowBar _owner;
        private bool _hovered;

        public WorkflowNextButton(WorkflowBar owner)
        {
            _owner = owner;
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
        }

        [AllowNull]
        public override string Text
        {
            get => _owner.NextCaption();
            set { }
        }

        public int PreferredWidth() =>
            TextRenderer.MeasureText(_owner.NextCaption() + "  ›", UiTokens.StrongFont, Size.Empty, TextFormatFlags.NoPadding).Width
            + _owner.Scale(24);

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            _owner.GoNext();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                e.Handled = true;
                OnClick(EventArgs.Empty);
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle pill = new(0, _owner.Scale(1), Math.Max(1, Width - 1), Math.Max(1, Height - _owner.Scale(2)));
            if (_hovered || Focused && ShowFocusCues)
            {
                PillToolStripRenderer.FillRound(e.Graphics, pill, UiTokens.AccentSoft, _owner.Scale(PillToolStripRenderer.PillRadius));
            }

            PillToolStripRenderer.StrokeRound(e.Graphics, pill, UiTokens.Blend(UiTokens.Accent, UiTokens.Surface, 0.8f), _owner.Scale(PillToolStripRenderer.PillRadius));
            TextRenderer.DrawText(e.Graphics, _owner.NextCaption() + "  ›", UiTokens.StrongFont, ClientRectangle, UiTokens.Accent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
