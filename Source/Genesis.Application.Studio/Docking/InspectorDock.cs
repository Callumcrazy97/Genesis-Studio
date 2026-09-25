using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Inspector;
using Genesis.Application.Editors.Suite.Objects;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;
using Genesis.Shared.Assets;
using WeifenLuo.WinFormsUI.Docking;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Studio.Docking;

public sealed class InspectorDock : GenesisDockContent
{
    private const int MaxTextSummaryBytes = 1024 * 1024;
    private const int MaxJsonSummaryBytes = 4 * 1024 * 1024;
    private const int MaxThumbnailBytes = 32 * 1024 * 1024;
    private const long MaxThumbnailPixels = 64L * 1024 * 1024;

    private readonly Panel _header;
    private readonly TableLayoutPanel _headerLayout;
    private readonly Panel _searchHost;
    private readonly TextBox _propertySearch;
    private readonly Panel _preview;
    private readonly PictureBox _thumbnail;
    private readonly Label _glyph;
    private readonly Label _name;
    private readonly Label _kind;
    private readonly Panel _details;
    private readonly TableLayoutPanel _content;
    private readonly TableLayoutPanel _identityGrid;
    private readonly Label _pathCaption;
    private readonly TextBox _path;
    private readonly Label _guidCaption;
    private readonly TextBox _guid;
    private readonly Label _modifiedCaption;
    private readonly Label _modified;
    private readonly Label _sizeCaption;
    private readonly Label _size;
    private readonly Label _kindSummary;
    private readonly Label _propertiesHeading;
    private readonly ResourceInspectorPropertySurface _propertySurface;
    private readonly ResourceIdentityHeader _resourceIdentity;
    private readonly TableLayoutPanel _actions;
    private readonly ModernButton _reveal;
    private readonly ModernButton _copyPath;
    private readonly EmptyStatePanel _emptyState;
    private readonly ModernButton _copyGuid;
    private readonly Panel _modelPreviewCard;
    private readonly Panel _modelPreviewHost;
    private ObjectCompositionPreviewControl? _modelPreview;
    private readonly ToolTip _toolTip = new();
    private ResourceItem? _resource;
    private bool _arranging;
    private bool _dpiLayoutReady;

    public InspectorDock()
    {
        Text = "Inspector";
        TabText = "Inspector";
        DockAreas = DockAreas.DockLeft | DockAreas.DockRight | DockAreas.Float;
        ShowHint = DockState.DockRight;

        _header = new Panel
        {
            Dock = DockStyle.Top,
            Name = "InspectorHeader",
            Tag = "surface",
        };
        _headerLayout = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            RowCount = 1,
            Tag = "surface",
        };
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        _headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _header.Controls.Add(_headerLayout);

