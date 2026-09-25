using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared responsive command surface for suite editors. Domain commands flow from the left while
/// document state and Save remain pinned on the right; lower-priority commands move into a labelled
/// overflow when the document narrows.
/// </summary>
public sealed class EditorCommandBar : ToolStrip
{
    private IEditorSurface? _surface;
    private ToolStripButton? _saveButton;
    private ToolStripDropDownButton? _historyButton;
    private ToolStripMenuItem? _undoItem;
    private ToolStripMenuItem? _redoItem;
    private ToolStripLabel? _stateLabel;

    internal EditorCommandBar()
    {
        AutoSize = false;
        CanOverflow = true;
        Dock = DockStyle.Top;
        GripStyle = ToolStripGripStyle.Hidden;
        Height = EditorChrome.CommandBarHeight;
        LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
        Padding = EditorChrome.CommandBarPadding;
        ShowItemToolTips = true;
        Stretch = true;

        OverflowButton.Text = "More";
        OverflowButton.AutoSize = false;
        OverflowButton.Width = 54;
        OverflowButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
        OverflowButton.ToolTipText = "More editor commands";
        OverflowButton.AccessibleName = "More editor commands";
        OverflowButton.AccessibleDescription =
            "Commands that do not fit in the current editor width.";

        ItemAdded += (_, args) =>
        {
            if (args.Item is { } item)
            {
                ApplyAccessibility(item);
            }
        };
    }

    /// <summary>Current user-facing document state, exposed for shell/headless verification.</summary>
    public string DocumentStateText => _stateLabel?.Text ?? string.Empty;

    public bool IsDocumentBound => _surface is not null;

    public ToolStripItem? SaveCommand => _saveButton;

    public ToolStripItem? HistoryCommand => _historyButton;

    public bool HasOverflowedCommands => Items.Cast<ToolStripItem>().Any(item => item.IsOnOverflow);

    public bool IsSavePinned => _saveButton is not null && !_saveButton.IsOnOverflow;

    /// <summary>True when Save is actually present on the primary strip, not merely configured.</summary>
    public bool IsSaveVisible => IsFullyVisibleOnPrimaryStrip(_saveButton);

    /// <summary>True when the non-colour save-state label is visible on the primary strip.</summary>
    public bool IsDocumentStateVisible => IsFullyVisibleOnPrimaryStrip(_stateLabel);

    internal void BindDocument(IEditorSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (_surface is not null)
        {
            return;
        }

        _surface = surface;
        _saveButton = Items
            .OfType<ToolStripButton>()
            .FirstOrDefault(item => string.Equals(NormaliseCaption(item.Text), "Save", StringComparison.OrdinalIgnoreCase));
        if (_saveButton is null)
        {
            _saveButton = EditorChrome.ToolButton(
                "Save",
                "Save this resource (Ctrl+S)",
                surface.Save);
            Items.Add(_saveButton);
        }

        _saveButton.Alignment = ToolStripItemAlignment.Right;
        _saveButton.Overflow = ToolStripItemOverflow.Never;
        _saveButton.AccessibleName = "Save resource";

        _undoItem = FindMenuItem("Undo");
        _redoItem = FindMenuItem("Redo");
        if (_undoItem is null || _redoItem is null)
        {
            _historyButton = new ToolStripDropDownButton("History")
            {
                Alignment = ToolStripItemAlignment.Right,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ForeColor = EditorChrome.Text,
                Overflow = ToolStripItemOverflow.AsNeeded,
                ToolTipText = "Undo or redo document edits",
                AccessibleName = "Document history",
            };
            _undoItem = new ToolStripMenuItem("Undo")
            {
                ShortcutKeyDisplayString = "Ctrl+Z",
                ToolTipText = "Undo the latest document edit",
            };
            _redoItem = new ToolStripMenuItem("Redo")
            {
                ShortcutKeyDisplayString = "Ctrl+Y",
                ToolTipText = "Redo the latest undone document edit",
            };
            _undoItem.Click += (_, _) => surface.Undo();
            _redoItem.Click += (_, _) => surface.Redo();
            _historyButton.DropDownItems.Add(_undoItem);
            _historyButton.DropDownItems.Add(_redoItem);
            Items.Add(_historyButton);
        }

        _stateLabel = new ToolStripLabel
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Overflow = ToolStripItemOverflow.Never,
            ToolTipText = "Document save state",
            AccessibleName = "Document save state",
        };
        Items.Add(_stateLabel);

        surface.DirtyChanged += OnSurfaceStateChanged;
        Disposed += OnDisposed;
        RefreshDocumentState();
    }

    /// <summary>Refreshes state after journal changes and is safe to call before binding.</summary>
    public void RefreshDocumentState()
    {
        if (_surface is null || _stateLabel is null || _undoItem is null || _redoItem is null)
        {
            return;
        }

        bool dirty = _surface.IsDirty;
        _stateLabel.Text = dirty ? "● Unsaved" : "● Saved";
        _stateLabel.ForeColor = dirty ? EditorChrome.Warning : EditorChrome.Success;
        _stateLabel.AccessibleDescription = dirty
            ? "This resource has unsaved changes."
            : "This resource is saved.";
        _undoItem.Enabled = _surface.CanUndo;
        _redoItem.Enabled = _surface.CanRedo;
        if (_historyButton is not null)
        {
            _historyButton.ToolTipText = _surface.CanUndo || _surface.CanRedo
                ? "Undo or redo document edits"
                : "No document edits to undo or redo";
        }
        PerformLayout();
        Invalidate();
    }

    private void OnSurfaceStateChanged(object? sender, EventArgs e) => RefreshDocumentState();

    private void OnDisposed(object? sender, EventArgs e)
    {
        if (_surface is not null)
        {
            _surface.DirtyChanged -= OnSurfaceStateChanged;
        }
    }

    private bool IsFullyVisibleOnPrimaryStrip(ToolStripItem? item)
    {
        if (item is not { Available: true, Visible: true }
            || !ReferenceEquals(item.GetCurrentParent(), this)
            || item.Bounds.Width <= 0)
        {
            return false;
        }

        Rectangle display = DisplayRectangle;
        return item.Bounds.Left >= display.Left && item.Bounds.Right <= display.Right;
    }

    private ToolStripMenuItem? FindMenuItem(string caption) =>
        Items.OfType<ToolStripDropDownButton>()
            .SelectMany(menu => menu.DropDownItems.OfType<ToolStripMenuItem>())
            .FirstOrDefault(item => string.Equals(item.Text, caption, StringComparison.OrdinalIgnoreCase));

    private static string NormaliseCaption(string? text) =>
        (text ?? string.Empty).Replace("＋", string.Empty, StringComparison.Ordinal)
            .Replace("…", string.Empty, StringComparison.Ordinal)
            .Trim();

    private static void ApplyAccessibility(ToolStripItem item)
    {
        if (string.IsNullOrWhiteSpace(item.AccessibleName))
        {
            item.AccessibleName = string.IsNullOrWhiteSpace(item.Text)
                ? item.ToolTipText
                : NormaliseCaption(item.Text);
        }

        if (item.Overflow != ToolStripItemOverflow.Never)
        {
            item.Overflow = ToolStripItemOverflow.AsNeeded;
        }
    }
}
