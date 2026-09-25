using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// Parts owned by the current terrain, grouped by <see cref="TerrainEntityType"/> with
/// per-section creation and per-row editing/deletion. The optional legacy library fallback
/// keeps existing standalone entity files readable while callers migrate to terrain ownership.
/// </summary>
public sealed class TerrainEntityListPanel : Panel
{
    private sealed class LoadedEntity
    {
        public required string Path;
        public required TerrainEntityDocument Document;
    }

    private static readonly TerrainEntityType[] GroupOrder =
    [
        TerrainEntityType.Terrain,
        TerrainEntityType.Foliage,
        TerrainEntityType.Object,
        TerrainEntityType.Fluid,
        TerrainEntityType.Environment,
    ];

    private static readonly JsonSerializerOptions DocumentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _projectRoot;
    private readonly Func<IEnumerable<string>>? _ownedPaths;
    private readonly Panel _scroll;
    private readonly Dictionary<TerrainEntityType, bool> _expanded = GroupOrder.ToDictionary(t => t, _ => true);
    private readonly Dictionary<string, Bitmap> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _toolTip = new();
    private bool _useDeviceScale;

    public TerrainEntityListPanel(string projectRoot, Func<IEnumerable<string>>? ownedPaths = null)
    {
        _projectRoot = projectRoot;
        _ownedPaths = ownedPaths;
        Dock = DockStyle.Fill;
        BackColor = EditorChrome.Surface;

        _scroll = new Panel
        {
            AutoScroll = true,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
        };
        Controls.Add(_scroll);
        EditorChrome.Changed += (_, _) => { if (!IsDisposed) Rebuild(); };
        Rebuild();
    }

    /// <summary>Raised when a group's [+] is clicked — the caller opens the creation wizard.</summary>
    public event EventHandler<TerrainEntityType>? CreateRequested;

    /// <summary>Raised when an item's Edit is clicked with the entity's resource path.</summary>
    public event EventHandler<string>? EditRequested;

    public void RefreshEntities() => Rebuild();

    private void Rebuild()
    {
        foreach (Control control in _scroll.Controls.OfType<Control>().ToArray())
        {
            control.Dispose();
        }

        _scroll.Controls.Clear();

        List<LoadedEntity> entities = LoadAll();
        int y = 0;
        int width = Math.Max(S(160), _scroll.ClientSize.Width - (_scroll.VerticalScroll.Visible ? S(18) : 0));

        foreach (TerrainEntityType type in GroupOrder)
        {
            List<LoadedEntity> group = entities.Where(e => e.Document.Type == type).ToList();
            y += AddGroupHeader(type, group.Count, y, width);
            if (_expanded[type])
            {
                foreach (LoadedEntity entity in group)
                {
                    y += AddItemRow(entity, y, width);
                }
            }
        }

        _scroll.AutoScrollMinSize = new Size(0, y);
    }