        _searchHost = new Panel
        {
            Dock = DockStyle.Bottom,
            Name = "InspectorSearchHost",
            Tag = "surface",
        };
        _propertySearch = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Name = "InspectorPropertySearch",
            PlaceholderText = "Filter properties…",
            Margin = new Padding(8, 6, 8, 6),
        };
        _propertySearch.TextChanged += (_, _) => _propertySurface?.SetFilter(_propertySearch.Text);
        _searchHost.Controls.Add(_propertySearch);
        _header.Controls.Add(_searchHost);
        _headerLayout.BringToFront();

        _preview = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Tag = "surface",
        };
        _glyph = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Symbol", 20f),
            Text = "◇",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _thumbnail = new PictureBox
        {
            Dock = DockStyle.Fill,
            Name = "InspectorThumbnail",
            SizeMode = PictureBoxSizeMode.Zoom,
            Visible = false,
        };
        _preview.Controls.Add(_glyph);
        _preview.Controls.Add(_thumbnail);
        _headerLayout.Controls.Add(_preview, 0, 0);

        TableLayoutPanel title = new()
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2,
            Tag = "surface",
        };
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        _name = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            Text = "Nothing selected",
            TextAlign = ContentAlignment.BottomLeft,
        };
        _kind = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Text = "Select a resource",
            TextAlign = ContentAlignment.TopLeft,
        };
        title.Controls.Add(_name, 0, 0);
        title.Controls.Add(_kind, 0, 1);
        _headerLayout.Controls.Add(title, 1, 0);

        _details = new Panel
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Name = "InspectorDetailsViewport",
            Tag = "canvas",
        };
        _content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Name = "InspectorContent",
            RowCount = 0,
            Tag = "canvas",
        };
        _content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _details.Controls.Add(_content);

        _emptyState = new EmptyStatePanel
        {
            Dock = DockStyle.Fill,
            Name = "InspectorEmptyState",
            TitleText = "Nothing selected",
            BodyText = "Select a resource in Assets to inspect its identity and editable properties.",
        };
        _details.Controls.Add(_emptyState);
        _emptyState.BringToFront();
        _content.Visible = false;
        _header.Visible = false;

        // Put the values users can act on immediately below the resource header. Identity,
        // descriptive metadata and file actions remain available further down the scroll view.
        // This is especially important in play mode, where a narrow dock must expose the live
        // script fields without making the user scroll past path/GUID bookkeeping first.
        _propertiesHeading = SectionHeading("EDITABLE PROPERTIES", "InspectorPropertiesHeading");
        _propertySurface = new ResourceInspectorPropertySurface();
        _propertySurface.ShowIdentityGroup = false;
        _propertySurface.ResourceEdited += OnResourcePropertyEdited;
        _resourceIdentity = new ResourceIdentityHeader();
        _resourceIdentity.OpenRequested += (_, resource) => ResourceOpenRequested?.Invoke(this, resource);
        AddContent(_resourceIdentity);
        AddContent(_propertiesHeading);
        AddContent(_propertySurface);

        AddContent(SectionHeading("IDENTITY", "InspectorIdentityHeading"));
        _identityGrid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Name = "InspectorIdentityGrid",
            RowCount = 4,
            Tag = "surface",
        };
        _identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _pathCaption = FieldCaption("Resource", "InspectorPathCaption");
        _path = FieldValue("InspectorPathValue");
        _guidCaption = FieldCaption("GUID", "InspectorGuidCaption");
        _guid = FieldValue("InspectorGuidValue");
        _modifiedCaption = FieldCaption("Modified", "InspectorModifiedCaption");
        _modified = PlainValue("InspectorModifiedValue");
        _sizeCaption = FieldCaption("Size", "InspectorSizeCaption");
        _size = PlainValue("InspectorSizeValue");
        AddIdentityRow(0, _pathCaption, _path);
        AddIdentityRow(1, _guidCaption, _guid);
        AddIdentityRow(2, _modifiedCaption, _modified);
        AddIdentityRow(3, _sizeCaption, _size);
        AddContent(_identityGrid);

        AddContent(SectionHeading("RESOURCE DETAILS", "InspectorResourceHeading"));
        _kindSummary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Name = "InspectorKindSummary",
            Padding = new Padding(10),
            Tag = "surface",
            Text = "Select a resource to see the properties that matter for its kind.",
            UseMnemonic = false,
        };
        AddContent(_kindSummary);

        AddContent(SectionHeading("QUICK ACTIONS", "InspectorActionsHeading"));
        _actions = new TableLayoutPanel
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Name = "InspectorActions",
            Tag = "canvas",
        };
        _reveal = ActionButton("Reveal", RevealResource);
        _copyPath = ActionButton("Copy name", CopyResourcePath);
        _copyGuid = ActionButton("Copy GUID", CopyResourceGuid);
        AddContent(_actions);

        _modelPreviewCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 254,
            Margin = new Padding(0, 12, 0, 0),
            Visible = false,
        };
        Button previewHeader = new()
        {
            Dock = DockStyle.Top,
            FlatStyle = FlatStyle.Flat,
            Height = 34,
            Text = "▾  3D Model Preview",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _modelPreviewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 24) };
        bool previewExpanded = true;
        previewHeader.Click += (_, _) =>
        {
            previewExpanded = !previewExpanded;
            _modelPreviewHost.Visible = previewExpanded;
            _modelPreviewCard.Height = previewExpanded ? 254 : 34;
            previewHeader.Text = (previewExpanded ? "▾  " : "▸  ") + "3D Model Preview";
        };
        _modelPreviewCard.Controls.Add(_modelPreviewHost);
        _modelPreviewCard.Controls.Add(previewHeader);
        AddContent(_modelPreviewCard);

        Controls.Add(_details);
        Controls.Add(_header);
        // WinForms lays out docked siblings back-to-front. Keeping Fill at z-order zero makes the
        // Top header reserve its strip before the remaining client rectangle is assigned.
        _details.BringToFront();

        _details.ClientSizeChanged += (_, _) => ApplyResponsiveLayout();
        ThemeService.ThemeChanged += OnThemeChanged;
        ThemeService.Apply(this);
        ApplyVisualTheme();
        ApplyDensity();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _dpiLayoutReady = true;
        ApplyDensity();
    }

    internal void ClearSelection()
    {
        _resource = null;
        _resourceIdentity.ClearSelection();
        _propertySurface.ClearSelection();
        _modelPreview?.Dispose(); _modelPreview = null;
        _modelPreviewHost.Controls.Clear(); _modelPreviewCard.Visible = false;
        Image? thumbnail = _thumbnail.Image; _thumbnail.Image = null; thumbnail?.Dispose();
        _content.Visible = false; _header.Visible = false; _emptyState.Visible = true;
    }

    public void Inspect(ResourceItem resource)
    {
        _emptyState.Visible = false;
        _content.Visible = true;
        _header.Visible = true;
        _resource = resource ?? throw new ArgumentNullException(nameof(resource));
        ResourceDefinition? definition = resource.IsFolder
            ? null
            : ResourceDefinitions.All.FirstOrDefault(candidate => candidate.Kind == resource.Kind);

        _name.Text = ResourceDisplayName.Format(resource.Name);
        _kind.Text = resource.IsFolder
            ? "Folder"
            : definition?.DisplayName ?? "Unregistered resource";
        _glyph.Text = resource.IsFolder ? "▰" : definition?.IconGlyph ?? "◇";
        _path.Text = resource.Name;
        _guid.Text = resource.AssetId == Guid.Empty
            ? "Folder — no asset GUID"
            : resource.AssetId.ToString("N");
        _copyGuid.Enabled = resource.AssetId != Guid.Empty;

        _modified.Text = ResourceModified(resource);
        _size.Text = ResourceSize(resource);
        _kindSummary.Text = ResourceSummary(resource);
        IReadOnlyList<ResourceInspectorLiveValue> liveValues = LiveValueProvider?.Invoke(resource) ?? [];
        _propertySurface.Inspect(resource, liveValues);
        _resourceIdentity.Bind(resource, _propertySurface);
        bool hasRuntimeValues = liveValues.Any(value =>
            value.PropertyPath.StartsWith("Runtime.", StringComparison.OrdinalIgnoreCase));
        _propertiesHeading.Text = hasRuntimeValues
            ? "PLAY MODE · EDITABLE VALUES"
            : "EDITABLE PROPERTIES";
        _propertiesHeading.Visible = !resource.IsFolder;
        _propertySurface.Visible = !resource.IsFolder;

        _toolTip.SetToolTip(_name, resource.Name);
        _toolTip.SetToolTip(_path, resource.Name);
        _toolTip.SetToolTip(_guid, _guid.Text);
        _toolTip.SetToolTip(_kindSummary, _kindSummary.Text);
        SetThumbnail(resource);
        UpdateModelPreview(resource);
        ApplyResponsiveLayout();
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<ResourceInspectorEditRequest, bool>? EditRouter
    {
        get => _propertySurface.EditRouter;
        set => _propertySurface.EditRouter = value;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<ResourceItem, IReadOnlyList<ResourceInspectorLiveValue>>? LiveValueProvider { get; set; }

    public IReadOnlyList<string> EditablePropertyPaths => _propertySurface.EditablePropertyPaths;

    public IReadOnlyList<string> EditablePropertyGroups => _propertySurface.GroupNames;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PropertyFilterText
    {
        get => _propertySearch.Text;
        set => _propertySearch.Text = value ?? string.Empty;
    }

    public event EventHandler<ResourceInspectorEditedEventArgs>? ResourceEdited;

    public event EventHandler<ResourceItem>? ResourceOpenRequested;

    public bool SetEditableValue(string propertyPath, object? value) =>
        _propertySurface.SetValue(propertyPath, value);

    public void RefreshLiveValues(ResourceItem resource)
    {
        if (_resource is null
            || !string.Equals(
                Path.GetFullPath(_resource.FullPath),
                Path.GetFullPath(resource.FullPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        IReadOnlyList<ResourceInspectorLiveValue> values = LiveValueProvider?.Invoke(_resource) ?? [];
        _propertySurface.Inspect(_resource, values);
        bool runtime = values.Any(value => value.PropertyPath.StartsWith("Runtime.", StringComparison.OrdinalIgnoreCase));
        _propertiesHeading.Text = runtime ? "PLAY MODE · EDITABLE VALUES" : "EDITABLE PROPERTIES";
    }

    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        if (_resource is not null && changes.Changed(_resource.FullPath)) Inspect(_resource);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ThemeService.ThemeChanged -= OnThemeChanged;
            _propertySurface.ResourceEdited -= OnResourcePropertyEdited;
            _toolTip.Dispose();
            _modelPreview?.Dispose();
            Image? thumbnail = _thumbnail.Image;
            _thumbnail.Image = null;
            thumbnail?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void UpdateModelPreview(ResourceItem resource)
    {
        _modelPreview?.Dispose();
        _modelPreview = null;
        _modelPreviewHost.Controls.Clear();
        _modelPreviewCard.Visible = false;
        if (resource.Kind is not (ResourceKind.Model or ResourceKind.GameObject)) return;
        string? projectRoot = ProjectRoot(resource.FullPath);
        if (string.IsNullOrWhiteSpace(projectRoot)) return;
        try
        {
            JObject prefab = resource.Kind == ResourceKind.GameObject
                ? JObject.Parse(File.ReadAllText(resource.FullPath))
                : new JObject
                {
                    ["dimension"] = "ThreeD",
                    ["model"] = ResourceNames.Name(projectRoot, resource.FullPath),
                    ["components"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "ModelRendererComponent",
                            ["enabled"] = true,
                            ["props"] = new JObject
                            {
                                ["ModelAsset"] = ResourceNames.Name(projectRoot, resource.FullPath),
                                ["ScaleX"] = 1f,
                                ["ScaleY"] = 1f,
                                ["ScaleZ"] = 1f,
                            },
                        },
                    },
                };
            _modelPreview = new ObjectCompositionPreviewControl(projectRoot, compact: true)
            {
                Dock = DockStyle.Fill,
                Playing = false,
            };
            _modelPreview.Reload(prefab);
            _modelPreviewHost.Controls.Add(_modelPreview);
            _modelPreviewCard.Visible = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or Newtonsoft.Json.JsonException or InvalidDataException)
        {
            _modelPreviewHost.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = ThemeService.Palette.TextMuted,
                Text = exception.Message,
                TextAlign = ContentAlignment.MiddleCenter,
            });
            _modelPreviewCard.Visible = true;
        }
    }

    private void AddContent(Control control)
    {
        int row = _content.RowCount++;
        _content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _content.Controls.Add(control, 0, row);
    }

    private void AddIdentityRow(int row, Control caption, Control value)
    {
        _identityGrid.Controls.Add(caption, 0, row);
        _identityGrid.Controls.Add(value, 1, row);
    }

    private static Label SectionHeading(string text, string name) =>
        new()
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font("Segoe UI Variable Text", 8f, FontStyle.Bold),
            Name = name,
            Text = text,
            UseMnemonic = false,
        };

    private static Label FieldCaption(string text, string name) =>
        new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Name = name,
            Text = text,
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

    private static TextBox FieldValue(string name) =>
        new()
        {
            BorderStyle = BorderStyle.None,
            Dock = DockStyle.Fill,
            Name = name,
            ReadOnly = true,
            TabStop = true,
            Text = "\u2014",
        };

    private static Label PlainValue(string name) =>
        new()
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Name = name,
            Text = "\u2014",
            TextAlign = ContentAlignment.MiddleLeft,
            UseMnemonic = false,
        };

    private static ModernButton ActionButton(string text, Action action)
    {
        ModernButton button = new()
        {
            Dock = DockStyle.Fill,
            Text = text,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        ThemeService.Apply(this);
        ApplyVisualTheme();
        ApplyDensity();
    }

    private void ApplyVisualTheme()
    {
        ThemePalette palette = ThemeService.Palette;
        BackColor = palette.Canvas;
        ForeColor = palette.Text;
        _header.BackColor = palette.Surface;
        _headerLayout.BackColor = palette.Surface;
        _searchHost.BackColor = palette.Surface;
        _propertySearch.BackColor = palette.SurfaceRaised;
        _propertySearch.ForeColor = palette.Text;
        _searchHost.Padding = new Padding(8, 6, 8, 6);
        _searchHost.Paint -= PaintSearchHostBorder;
        _searchHost.Paint += PaintSearchHostBorder;
        _preview.BackColor = palette.SurfaceHover;
        _glyph.BackColor = palette.SurfaceHover;
        _glyph.ForeColor = palette.Accent;
        _name.ForeColor = palette.Text;
        _kind.ForeColor = palette.TextMuted;
        _details.BackColor = palette.Canvas;
        _content.BackColor = palette.Canvas;
        _identityGrid.BackColor = palette.Surface;
        _pathCaption.ForeColor = palette.TextMuted;
        _guidCaption.ForeColor = palette.TextMuted;
        _modifiedCaption.ForeColor = palette.TextMuted;
        _sizeCaption.ForeColor = palette.TextMuted;
        _path.BackColor = palette.SurfaceRaised;
        _path.ForeColor = palette.Text;
        _guid.BackColor = palette.SurfaceRaised;
        _guid.ForeColor = palette.Text;
        _modified.ForeColor = palette.Text;
        _size.ForeColor = palette.Text;
        _kindSummary.BackColor = palette.Surface;
        _kindSummary.ForeColor = palette.Text;
        _propertySurface.ApplyTheme();
        _resourceIdentity.ApplyTheme();
        _actions.BackColor = palette.Canvas;
        _emptyState.BackColor = palette.Canvas;
        _emptyState.Invalidate();

        foreach (Label heading in _content.Controls.OfType<Label>()
                     .Where(label => label != _kindSummary))
        {
            heading.ForeColor = palette.TextMuted;
        }

        Invalidate(true);
    }

    private void PaintSearchHostBorder(object? sender, PaintEventArgs e)
    {
        Rectangle box = _propertySearch.Bounds;
        box.Inflate(1, 1);
        using Pen pen = new(ThemeService.Palette.Border);
        e.Graphics.DrawRectangle(pen, box);
    }

    private void ApplyDensity()
    {
        InspectorMetrics metrics = CurrentMetrics();
        _header.Height = metrics.HeaderHeight + metrics.SearchHeight;
        _searchHost.Height = metrics.SearchHeight;
        _searchHost.Padding = new Padding(metrics.OuterPadding, 0, metrics.OuterPadding, metrics.SmallGap);
        _headerLayout.Padding = new Padding(metrics.OuterPadding);
        _preview.Margin = new Padding(0, 0, metrics.OuterPadding, 0);
        _details.Padding = new Padding(metrics.OuterPadding);
        _content.Padding = Padding.Empty;

        foreach (Label heading in _content.Controls.OfType<Label>()
                     .Where(label => label != _kindSummary))
        {
            int top = heading == _content.Controls[0] ? 0 : metrics.SectionGap;
            heading.Margin = new Padding(0, top, 0, metrics.SmallGap);
        }

        _identityGrid.Padding = new Padding(metrics.IdentityPadding);
        _identityGrid.Margin = Padding.Empty;

        _kindSummary.Padding = new Padding(metrics.InnerPadding);
        _kindSummary.Margin = Padding.Empty;
        _propertySurface.Margin = Padding.Empty;
        _actions.Margin = Padding.Empty;
        foreach (ModernButton action in new[] { _reveal, _copyPath, _copyGuid })
        {
            if (Math.Abs(action.Font.SizeInPoints - 8.25f) > 0.01f)
            {
                action.Font = new Font(ThemeService.InterfaceFont.FontFamily, 8.25f, FontStyle.Regular);
            }
        }
        _reveal.Height = metrics.ActionHeight;
        _copyPath.Height = metrics.ActionHeight;
        _copyGuid.Height = metrics.ActionHeight;
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (_arranging || IsDisposed)
        {
            return;
        }

        _arranging = true;
        try
        {
            InspectorMetrics metrics = CurrentMetrics();
            int width = Math.Max(1, _details.ClientSize.Width - _details.Padding.Horizontal);
            _content.Width = width;
            _kindSummary.MaximumSize = new Size(width, 0);
            _propertySurface.Width = width;

            // ClientSize is already in the control's current device-pixel coordinate space.
            // Scaling this breakpoint again made a roomy high-DPI Inspector choose its narrow
            // single-column layout and was a direct source of the reported squashing.
            const int stackThreshold = 205;
            bool stack = width < stackThreshold;
            ArrangeIdentity(metrics, width, stack);
            _actions.Width = width;
            _actions.SuspendLayout();
            _actions.Controls.Clear();
            _actions.SetColumnSpan(_copyGuid, 1);
            _actions.ColumnStyles.Clear();
            _actions.RowStyles.Clear();
            _actions.ColumnCount = stack ? 1 : 2;
            _actions.RowCount = stack ? 3 : 2;
            _actions.Height = _actions.RowCount * (metrics.ActionHeight + metrics.SmallGap);

            for (int column = 0; column < _actions.ColumnCount; column++)
            {
                _actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,
                    100f / _actions.ColumnCount));
            }

            for (int row = 0; row < _actions.RowCount; row++)
            {
                _actions.RowStyles.Add(new RowStyle(SizeType.Absolute,
                    metrics.ActionHeight + metrics.SmallGap));
            }

            if (stack)
            {
                _actions.Controls.Add(_reveal, 0, 0);
                _actions.Controls.Add(_copyPath, 0, 1);
                _actions.Controls.Add(_copyGuid, 0, 2);
            }
            else
            {
                _actions.Controls.Add(_reveal, 0, 0);
                _actions.Controls.Add(_copyPath, 1, 0);
                _actions.Controls.Add(_copyGuid, 0, 1);
                _actions.SetColumnSpan(_copyGuid, 2);
            }

            Padding buttonMargin = new(metrics.SmallGap / 2);
            _reveal.Margin = buttonMargin;
            _copyPath.Margin = buttonMargin;
            _copyGuid.Margin = buttonMargin;
            _reveal.Height = metrics.ActionHeight;
            _copyPath.Height = metrics.ActionHeight;
            _copyGuid.Height = metrics.ActionHeight;
            _actions.ResumeLayout(performLayout: true);
        }
        finally
        {
            _arranging = false;
        }
    }

    private void ArrangeIdentity(InspectorMetrics metrics, int width, bool stack)
    {
        _identityGrid.SuspendLayout();
        _identityGrid.Controls.Clear();
        _identityGrid.ColumnStyles.Clear();
        _identityGrid.RowStyles.Clear();
        foreach (Control control in new Control[]
                 {
                     _pathCaption, _path, _guidCaption, _guid,
                     _modifiedCaption, _modified, _sizeCaption, _size,
                 })
        {
            _identityGrid.SetColumnSpan(control, 1);
        }

        if (!stack)
        {
            ArrangeWideIdentity(metrics);
            _identityGrid.ResumeLayout(performLayout: true);
            return;
        }

        int innerWidth = Math.Max(1, width - _identityGrid.Padding.Horizontal);
        int modifiedCaptionWidth = CaptionWidth(_modifiedCaption);
        int sizeCaptionWidth = CaptionWidth(_sizeCaption);
        bool stackMetadata = innerWidth
                             < (modifiedCaptionWidth + sizeCaptionWidth + (metrics.SmallGap * 4));
        _identityGrid.ColumnCount = stackMetadata ? 1 : 2;
        _identityGrid.RowCount = stackMetadata ? 8 : 6;
        for (int column = 0; column < _identityGrid.ColumnCount; column++)
        {
            _identityGrid.ColumnStyles.Add(new ColumnStyle(
                SizeType.Percent,
                100f / _identityGrid.ColumnCount));
        }

        AddAbsoluteRow(metrics.CaptionHeight);
        AddAbsoluteRow(metrics.FieldHeight);
        AddAbsoluteRow(metrics.CaptionHeight);
        AddAbsoluteRow(metrics.FieldHeight);
        AddAbsoluteRow(metrics.CaptionHeight);
        AddAbsoluteRow(metrics.MetadataHeight);
        if (stackMetadata)
        {
            AddAbsoluteRow(metrics.CaptionHeight);
            AddAbsoluteRow(metrics.MetadataHeight);
        }

        AddSpanningIdentityControl(_pathCaption, 0);
        AddSpanningIdentityControl(_path, 1);
        AddSpanningIdentityControl(_guidCaption, 2);
        AddSpanningIdentityControl(_guid, 3);
        _identityGrid.Controls.Add(_modifiedCaption, 0, 4);
        _identityGrid.Controls.Add(_modified, 0, 5);
        if (stackMetadata)
        {
            _identityGrid.Controls.Add(_sizeCaption, 0, 6);
            _identityGrid.Controls.Add(_size, 0, 7);
        }
        else
        {
            _identityGrid.Controls.Add(_sizeCaption, 1, 4);
            _identityGrid.Controls.Add(_size, 1, 5);
        }

        _pathCaption.TextAlign = ContentAlignment.BottomLeft;
        _guidCaption.TextAlign = ContentAlignment.BottomLeft;
        _modifiedCaption.TextAlign = ContentAlignment.BottomLeft;
        _sizeCaption.TextAlign = ContentAlignment.BottomLeft;
        _pathCaption.Margin = Padding.Empty;
        _guidCaption.Margin = Padding.Empty;
        _path.Margin = new Padding(0, 0, 0, metrics.SmallGap);
        _guid.Margin = new Padding(0, 0, 0, metrics.SmallGap);

        int halfGap = metrics.SmallGap / 2;
        _modifiedCaption.Margin = stackMetadata
            ? Padding.Empty
            : new Padding(0, 0, halfGap, 0);
        _modified.Margin = stackMetadata
            ? Padding.Empty
            : new Padding(0, 0, halfGap, 0);
        _sizeCaption.Margin = stackMetadata
            ? Padding.Empty
            : new Padding(halfGap, 0, 0, 0);
        _size.Margin = stackMetadata
            ? Padding.Empty
            : new Padding(halfGap, 0, 0, 0);
        _identityGrid.ResumeLayout(performLayout: true);
    }

    private void ArrangeWideIdentity(InspectorMetrics metrics)
    {
        int captionWidth = new[] { _pathCaption, _guidCaption, _modifiedCaption, _sizeCaption }
            .Max(CaptionWidth) + metrics.SmallGap + 2;
        _identityGrid.ColumnCount = 2;
        _identityGrid.RowCount = 4;
        _identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, captionWidth));
        _identityGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddAbsoluteRow(metrics.FieldHeight);
        AddAbsoluteRow(metrics.FieldHeight);
        AddAbsoluteRow(metrics.MetadataHeight);
        AddAbsoluteRow(metrics.MetadataHeight);
        AddIdentityRow(0, _pathCaption, _path);
        AddIdentityRow(1, _guidCaption, _guid);
        AddIdentityRow(2, _modifiedCaption, _modified);
        AddIdentityRow(3, _sizeCaption, _size);

        int halfGap = metrics.SmallGap / 2;
        foreach (Label caption in new[]
                 {
                     _pathCaption, _guidCaption, _modifiedCaption, _sizeCaption,
                 })
        {
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.Margin = new Padding(0, halfGap, metrics.SmallGap, halfGap);
        }

        foreach (Control value in new Control[] { _path, _guid, _modified, _size })
        {
            value.Margin = new Padding(0, halfGap, 0, halfGap);
        }
    }

    private void AddSpanningIdentityControl(Control control, int row)
    {
        _identityGrid.Controls.Add(control, 0, row);
        _identityGrid.SetColumnSpan(control, _identityGrid.ColumnCount);
    }

    private void AddAbsoluteRow(int height) =>
        _identityGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

    private static int CaptionWidth(Control caption) =>
        TextRenderer.MeasureText(
            caption.Text,
            caption.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;

    private void OnResourcePropertyEdited(object? sender, ResourceInspectorEditedEventArgs args)
    {
        if (args.Persisted)
        {
            _modified.Text = ResourceModified(args.Resource);
            _size.Text = ResourceSize(args.Resource);
            _kindSummary.Text = ResourceSummary(args.Resource);
        }
        ResourceEdited?.Invoke(this, args);
    }

    private InspectorMetrics CurrentMetrics()
    {
        InspectorMetrics metrics = InspectorMetrics.For(ThemeService.Density);
        return _dpiLayoutReady ? metrics.AtDpi(DeviceDpi) : metrics;
    }

    private void SetThumbnail(ResourceItem resource)
    {
        Image? previous = _thumbnail.Image;
        _thumbnail.Image = null;
        previous?.Dispose();

        string? path = PreviewPath(resource);
        Image? image = path is null ? null : LoadThumbnail(path);
        if (image is null && !resource.IsFolder && ProjectRoot(resource.FullPath) is { } projectRoot)
        {
            image = AssetPickerPreviewCache.Get(projectRoot,
                new ProjectAssetEntry(ResourceDisplayName.Format(resource.Name), resource.FullPath,
                    resource.RelativePath, resource.Kind), new Size(72, 72));
        }
        _thumbnail.Image = image;
        _thumbnail.Visible = image is not null;
        _glyph.Visible = image is null;
        if (image is not null)
        {
            _thumbnail.BringToFront();
        }
        else
        {
            _glyph.BringToFront();
        }
    }

    private static string? PreviewPath(ResourceItem resource)
    {
        if (resource.Kind == ResourceKind.GameObject)
        {
            string? projectRoot = ProjectRoot(resource.FullPath);
            return projectRoot is null
                ? null
                : ObjectResourceReader.ResolveImageFile(resource.FullPath, projectRoot);
        }

        if (resource.Kind == ResourceKind.Image)
        {
            return ResourceAssociates.FindPrimaryImage(resource.FullPath);
        }

        string extension = Path.GetExtension(resource.FullPath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            ? resource.FullPath
            : null;
    }

    private static string? ProjectRoot(string resourcePath)
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(resourcePath) ?? resourcePath);
        while (directory is not null)
        {
            if (directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static Image? LoadThumbnail(string path)
    {
        try
        {
            byte[] bytes = ReadBoundedBytes(path, MaxThumbnailBytes, "thumbnail");
            using MemoryStream stream = new(bytes, writable: false);
            using Image source = Image.FromStream(stream, useEmbeddedColorManagement: false,
                validateImageData: false);
            if ((long)source.Width * source.Height > MaxThumbnailPixels)
            {
                throw new InvalidDataException(
                    $"Image exceeds the Inspector's {MaxThumbnailPixels:N0}-pixel thumbnail limit.");
            }

            using Bitmap thumbnail = new(72, 72);
            using Graphics graphics = Graphics.FromImage(thumbnail);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(72f / source.Width, 72f / source.Height);
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            graphics.DrawImage(source, (72 - width) / 2, (72 - height) / 2, width, height);
            return new Bitmap(thumbnail);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or ArgumentException
                                           or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            // Selection must stay usable when associated art is missing or corrupt; the kind glyph
            // is the intentional preview fallback, while identity and diagnostics remain visible.
            return null;
        }
    }

    private void RevealResource()
    {
        if (_resource is null)
        {
            return;
        }

        string argument = _resource.IsFolder
            ? $"\"{_resource.FullPath}\""
            : $"/select,\"{_resource.FullPath}\"";
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", argument)
            {
                UseShellExecute = true,
            });
    }

    private void CopyResourcePath()
    {
        if (_resource is not null)
        {
            string path = _resource.Name;
            CopyTextToClipboard(path, _copyPath);
        }
    }

    private void CopyResourceGuid()
    {
        if (_resource is { AssetId: var id } && id != Guid.Empty)
        {
            CopyTextToClipboard(id.ToString("N"), _copyGuid);
        }
    }

    private void CopyTextToClipboard(string text, ModernButton source)
    {
        try
        {
            DataObject data = new();
            data.SetText(text, TextDataFormat.UnicodeText);
            Clipboard.SetDataObject(data, copy: true, retryTimes: 12, retryDelay: 50);
            _toolTip.Hide(source);
            _toolTip.SetToolTip(source, null);
            source.AccessibleDescription = null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            const string message = "Could not copy: the clipboard is busy. Try again.";
            source.AccessibleDescription = message;
            _toolTip.SetToolTip(source, message);
            _toolTip.Show(message, source, new Point(0, source.Height), 5000);
        }
    }

    private static string ResourceModified(ResourceItem resource)
    {
        try
        {
            if (resource.IsFolder ? !Directory.Exists(resource.FullPath) : !File.Exists(resource.FullPath))
            {
                return "Unavailable";
            }

            DateTime modified = resource.IsFolder
                ? Directory.GetLastWriteTime(resource.FullPath)
                : File.GetLastWriteTime(resource.FullPath);
            return modified.ToString("g", CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            // The tree may briefly be stale after an external rename or delete. Selection still
            // needs to work so the asset browser can refresh instead of losing the whole Inspector.
            return "Unavailable";
        }
    }

    private static string ResourceSize(ResourceItem resource)
    {
        try
        {
            if (resource.IsFolder)
            {
                if (!Directory.Exists(resource.FullPath))
                {
                    return "Unavailable";
                }

                int count = resource.Children.Count;
                return $"{count} {Plural(count, "item")}";
            }

            if (!File.Exists(resource.FullPath))
            {
                return "Unavailable";
            }

            long bytes = new FileInfo(resource.FullPath).Length;
            return bytes switch
            {
                >= 1024 * 1024 => $"{bytes / (1024d * 1024d):0.##} MB",
                >= 1024 => $"{bytes / 1024d:0.##} KB",
                _ => $"{bytes} B",
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
        {
            // Metadata is advisory. A locked or externally removed file must not make selecting its
            // stale tree row throw; the unavailable value tells the user why the details are absent.
            return "Unavailable";
        }
    }

    private static string ResourceSummary(ResourceItem resource)
    {
        if (resource.IsFolder)
        {
            int folders = resource.Children.Count(child => child.IsFolder);
            int assets = resource.Children.Count - folders;
            return $"{folders} {Plural(folders, "folder")} · {assets} {Plural(assets, "asset")}";
        }

        try
        {
            return resource.Kind switch
            {
                ResourceKind.PgslScript => TextSummary(resource.FullPath, "PGSL"),
                ResourceKind.Note => TextSummary(resource.FullPath, "note"),
                _ => JsonSummary(resource),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or InvalidDataException or JsonException
                                           or InvalidOperationException
                                           or ArgumentException or NotSupportedException
                                           or FormatException or OverflowException)
        {
            return $"Details unavailable · {exception.Message}";
        }
    }

    private static string TextSummary(string path, string kind)
    {
        string text = ReadBoundedText(path, MaxTextSummaryBytes, "text summary");
        int lines = LineCount(text);
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (kind == "PGSL")
        {
            int events = text.Split('\n').Count(line =>
                line.TrimStart().StartsWith("event ", StringComparison.OrdinalIgnoreCase));
            return $"{events} {Plural(events, "event")} · {lines} {Plural(lines, "line")}";
        }

        return $"{lines} {Plural(lines, "line")} · {words} {Plural(words, "word")}";
    }

    private static string JsonSummary(ResourceItem resource)
    {
        byte[] json = ReadBoundedBytes(resource.FullPath, MaxJsonSummaryBytes, "JSON summary");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return resource.Kind switch
        {
            ResourceKind.Image => ImageSummary(root),
            ResourceKind.Audio =>
                $"Source {Scalar(root, "source", "not assigned")} · Volume {Number(root, "volume", 1):0.##}\n"
                + $"{Flag(root, "loop", "Looping", "One shot")} · "
                + Flag(root, "spatial", "Spatial", "Non-spatial"),
            ResourceKind.Shader => ShaderSummary(root),
            ResourceKind.GameObject =>
                $"{Scalar(root, "dimension", "Unspecified")} · "
                + $"{Count(root, "components")} {Plural(Count(root, "components"), "component")} · "
                + $"{Count(root, "events")} {Plural(Count(root, "events"), "event")}\n"
                + $"Image {Scalar(root, "sprite", "not assigned")} · Model {Scalar(root, "model", "not assigned")}",
            ResourceKind.Room => RoomSummary(root),
            ResourceKind.Model => ModelSummary(root),
            ResourceKind.Particle =>
                $"{Number(root, "duration", 0):0.##} s · {Flag(root, "loop", "Looping", "One shot")} · "
                + $"{NestedNumber(root, "emission", "rate", 0):0.##}/s\n"
                + $"Emitter {NestedScalar(root, "shape", "type", "Unspecified")}",
            ResourceKind.Physics =>
                $"Friction {Number(root, "friction", 0):0.##} · "
                + $"Restitution {Number(root, "restitution", 0):0.##}\n"
                + $"Density {Number(root, "density", 0):0.##}",
            ResourceKind.Terrain => TerrainSummary(root),
            ResourceKind.TerrainEntity =>
                $"{Scalar(root, "type", "Unspecified")} · "
                + $"{Count(root, "components")} {Plural(Count(root, "components"), "component")}",
            ResourceKind.Pathing => PathingSummary(root),
            _ => $"{Path.GetExtension(resource.FullPath).TrimStart('.').ToUpperInvariant()} resource",
        };
    }

    private static string PathingSummary(JsonElement root)
    {
        if (!root.TryGetProperty("route", out JsonElement route) || route.ValueKind != JsonValueKind.Object)
            return "Navigation route";
        int waypoints = route.TryGetProperty("waypoints", out JsonElement points) && points.ValueKind == JsonValueKind.Array
            ? points.GetArrayLength() : 0;
        string mode = Scalar(route, "mode", "WaypointPatrol");
        string loop = Scalar(route, "loopMode", "Loop");
        return $"{mode} · {loop} · {waypoints} {Plural(waypoints, "waypoint")}\n"
            + $"Room {Scalar(root, "targetRoom", "not assigned")} · Object {Scalar(root, "targetObject", "not assigned")}";
    }

    private static string ShaderSummary(JsonElement root)
    {
        string source = Scalar(root, "source", string.Empty);
        int parameters = Count(root, "parameters");
        return $"{Scalar(root, "pipeline", "Unspecified")} pipeline · "
               + $"Entry {Scalar(root, "entry", "not assigned")} · "
               + $"Profile {Scalar(root, "profile", "not assigned")}\n"
               + $"{LineCount(source)} {Plural(LineCount(source), "line")} · "
               + $"{source.Length} {Plural(source.Length, "character")} · "
               + $"{parameters} {Plural(parameters, "parameter")}";
    }

    private static string ModelSummary(JsonElement root)
    {
        int parts = Count(root, "parts");
        int animations = Count(root, "animations");
        string rig = Scalar(root, "rigTemplate", string.Empty);
        if (string.IsNullOrWhiteSpace(rig))
        {
            rig = Scalar(root, "rig", "not assigned");
        }

        return $"{parts} {Plural(parts, "part")} · "
               + $"{animations} {Plural(animations, "animation")}\n"
               + $"Rig {rig}";
    }

    private static string ImageSummary(JsonElement root)
    {
        JsonElement canvas = Object(root, "canvas");
        int width = Integer(canvas, "width");
        int height = Integer(canvas, "height");
        JsonElement usage = Object(root, "usage");
        string roles = Scalar(usage, "allowed", "Unspecified");
        int frames = Count(root, "frames");
        int layers = Count(root, "layers");
        return $"{width} × {height} px · {roles}\n"
               + $"{frames} {Plural(frames, "frame")} · {layers} {Plural(layers, "layer")}";
    }

    private static string RoomSummary(JsonElement root)
    {
        JsonElement settings = Object(root, "settings");
        int width = Integer(settings, "width");
        int height = Integer(settings, "height");
        double grid = Number(settings, "gridSize", 0);
        int layers = Count(root, "layers");
        int nodes = Count(root, "nodes");
        return $"{width} × {height} px · Grid {grid:0.##}\n"
               + $"{layers} {Plural(layers, "layer")} · {nodes} {Plural(nodes, "node")}";
    }

    private static string TerrainSummary(JsonElement root)
    {
        JsonElement resolution = Array(root, "resolution");
        int width = resolution.GetArrayLength() > 0 ? resolution[0].GetInt32() : 0;
        int height = resolution.GetArrayLength() > 1 ? resolution[1].GetInt32() : 0;
        return $"{width} × {height} cells · {Number(root, "cellSize", 0):0.##} unit cell\n"
               + $"Height {Number(root, "minHeight", 0):0.##}–{Number(root, "maxHeight", 0):0.##} · "
               + $"{Count(root, "layers")} {Plural(Count(root, "layers"), "layer")}";
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static JsonElement Array(JsonElement parent, string name) =>
        TryGetProperty(parent, name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value
            : default;

    private static int Count(JsonElement parent, string name)
    {
        if (!TryGetProperty(parent, name, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.Object => value.EnumerateObject().Count(),
            _ => 0,
        };
    }

    private static int Integer(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && TryGetProperty(parent, name, out JsonElement value)
        && value.TryGetInt32(out int result)
            ? result
            : 0;

    private static double Number(JsonElement parent, string name, double fallback) =>
        parent.ValueKind == JsonValueKind.Object
        && TryGetProperty(parent, name, out JsonElement value)
        && value.TryGetDouble(out double result)
            ? result
            : fallback;

    private static double NestedNumber(
        JsonElement parent,
        string objectName,
        string propertyName,
        double fallback) =>
        Number(Object(parent, objectName), propertyName, fallback);

    private static string Scalar(JsonElement parent, string name, string fallback)
    {
        if (!TryGetProperty(parent, name, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString())
                ? fallback
                : value.GetString()!,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => fallback,
        };
    }

    private static string NestedScalar(
        JsonElement parent,
        string objectName,
        string propertyName,
        string fallback) =>
        Scalar(Object(parent, objectName), propertyName, fallback);

    private static string Flag(JsonElement parent, string name, string whenTrue, string whenFalse) =>
        parent.ValueKind == JsonValueKind.Object
        && TryGetProperty(parent, name, out JsonElement value)
        && value.ValueKind is JsonValueKind.True
            ? whenTrue
            : whenFalse;

    private static bool TryGetProperty(
        JsonElement parent,
        string name,
        out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object)
        {
            if (parent.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (JsonProperty property in parent.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static byte[] ReadBoundedBytes(string path, int limit, string purpose)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > limit)
        {
            throw new InvalidDataException(
                $"File exceeds the Inspector's {limit / (1024 * 1024)} MB {purpose} limit.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string ReadBoundedText(string path, int limit, string purpose)
    {
        byte[] bytes = ReadBoundedBytes(path, limit, purpose);
        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static int LineCount(string text) =>
        text.Length == 0 ? 0 : text.Count(character => character == '\n') + 1;

    private static string Plural(int count, string noun) => count == 1 ? noun : noun + "s";

    private sealed record InspectorMetrics(
        int OuterPadding,
        int InnerPadding,
        int IdentityPadding,
        int SmallGap,
        int SectionGap,
        int HeaderHeight,
        int SearchHeight,
        int CaptionHeight,
        int FieldHeight,
        int MetadataHeight,
        int ActionHeight)
    {
        public static InspectorMetrics For(string density) => density switch
        {
            "Compact" => new(10, 7, 4, 4, 8, 84, 31, 16, 30, 24, 30),
            "Spacious" => new(18, 14, 8, 10, 18, 122, 48, 24, 44, 34, 44),
            _ => new(14, 10, 6, 7, 13, 102, 39, 20, 36, 28, 36),
        };

        public InspectorMetrics AtDpi(int dpi) => new(
            DpiLayout.Scale(OuterPadding, dpi),
            DpiLayout.Scale(InnerPadding, dpi),
            DpiLayout.Scale(IdentityPadding, dpi),
            DpiLayout.Scale(SmallGap, dpi),
            DpiLayout.Scale(SectionGap, dpi),
            DpiLayout.Scale(HeaderHeight, dpi),
            DpiLayout.Scale(SearchHeight, dpi),
            DpiLayout.Scale(CaptionHeight, dpi),
            DpiLayout.Scale(FieldHeight, dpi),
            DpiLayout.Scale(MetadataHeight, dpi),
            DpiLayout.Scale(ActionHeight, dpi));

    }
}
