using Genesis.Runtime.Scripting.VM;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>Non-modal debugger state for a running PGSL VM worker.</summary>
public sealed class PgslDebuggerWindow : Form
{
    private readonly PgslDebugController _controller;
    private readonly Label _status = new();
    private readonly ListView _callStack = new();
    private readonly ListView _variables = new();

    public PgslDebuggerWindow(PgslDebugController controller, string title)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        Text = "PGSL Debugger — " + title;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(650, 420);
        Size = new Size(780, 560);
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;

        ToolStrip toolbar = EditorChrome.MakeToolbar();
        toolbar.Items.Add(EditorChrome.ToolButton("Continue (F5)", "Run to the next breakpoint", controller.Continue));
        toolbar.Items.Add(EditorChrome.ToolButton("Step Into (F11)", "Execute one VM instruction", controller.StepInto));
        toolbar.Items.Add(EditorChrome.ToolButton("Pause", "Pause before the next instruction", controller.Pause));
        toolbar.Items.Add(EditorChrome.ToolButton("Stop", "End this debug session", controller.Stop));

        _status.Dock = DockStyle.Top;
        _status.Height = 34;
        _status.Padding = new Padding(10, 8, 4, 0);
        _status.BackColor = EditorChrome.Raised;
        _status.ForeColor = EditorChrome.Muted;
        _status.Text = "Starting VM…";

        ConfigureList(_callStack);
        _callStack.Columns.Add("Call stack", 330);
        ConfigureList(_variables);
        _variables.Columns.Add("Variable", 210);
        _variables.Columns.Add("Value", 260);
        _variables.Columns.Add("Type", 120);

        SplitContainer split = new()
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 5,
            SplitterDistance = 285,
            BackColor = EditorChrome.Border,
        };
        split.Panel1.Controls.Add(_callStack);
        split.Panel1.Controls.Add(Heading("CALL STACK"));
        split.Panel2.Controls.Add(_variables);
        split.Panel2.Controls.Add(Heading("LOCALS AND INSTANCE VALUES"));

        Controls.Add(split);
        Controls.Add(_status);
        Controls.Add(toolbar);
        _controller.Paused += OnPaused;
        _controller.Continued += OnContinued;
        _controller.Completed += OnCompleted;
        FormClosed += (_, _) =>
        {
            _controller.Paused -= OnPaused;
            _controller.Continued -= OnContinued;
            _controller.Completed -= OnCompleted;
            _controller.Stop();
        };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { _controller.Continue(); return true; }
        if (keyData == Keys.F11) { _controller.StepInto(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OnPaused(object? sender, PgslDebugLocation location) => OnUi(() =>
    {
        _status.Text = string.IsNullOrWhiteSpace(location.ErrorMessage)
            ? $"Paused · {location.SourceName} · {location.FunctionName} · line {location.Line}"
            : $"Paused on error · line {location.Line}: {location.ErrorMessage}";
        _status.ForeColor = string.IsNullOrWhiteSpace(location.ErrorMessage)
            ? EditorChrome.Warning
            : EditorChrome.Error;
        _callStack.BeginUpdate();
        _callStack.Items.Clear();
        foreach (string frame in location.CallStack.Reverse())
            _callStack.Items.Add(new ListViewItem(frame));
        _callStack.EndUpdate();
        _variables.BeginUpdate();
        _variables.Items.Clear();
        foreach ((string name, object value) in location.Variables
                     .Where(pair => !pair.Key.StartsWith("@", StringComparison.Ordinal))
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            ListViewItem item = new(name);
            item.SubItems.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null");
            item.SubItems.Add(value?.GetType().Name ?? "Null");
            _variables.Items.Add(item);
        }
        _variables.EndUpdate();
    });

    private void OnContinued(object? sender, EventArgs e) => OnUi(() =>
    {
        _status.Text = "Running…";
        _status.ForeColor = EditorChrome.Success;
    });

    private void OnCompleted(object? sender, EventArgs e) => OnUi(() =>
    {
        _status.Text = "Debug session complete.";
        _status.ForeColor = EditorChrome.Success;
    });

    private void OnUi(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private static void ConfigureList(ListView view)
    {
        view.Dock = DockStyle.Fill;
        view.View = View.Details;
        view.FullRowSelect = true;
        view.BorderStyle = BorderStyle.None;
        view.BackColor = EditorChrome.Surface;
        view.ForeColor = EditorChrome.Text;
        view.HeaderStyle = ColumnHeaderStyle.Nonclickable;
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Top,
        Height = 30,
        Padding = new Padding(8, 8, 0, 0),
        BackColor = EditorChrome.Raised,
        ForeColor = EditorChrome.Muted,
    };
}