    private List<LoadedEntity> LoadAll()
    {
        List<LoadedEntity> results = [];
        IEnumerable<string> paths = _ownedPaths?.Invoke()
            ?? ProjectAssetIndex.Enumerate(_projectRoot, ResourceKind.TerrainEntity).Select(entry => entry.FullPath);
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string json = File.ReadAllText(path);
                TerrainEntityDocument? document = path.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase)
                    ? TerrainObjectResourceBridge.Load(_projectRoot, path) : JsonSerializer.Deserialize<TerrainEntityDocument>(json, DocumentJsonOptions);
                if (document is not null)
                {
                    results.Add(new LoadedEntity { Path = path, Document = document });
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // Skip unreadable entries rather than breaking the whole library view.
            }
        }

        results.Sort((a, b) => string.Compare(a.Document.Name, b.Document.Name, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private const int HeaderHeight = 30;
    private const int RowHeight = 32;

    private int S(int logicalPixels) =>
        _useDeviceScale ? DpiLayout.Scale(this, logicalPixels) : logicalPixels;

    private int AddGroupHeader(TerrainEntityType type, int count, int y, int width)
    {
        int headerHeight = S(HeaderHeight);
        Panel header = new()
        {
            BackColor = EditorChrome.Raised,
            Location = new Point(0, y),
            Size = new Size(width, headerHeight),
            Cursor = Cursors.Hand,
            Tag = type,
        };
        header.Paint += (_, e) => PaintChevron(e.Graphics, S(8), headerHeight / 2, _expanded[type]);
        Label label = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Text,
            Location = new Point(S(24), 0),
            Size = new Size(width - S(60), headerHeight),
            Text = $"{type.ToString().ToUpperInvariant()}  ({count})",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Button addButton = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(width - S(30), S(3)),
            Size = new Size(S(24), S(24)),
            Text = "+",
            TabStop = false,
        };
        EditorChrome.StyleField(addButton);
        addButton.FlatAppearance.BorderSize = 0;
        addButton.Click += (_, _) => CreateRequested?.Invoke(this, type);

        void Toggle()
        {
            _expanded[type] = !_expanded[type];
            Rebuild();
        }

        header.Click += (_, _) => Toggle();
        label.Click += (_, _) => Toggle();
        header.Controls.Add(label);
        header.Controls.Add(addButton);
        _scroll.Controls.Add(header);
        return headerHeight;
    }

    private int AddItemRow(LoadedEntity entity, int y, int width)
    {
        int rowHeight = S(RowHeight);
        Panel row = new()
        {
            BackColor = EditorChrome.Surface,
            Location = new Point(0, y),
            Size = new Size(width, rowHeight),
        };

        PictureBox icon = new()
        {
            BackColor = EditorChrome.Canvas,
            Location = new Point(S(8), S(3)),
            Size = new Size(rowHeight - S(6), rowHeight - S(6)),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = ResolveIcon(entity.Document),
        };
        Label name = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = EditorChrome.BaseFont,
            ForeColor = EditorChrome.Text,
            Location = new Point(rowHeight + S(6), 0),
            Size = new Size(width - rowHeight - S(74), rowHeight),
            Text = entity.Document.Name,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Button editButton = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            Location = new Point(width - S(62), S(3)),
            Size = new Size(S(26), S(26)),
            Text = "✎",
            TabStop = false,
        };
        Button deleteButton = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            ForeColor = EditorChrome.Error,
            Location = new Point(width - S(32), S(3)),
            Size = new Size(S(26), S(26)),
            Text = "✕",
            TabStop = false,
        };
        EditorChrome.StyleField(editButton);
        EditorChrome.StyleField(deleteButton);
        editButton.FlatAppearance.BorderSize = 0;
        deleteButton.FlatAppearance.BorderSize = 0;
        deleteButton.ForeColor = EditorChrome.Error;
        _toolTip.SetToolTip(editButton, "Edit");
        _toolTip.SetToolTip(deleteButton, "Delete");

        string path = entity.Path;
        editButton.Click += (_, _) => EditRequested?.Invoke(this, path);
        deleteButton.Click += (_, _) => DeleteEntity(path, entity.Document.Name);

        row.Controls.Add(icon);
        row.Controls.Add(name);
        row.Controls.Add(editButton);
        row.Controls.Add(deleteButton);
        _scroll.Controls.Add(row);
        return rowHeight;
    }

    private void DeleteEntity(string path, string name)
    {
        DialogResult result = Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(
            this,
            $"Delete terrain entity '{name}'? It moves to the project trash.",
            "Delete Terrain Entity",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ResourceService resources = ProjectAssetIndex.OpenResourceService(_projectRoot);
            resources.MoveToTrash(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Genesis.Application.Editors.Image.Dialogs.ThemeMessageBox.Show(this, $"Could not delete: {exception.Message}", "Delete Terrain Entity", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Rebuild();
    }

    private Bitmap? ResolveIcon(TerrainEntityDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Icon))
        {
            return null;
        }

        if (_iconCache.TryGetValue(document.Icon, out Bitmap? cached))
        {
            return cached;
        }

        string? imagePath = ProjectAssetIndex.ResolveSpriteImage(_projectRoot, document.Icon);
        if (imagePath is null || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(imagePath);
            Bitmap bitmap = new(stream);
            _iconCache[document.Icon] = bitmap;
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    private void PaintChevron(Graphics graphics, int centerX, int centerY, bool expanded)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using SolidBrush brush = new(EditorChrome.Muted);
        Point[] points = expanded
            ?
            [
                new Point(centerX - S(4), centerY - S(3)),
                new Point(centerX + S(4), centerY - S(3)),
                new Point(centerX, centerY + S(4)),
            ]
            :
            [
                new Point(centerX - S(3), centerY - S(5)),
                new Point(centerX + S(4), centerY),
                new Point(centerX - S(3), centerY + S(5)),
            ];
        graphics.FillPolygon(brush, points);
    }

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        _useDeviceScale = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Bitmap bitmap in _iconCache.Values)
            {
                bitmap.Dispose();
            }

            _iconCache.Clear();
        }

        base.Dispose(disposing);
    }
}

