using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Runtime.Scene;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using DrawingImage = System.Drawing.Image;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// The Room Editor's single right-hand Inspector. It switches between a concise room context and
/// the selected instance, while all edits continue through the editor's live-value and undo APIs.
/// </summary>
public sealed class RoomInspectorPanel : Panel
{
    private readonly RoomEditorControl _editor;
    private readonly Panel _headerBar;
    private readonly Panel _cardPanel;
    private readonly Label _cardTitle;
    private readonly Label _cardSubtitle;
    private readonly PictureBox _cardIcon;
    private readonly Button _cardButton;
    private readonly FlatScrollPanel _scroll;
    private readonly TextBox _filter = new();
    private readonly ResourceInspectorPropertySurface _unifiedSurface = new();
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<string, FieldBinding> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<InspectorSection> _sections = [];

    private RoomNode? _inspectedNode;
    private string _contextKey = string.Empty;
    private string _surfaceSignature = string.Empty;
    private bool _syncing;
    private bool _committing;
    private bool _refreshPending;
    private bool _refreshQueued;
    private string _cardVisualKey = string.Empty;

    public event Action? CloseRequested;
    public event Action<string>? OpenObjectRequested;

    public RoomInspectorPanel(RoomEditorControl editor, bool roomSettingsOnly = false)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Surface;
        MinimumSize = new Size(260, 0);
        Name = "RoomContextInspector";

        _headerBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = EditorChrome.Canvas,
            Padding = new Padding(10, 5, 8, 5),
        };
        Label headerTitle = new()
        {
            AutoSize = true,
            Dock = DockStyle.Left,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Text = "Inspector",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Button close = new()
        {
            Cursor = Cursors.Hand,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = EditorChrome.Muted,
            Height = 26,
            Text = "✕",
            Width = 28,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => CloseRequested?.Invoke();
        _headerBar.Controls.Add(headerTitle);
        _headerBar.Controls.Add(close);

        _cardPanel = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(10, 10, 10, 8),
        };
        _cardIcon = new PictureBox
        {
            BackColor = EditorChrome.Canvas,
            Dock = DockStyle.Left,
            Height = 42,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Width = 42,
        };
        _cardButton = new Button
        {
            AccessibleName = "Open source Object",
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            Height = 30,
            Text = "↗",
            Visible = false,
            Width = 30,
        };
        EditorChrome.StyleField(_cardButton);
        _toolTip.SetToolTip(_cardButton, "Open source Object");
        _cardButton.Click += (_, _) =>
        {
            if (_inspectedNode?.GameObject is { } gameObject
                && !string.IsNullOrWhiteSpace(gameObject.Prefab))
            {
                OpenObjectRequested?.Invoke(gameObject.Prefab);
            }
        };

        Panel cardText = new()
        {
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 2, 8, 2),
        };
        _cardTitle = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Height = 21,
        };
        _cardSubtitle = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Top,
            Font = EditorChrome.SmallFont,
            ForeColor = EditorChrome.Muted,
            Height = 19,
        };
        cardText.Controls.Add(_cardSubtitle);
        cardText.Controls.Add(_cardTitle);
        _cardPanel.Controls.Add(cardText);
        _cardPanel.Controls.Add(_cardButton);
        _cardPanel.Controls.Add(_cardIcon);

        _scroll = new FlatScrollPanel { Dock = DockStyle.Fill, Name = "RoomInspectorScroll" };
        _scroll.Content.Padding = new Padding(0, 2, 0, 8);
        _scroll.SizeChanged += (_, _) => UpdateScrollExtent();

        Panel filterHost = new()
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 5, 10, 5),
            BackColor = EditorChrome.Surface,
        };
        _filter.Dock = DockStyle.Fill;
        _filter.PlaceholderText = "Filter properties…";
        EditorChrome.StyleField(_filter);
        _filter.TextChanged += (_, _) => _unifiedSurface.SetFilter(_filter.Text);
        filterHost.Controls.Add(_filter);

        _unifiedSurface.EditRouter = request =>
        {
            if (_committing) return false;
            _committing = true;
            try { return _editor.TryApplyContextInspectorValue(_inspectedNode, request.PropertyPath, request.Value); }
            finally
            {
                _committing = false;
                if (_refreshPending) { _refreshPending = false; QueueRefresh(); }
            }
        };
        _unifiedSurface.ResourceEdited += (_, _) => QueueRefresh();

        Controls.Add(_scroll);
        Controls.Add(filterHost);
        Controls.Add(_cardPanel);
        Controls.Add(_headerBar);
        if (roomSettingsOnly)
        {
            Name = "RoomSettingsFields";
            MinimumSize = new Size(180, 0);
            headerTitle.Text = "Room settings";
            close.Visible = false;
            _cardPanel.Visible = false;
        }
        EditorChrome.Changed += OnChromeChanged;
        ApplyChrome();
    }

    /// <summary>Shows the selected node, or room settings when <paramref name="node"/> is null.</summary>
    public void InspectNode(RoomNode? node)
    {
        if (node is not null && !_editor.CanInspectNodeInActiveContext(node)) node = null;
        string nextKey = node?.Id ?? "$room";
        bool changed = !nextKey.Equals(_contextKey, StringComparison.OrdinalIgnoreCase);
        _inspectedNode = node;
        _contextKey = nextKey;
        RefreshInspector(changed);
    }

    /// <summary>Explicit Settings-navigation entry point for the global room context.</summary>
    public void ShowRoomSettings()
    {
        _inspectedNode = null;
        _contextKey = "$room";
        _editor.SetInspectorVisible(true);
        RefreshInspector(forceRebuild: true);
    }

    /// <summary>Returns the Inspector to the editor's current selection.</summary>
    public void ShowSelection()
    {
        InspectNode(_editor.SelectedNode);
        _editor.SetInspectorVisible(true);
    }

    /// <summary>Refreshes external changes without replacing the control currently being edited.</summary>
    public void RefreshInspector() => RefreshInspector(forceRebuild: false);

    /// <summary>Compatibility entry point for callers that use the WinForms Refresh name.</summary>
    public new void Refresh()
    {
        RefreshInspector();
        base.Refresh();
    }

    private void RefreshInspector(bool forceRebuild)
    {
        if (IsDisposed) return;
        if (_committing)
        {
            _refreshPending = true;
            return;
        }

        UpdateCard();
        IReadOnlyList<ResourceInspectorLiveValue> values = _editor.GetContextInspectorValues(_inspectedNode);
        string signature = SurfaceSignature(values);
        if (forceRebuild || !signature.Equals(_surfaceSignature, StringComparison.Ordinal))
        {
            BuildUnifiedSurface(values);
            _surfaceSignature = signature;
        }
        else
        {
            _unifiedSurface.InspectLive(CreateRoomResource(), values);
        }
        UpdateScrollExtent();
    }

    private void BuildUnifiedSurface(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        int offset = _scroll.ScrollOffset;
        _scroll.Content.SuspendLayout();
        try
        {
            foreach (Control control in _scroll.Content.Controls.Cast<Control>().ToArray())
            {
                if (!ReferenceEquals(control, _unifiedSurface)) control.Dispose();
            }
            _scroll.Content.Controls.Clear();
            ResourceItem room = CreateRoomResource();
            _unifiedSurface.Dock = DockStyle.Top;
            _unifiedSurface.InspectLive(room, values);
            _unifiedSurface.SetFilter(_filter.Text);
            _scroll.Content.Controls.Add(_unifiedSurface);
        }
        finally
        {
            _scroll.Content.ResumeLayout(true);
        }
        UpdateScrollExtent();
        _scroll.ScrollOffset = offset;
    }

    private ResourceItem CreateRoomResource() => new()
    {
        Name = ResourceDisplayName.Format(_editor.ResourcePath),
        FullPath = _editor.ResourcePath,
        RelativePath = Path.GetRelativePath(_editor.ProjectRoot, _editor.ResourcePath).Replace('\\', '/'),
        Kind = ResourceKind.Room,
        IsFolder = false,
    };

    private void BuildSurface(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        int previousOffset = _scroll.ScrollOffset;
        foreach (InspectorSection section in _sections)
            _expandedGroups[section.Title] = section.Expanded;

        _scroll.Content.SuspendLayout();
        try
        {
            foreach (Control control in _scroll.Content.Controls.Cast<Control>().ToArray())
                control.Dispose();
            _scroll.Content.Controls.Clear();
            _sections.Clear();
            _fields.Clear();

            List<IGrouping<string, ResourceInspectorLiveValue>> groups = values
                .Where(value => value.Value is not null)
                .GroupBy(value => value.Group, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => InspectorGroupOrder(group.Key))
                .ToList();
            for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                IGrouping<string, ResourceInspectorLiveValue> group = groups[groupIndex];
                InspectorSection section = new()
                {
                    Dock = DockStyle.Top,
                    Expanded = _expandedGroups.TryGetValue(group.Key, out bool remembered)
                        ? remembered
                        : groupIndex < 4 || group.Key.Equals("Instance variables", StringComparison.OrdinalIgnoreCase),
                    Title = group.Key,
                };
                IReadOnlyList<InspectorDisplayRow> rows = CreateDisplayRows(group);
                TableLayoutPanel table = CreatePropertyTable(rows);
                int row = 0;
                foreach (InspectorDisplayRow displayRow in rows)
                {
                    if (displayRow.Values.Count == 1)
                        AddPropertyRow(table, row++, displayRow.Values[0]);
                    else
                        AddVectorPropertyRow(table, row++, displayRow);
                }
                section.Body.Controls.Add(table);
                section.ExpandedChanged += (_, _) =>
                {
                    _expandedGroups[section.Title] = section.Expanded;
                    UpdateScrollExtent();
                };
                section.SizeChanged += (_, _) => UpdateScrollExtent();
                section.VisibleChanged += (_, _) => UpdateScrollExtent();
                _sections.Add(section);
            }

            foreach (InspectorSection section in _sections.AsEnumerable().Reverse())
                _scroll.Content.Controls.Add(section);
        }
        finally
        {
            _scroll.Content.ResumeLayout(true);
        }
        UpdateScrollExtent();
        _scroll.ScrollOffset = previousOffset;
    }

    private void AddPropertyRow(TableLayoutPanel table, int row, ResourceInspectorLiveValue value)
    {
        if (value.Value is RoomInspectorAction)
        {
            Control action = CreatePropertyDrawer(value);
            table.Controls.Add(action, 0, row);
            table.SetColumnSpan(action, 2);
            _toolTip.SetToolTip(action, value.Description);
            return;
        }
        Label label = CreatePropertyLabel(value.Label);
        Control field = CreatePropertyDrawer(value);
        AddDescription(value, label, field);
        table.Controls.Add(label, 0, row);
        table.Controls.Add(field, 1, row);
    }

    private void AddVectorPropertyRow(TableLayoutPanel table, int row, InspectorDisplayRow displayRow)
    {
        TableLayoutPanel block = new()
        {
            BackColor = Color.Transparent,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 2),
            RowCount = 2,
        };
        block.RowStyles.Add(new RowStyle(SizeType.Absolute, 18f));
        block.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Label label = CreatePropertyLabel(displayRow.Label);
        label.Margin = Padding.Empty;
        TableLayoutPanel axes = new()
        {
            BackColor = Color.Transparent,
            ColumnCount = displayRow.Values.Count,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 1,
        };
        axes.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (int index = 0; index < displayRow.Values.Count; index++)
        {
            axes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / displayRow.Values.Count));
            ResourceInspectorLiveValue value = displayRow.Values[index];
            TableLayoutPanel axis = new()
            {
                BackColor = Color.Transparent,
                ColumnCount = 2,
                Dock = DockStyle.Fill,
                Margin = new Padding(index == 0 ? 0 : 3, 0, 0, 0),
                RowCount = 1,
            };
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20f));
            axis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            axis.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Label axisLabel = new()
            {
                AccessibleName = value.Label + " axis",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = EditorChrome.HeadingFont,
                ForeColor = EditorChrome.Accent,
                Margin = Padding.Empty,
                Name = "RoomInspectorAxis_" + SafeName(value.PropertyPath),
                Text = value.PropertyPath[^1].ToString(),
                TextAlign = ContentAlignment.MiddleCenter,
                UseMnemonic = false,
            };
            Control field = CreatePropertyDrawer(value);
            field.Margin = Padding.Empty;
            AddDescription(value, axisLabel, field);
            axis.Controls.Add(axisLabel, 0, 0);
            axis.Controls.Add(field, 1, 0);
            axes.Controls.Add(axis, index, 0);
        }
        block.Controls.Add(label, 0, 0);
        block.Controls.Add(axes, 0, 1);
        table.Controls.Add(block, 0, row);
        table.SetColumnSpan(block, 2);
    }

    private static Label CreatePropertyLabel(string text) => new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Font = EditorChrome.SmallFont,
        ForeColor = EditorChrome.Muted,
        Margin = new Padding(0, 4, 10, 4),
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
    };

    private Control CreatePropertyDrawer(ResourceInspectorLiveValue value)
    {
        if (value.Value is RoomInspectorAction action)
        {
            Button button = new()
            {
                Text = action.Text, Name = "RoomInspectorProperty_" + SafeName(value.PropertyPath),
                Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 4), Enabled = !value.ReadOnly,
                AutoEllipsis = true, UseMnemonic = false,
            };
            EditorChrome.StyleField(button);
            button.Click += (_, _) => CommitValue(value.PropertyPath, action);
            _fields[value.PropertyPath] = new FieldBinding(button, value.ReadOnly);
            return button;
        }
        object initial = value.Value!;
        Control field = PropertyDrawerRegistry.CreateControl(new PropertyDrawerContext(
            value.Label,
            initial.GetType(),
            initial,
            changed => CommitValue(value.PropertyPath, changed),
            value.Choices,
            value.Minimum,
            value.Maximum,
            value.Increment,
            value.DecimalPlaces,
            value.ReadOnly,
            value.AssetKind,
            _editor.ProjectRoot,
            (IWin32Window?)FindForm() ?? this,
            "RoomInspectorProperty_" + SafeName(value.PropertyPath),
            value.Description));
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 4, 0, 4);
        field.Enabled = !value.ReadOnly;
        StyleDrawer(field);
        _fields[value.PropertyPath] = new FieldBinding(field, value.ReadOnly);
        return field;
    }

    private void AddDescription(ResourceInspectorLiveValue value, Control label, Control field)
    {
        if (string.IsNullOrWhiteSpace(value.Description)) return;
        _toolTip.SetToolTip(label, value.Description);
        _toolTip.SetToolTip(field, value.Description);
    }

    private static IReadOnlyList<InspectorDisplayRow> CreateDisplayRows(
        IEnumerable<ResourceInspectorLiveValue> values)
    {
        List<ResourceInspectorLiveValue> source = values.ToList();
        List<InspectorDisplayRow> rows = [];
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (ResourceInspectorLiveValue value in source)
        {
            if (!used.Add(value.PropertyPath)) continue;
            Match transform = Regex.Match(value.PropertyPath,
                "^Context\\.Selection\\.Transform\\.(?<vector>Position|Rotation|Scale)\\.[XYZ]$",
                RegexOptions.IgnoreCase);
            Match modelScale = Regex.Match(value.PropertyPath,
                "^Context\\.Selection\\.Components\\[(?<index>\\d+)\\]\\.Properties\\.Scale[XYZ]$",
                RegexOptions.IgnoreCase);
            string vector;
            string pattern;
            if (transform.Success)
            {
                vector = transform.Groups["vector"].Value;
                pattern = "^Context\\.Selection\\.Transform\\." + Regex.Escape(vector) + "\\.[XYZ]$";
            }
            else if (modelScale.Success)
            {
                vector = "Scale";
                pattern = "^Context\\.Selection\\.Components\\["
                          + Regex.Escape(modelScale.Groups["index"].Value)
                          + "\\]\\.Properties\\.Scale[XYZ]$";
            }
            else
            {
                string label = value.PropertyPath.EndsWith(".UniformScale", StringComparison.OrdinalIgnoreCase)
                    ? "Lock proportions"
                    : value.Label;
                rows.Add(new InspectorDisplayRow(label, [value]));
                continue;
            }

            List<ResourceInspectorLiveValue> axes = source.Where(candidate => Regex.IsMatch(
                    candidate.PropertyPath,
                    pattern,
                    RegexOptions.IgnoreCase))
                .OrderBy(candidate => candidate.PropertyPath[^1] switch
                {
                    'X' or 'x' => 0,
                    'Y' or 'y' => 1,
                    _ => 2,
                })
                .ToList();
            foreach (ResourceInspectorLiveValue axis in axes) used.Add(axis.PropertyPath);
            Match unit = Regex.Match(value.Label, @"\s*(?<unit>\([^)]*\))$");
            rows.Add(new InspectorDisplayRow(
                vector + (unit.Success ? " " + unit.Groups["unit"].Value : string.Empty),
                axes));
        }
        return rows;
    }

    private static int InspectorGroupOrder(string group) => group.ToLowerInvariant() switch
    {
        "instance" => 0,
        "transform" => 1,
        "object reference" => 2,
        "instance variables" => 3,
        _ => 10,
    };

    private void CommitValue(string propertyPath, object? value)
    {
        if (_syncing || _committing) return;
        bool handled;
        _committing = true;
        try
        {
            handled = _editor.TryApplyContextInspectorValue(_inspectedNode, propertyPath, value);
        }
        finally
        {
            _committing = false;
        }

        if (handled || _refreshPending)
        {
            _refreshPending = false;
            QueueRefresh();
        }
    }

    private void QueueRefresh()
    {
        if (IsDisposed || Disposing || _refreshQueued) return;
        if (IsHandleCreated)
        {
            _refreshQueued = true;
            BeginInvoke(() =>
            {
                _refreshQueued = false;
                if (!IsDisposed && !Disposing) RefreshInspector();
            });
        }
        else
        {
            RefreshInspector();
        }
    }

    private void SyncSurface(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        _syncing = true;
        try
        {
            foreach (ResourceInspectorLiveValue value in values)
            {
                if (!_fields.TryGetValue(value.PropertyPath, out FieldBinding? binding)) continue;
                binding.ReadOnly = value.ReadOnly;
                binding.Control.Enabled = !value.ReadOnly;
                if (!binding.Control.ContainsFocus && value.Value is not null)
                    SetDrawerValue(binding.Control, value.Value);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdateCard()
    {
        if (_inspectedNode is null)
        {
            _cardTitle.Text = _editor.Room.Name;
            _cardSubtitle.Text = $"{(_editor.Room.Dimension == RoomDimension.ThreeD ? "3D" : "2D")} room · {_editor.Room.Nodes.Count} instances";
            _cardButton.Visible = false;
            if (_cardVisualKey != "$room") { _cardVisualKey = "$room"; ReplaceCardIcon(CreateRoomCardIcon()); }
            return;
        }

        RoomLayer? layer = _editor.LayerFor(_inspectedNode);
        _cardTitle.Text = _inspectedNode.Name;
        _cardSubtitle.Text = _inspectedNode.Kind + (layer is null ? string.Empty : " · " + layer.Name)
            + (_editor.IsNodeLocked(_inspectedNode) ? " · Locked" : string.Empty);
        _cardButton.Visible = _inspectedNode.GameObject is { Prefab.Length: > 0 };
        string visualKey = _inspectedNode.Id + "|" + _inspectedNode.GameObject?.Prefab;
        if (_cardVisualKey != visualKey)
        {
            _cardVisualKey = visualKey;
            ReplaceCardIcon(ResolveCardIcon(_inspectedNode));
        }
    }

    private void UpdateScrollExtent()
    {
        if (_scroll.IsDisposed) return;
        int height = _scroll.Content.Padding.Vertical + _scroll.Content.Controls.Cast<Control>()
            .Where(control => control.Visible)
            .Sum(control => control.Height + control.Margin.Vertical);
        _scroll.SetContentHeight(height);
    }

    private void OnChromeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed) ApplyChrome();
    }

    private void ApplyChrome()
    {
        BackColor = EditorChrome.Surface;
        ForeColor = EditorChrome.Text;
        _headerBar.BackColor = EditorChrome.Canvas;
        _cardPanel.BackColor = EditorChrome.Surface;
        _cardIcon.BackColor = EditorChrome.Canvas;
        _cardTitle.ForeColor = EditorChrome.Text;
        _cardSubtitle.ForeColor = EditorChrome.Muted;
        EditorChrome.StyleField(_cardButton);
        foreach (FieldBinding binding in _fields.Values) StyleDrawer(binding.Control);
        Invalidate(true);
    }

    private static TableLayoutPanel CreatePropertyTable(IReadOnlyList<InspectorDisplayRow> rows)
    {
        TableLayoutPanel table = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            RowCount = rows.Count,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66f));
        foreach (InspectorDisplayRow row in rows)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, row.Values.Count > 1 ? 50f : 40f));
        return table;
    }

    private static void StyleDrawer(Control control)
    {
        switch (control)
        {
            case TableLayoutPanel or Panel:
                control.BackColor = Color.Transparent;
                break;
            case CheckBox check:
                check.BackColor = Color.Transparent;
                check.ForeColor = EditorChrome.Text;
                check.Font = EditorChrome.BaseFont;
                break;
            case TrackBar track:
                track.BackColor = EditorChrome.Surface;
                break;
            default:
                EditorChrome.StyleField(control);
                break;
        }
        foreach (Control child in control.Controls) StyleDrawer(child);
    }

    private static void SetDrawerValue(Control control, object value)
    {
        try
        {
            switch (control)
            {
                case NumericUpDown numeric:
                    decimal number = Math.Clamp(Convert.ToDecimal(value, CultureInfo.InvariantCulture), numeric.Minimum, numeric.Maximum);
                    if (numeric.Value != number) numeric.Value = number;
                    return;
                case CheckBox check:
                    bool flag = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                    if (check.Checked != flag) check.Checked = flag;
                    return;
                case ComboBox combo:
                    string choice = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    object? item = combo.Items.Cast<object>().FirstOrDefault(candidate =>
                        string.Equals(candidate.ToString(), choice, StringComparison.OrdinalIgnoreCase));
                    if (item is not null && !Equals(combo.SelectedItem, item)) combo.SelectedItem = item;
                    return;
                case TextBoxBase text:
                    string content = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (!text.Text.Equals(content, StringComparison.Ordinal)) text.Text = content;
                    return;
                case Button button when value is Color color && Equals(button.Tag, "property-color"):
                    button.BackColor = color;
                    button.ForeColor = color.GetBrightness() < 0.5f ? Color.White : Color.Black;
                    button.Text = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
                    return;
            }
            foreach (Control child in control.Controls) SetDrawerValue(child, value);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            // Keep the current editor value when an external refresh contains an invalid scalar.
        }
    }

    private static string SurfaceSignature(IReadOnlyList<ResourceInspectorLiveValue> values)
    {
        StringBuilder signature = new();
        foreach (ResourceInspectorLiveValue value in values)
        {
            signature.Append(value.Group).Append('|')
                .Append(value.PropertyPath).Append('|')
                .Append(value.Label).Append('|')
                .Append(value.Value?.GetType().FullName).Append('|')
                .Append(value.AssetKind).Append('|')
                .Append(value.Minimum).Append('|').Append(value.Maximum).Append('|')
                .AppendJoin(',', value.Choices ?? []).AppendLine();
        }
        return signature.ToString();
    }

    private void ReplaceCardIcon(DrawingImage? image)
    {
        DrawingImage? previous = _cardIcon.Image;
        _cardIcon.Image = image;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            EditorChrome.Changed -= OnChromeChanged;
            _toolTip.Dispose();
            ReplaceCardIcon(null);
        }
        base.Dispose(disposing);
    }

    private static Bitmap CreateRoomCardIcon()
    {
        Bitmap bitmap = new(36, 36);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush fill = new(Color.FromArgb(42, 90, 160, 250));
        using Pen border = new(Color.FromArgb(105, 170, 250), 2f);
        graphics.FillRectangle(fill, 5, 5, 26, 26);
        graphics.DrawRectangle(border, 5, 5, 26, 26);
        return bitmap;
    }

    private Bitmap ResolveCardIcon(RoomNode node)
    {
        try
        {
            if (node.GameObject is { } gameObject)
            {
                string? prefabPath = RoomSceneBuilder.ResolvePrefabPath(_editor.ProjectRoot, gameObject.Prefab);
                if (prefabPath is not null && File.Exists(prefabPath))
                {
                    JObject document = RoomSceneBuilder.ApplyOverrides(
                        JObject.Parse(File.ReadAllText(prefabPath)), gameObject.ComponentOverrides);
                    string? sprite = (document["components"] as JArray)?
                        .OfType<JObject>()
                        .Where(component => (bool?)component["enabled"] ?? true)
                        .Select(component => (string?)(component["props"] as JObject)?["Sprite"]
                                             ?? (string?)(component["props"] as JObject)?["Image"])
                        .FirstOrDefault(reference => !string.IsNullOrWhiteSpace(reference));
                    sprite ??= (string?)document["sprite"] ?? (string?)document["Sprite"];
                    if (!string.IsNullOrWhiteSpace(sprite))
                    {
                        string? imagePath = ProjectAssetIndex.ResolveSpriteImage(_editor.ProjectRoot, sprite);
                        if (imagePath is not null && File.Exists(imagePath)
                            && TryCreateCardThumbnail(imagePath) is { } thumbnail)
                        {
                            return thumbnail;
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or JsonException or ArgumentException)
        {
            // A broken source Object still gets a stable fallback icon and editable reference.
        }

        Bitmap fallback = new(36, 36);
        using Graphics fallbackGraphics = Graphics.FromImage(fallback);
        fallbackGraphics.Clear(Color.Transparent);
        fallbackGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush brush = new(Color.FromArgb(70, 140, 230));
        fallbackGraphics.FillEllipse(brush, 6, 6, 24, 24);
        return fallback;
    }

    private static Bitmap? TryCreateCardThumbnail(string imagePath)
    {
        try
        {
            using FileStream stream = new(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using DrawingImage source = DrawingImage.FromStream(
                stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (source.Width <= 0 || source.Height <= 0) return null;

            Bitmap thumbnail = new(36, 36);
            using Graphics graphics = Graphics.FromImage(thumbnail);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            float scale = Math.Min(36f / source.Width, 36f / source.Height);
            int width = Math.Max(1, (int)MathF.Round(source.Width * scale));
            int height = Math.Max(1, (int)MathF.Round(source.Height * scale));
            graphics.DrawImage(source, new Rectangle((36 - width) / 2, (36 - height) / 2, width, height));
            return thumbnail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or ExternalException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private static string SafeName(string value) => Regex.Replace(value, @"[^A-Za-z0-9_]", "_");

    private sealed class FieldBinding(Control control, bool readOnly)
    {
        public Control Control { get; } = control;
        public bool ReadOnly { get; set; } = readOnly;
    }

    private sealed record InspectorDisplayRow(
        string Label,
        IReadOnlyList<ResourceInspectorLiveValue> Values);
}
