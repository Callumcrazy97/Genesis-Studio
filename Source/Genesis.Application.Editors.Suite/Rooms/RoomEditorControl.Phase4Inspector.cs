using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private readonly PropertyGrid _kindInspector = new();

    private void BuildPhase4Inspector(int y)
    {
        Label heading = new()
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Muted,
            Location = new Point(12, y),
            Text = "NODE DETAILS",
        };
        _inspectorPanel.Controls.Add(heading);
        y += 24;

        _kindInspector.BackColor = EditorChrome.Surface;
        _kindInspector.CommandsBackColor = EditorChrome.Surface;
        _kindInspector.CommandsForeColor = EditorChrome.Text;
        _kindInspector.HelpBackColor = EditorChrome.Surface;
        _kindInspector.HelpForeColor = EditorChrome.Text;
        _kindInspector.HelpVisible = false;
        _kindInspector.LineColor = EditorChrome.Raised;
        _kindInspector.Location = new Point(8, y);
        _kindInspector.Name = "RoomNodePropertyGrid";
        _kindInspector.PropertySort = PropertySort.Categorized;
        _kindInspector.Size = new Size(276, 360);
        _kindInspector.ToolbarVisible = false;
        _kindInspector.ViewBackColor = EditorChrome.Surface;
        _kindInspector.ViewForeColor = EditorChrome.Text;
        _inspectorPanel.Controls.Add(_kindInspector);
        _inspectorPanel.AutoScrollMinSize = new Size(0, y + _kindInspector.Height + 12);
    }

    private void SyncPhase4Inspector()
    {
        if (_selected is null)
        {
            _kindInspector.SelectedObject = null;
            _kindInspector.Enabled = false;
            return;
        }

        _kindInspector.Enabled = !IsNodeLocked(_selected);
        object? selectedTile = TileInspectorObject();
        _kindInspector.SelectedObject = selectedTile ?? _selected.Kind switch
        {
            RoomNodeKind.Background => new BackgroundInspector(this, _selected),
            RoomNodeKind.TileLayer => new TileLayerInspector(this, _selected),
            RoomNodeKind.Terrain => new TerrainInspector(this, _selected),
            _ => new GameObjectInspector(this, _selected),
        };
    }

    private bool SetNodeValue<T>(
        RoomNode node,
        string label,
        Func<T> read,
        Action<T> write,
        T value)
    {
        if (!CanEditNodeInActiveContext(node)) return false;
        T before = read();
        if (EqualityComparer<T>.Default.Equals(before, value)) return false;
        write(value);
        PushEdit(
            label,
            () => { write(value); RefreshPhase4Ui(); },
            () => { write(before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetNodeDimensionVisibility(RoomNode node, RoomDimension dimension, bool enabled)
    {
        return dimension == RoomDimension.TwoD
            ? SetNodeValue(node, $"Set '{node.Name}' 2D visibility", () => node.EnabledIn2D, value => node.EnabledIn2D = value, enabled)
            : SetNodeValue(node, $"Set '{node.Name}' 3D visibility", () => node.EnabledIn3D, value => node.EnabledIn3D = value, enabled);
    }

    public bool SetBackgroundLayout(RoomNode node, RoomBackgroundLayout layout)
    {
        if (node.Background is null) return false;
        return SetNodeValue(node, $"Set '{node.Name}' layout", () => node.Background.Layout, value => node.Background.Layout = value, layout);
    }

    public bool SetBackgroundMode(RoomNode node, RoomBackgroundMode mode)
    {
        if (node.Background is null) return false;
        return SetNodeValue(node, $"Set '{node.Name}' mode", () => node.Background.Mode, value => node.Background.Mode = value, mode);
    }

    public bool SetBackgroundTint(RoomNode node, int tintArgb)
    {
        if (node.Background is null) return false;
        return SetNodeValue(node, $"Set '{node.Name}' tint", () => node.Background.TintArgb, value => node.Background.TintArgb = value, tintArgb);
    }

    public bool SetBackgroundOpacity(RoomNode node, float opacity)
    {
        if (node.Background is null) return false;
        float value = Math.Clamp(opacity, 0f, 1f);
        return SetNodeValue(node, $"Set '{node.Name}' opacity", () => node.Background.Opacity, next => node.Background.Opacity = next, value);
    }

    public bool SetBackgroundRepeat(RoomNode node, bool repeatX, bool repeatY)
    {
        if (node.Background is null || !CanEditNodeInActiveContext(node)) return false;
        (bool X, bool Y) before = (node.Background.RepeatX, node.Background.RepeatY);
        (bool X, bool Y) after = (repeatX, repeatY);
        if (before == after) return false;
        void Write((bool X, bool Y) value)
        {
            node.Background.RepeatX = value.X;
            node.Background.RepeatY = value.Y;
        }
        Write(after);
        PushEdit(
            $"Set '{node.Name}' repeat",
            () => { Write(after); RefreshPhase4Ui(); },
            () => { Write(before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetTileLayerCellSize(RoomNode node, int width, int height)
    {
        if (node.TileLayer is null || !CanEditNodeInActiveContext(node)) return false;
        (int Width, int Height) before = (node.TileLayer.CellWidth, node.TileLayer.CellHeight);
        (int Width, int Height) after = (Math.Clamp(width, 1, 4096), Math.Clamp(height, 1, 4096));
        if (before == after) return false;
        void Write((int Width, int Height) value)
        {
            node.TileLayer.CellWidth = value.Width;
            node.TileLayer.CellHeight = value.Height;
        }
        Write(after);
        PushEdit(
            $"Set '{node.Name}' cell size",
            () => { Write(after); RefreshPhase4Ui(); },
            () => { Write(before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
        return true;
    }

    public bool SetTileLayerCollision(RoomNode node, bool enabled)
    {
        if (node.TileLayer is null) return false;
        return SetNodeValue(node, $"Set '{node.Name}' collision", () => node.TileLayer.CollisionEnabled, value => node.TileLayer.CollisionEnabled = value, enabled);
    }

    private void SetRoomDimensions(int width, int height)
    {
        width = Math.Clamp(width, 1, 100_000);
        height = Math.Clamp(height, 1, 100_000);
        (int Width, int Height) before = (_room.Settings.Width, _room.Settings.Height);
        (int Width, int Height) after = (width, height);
        if (before == after) return;
        void Write((int Width, int Height) value)
        {
            _room.Settings.Width = value.Width;
            _room.Settings.Height = value.Height;
        }
        Write(after);
        PushEdit(
            "Resize room",
            () => { Write(after); RefreshPhase4Ui(); },
            () => { Write(before); RefreshPhase4Ui(); });
        RefreshPhase4Ui();
    }

    private abstract class NodeInspector
    {
        protected NodeInspector(RoomEditorControl editor, RoomNode node)
        {
            Editor = editor;
            Node = node;
        }

        protected RoomEditorControl Editor { get; }
        protected RoomNode Node { get; }

        [Category("Selection"), DisplayName("Selected count"), ReadOnly(true)]
        public int SelectedCount => Editor._selection.Count;

        [Category("Selection"), ReadOnly(true)]
        public bool LayerLocked => Editor.IsNodeLocked(Node);

        [Category("Placement"), Description("Lower depth values draw nearer in 2D.")]
        public int Depth
        {
            get => Editor.NodeDepth(Node);
            set => Editor.SetNodeDepth(Node, value);
        }

        [Category("Placement"), DisplayName("Visible in 2D")]
        public bool EnabledIn2D
        {
            get => Node.EnabledIn2D;
            set => Editor.SetNodeDimensionVisibility(Node, RoomDimension.TwoD, value);
        }

        [Category("Placement"), DisplayName("Visible in 3D")]
        public bool EnabledIn3D
        {
            get => Node.EnabledIn3D;
            set => Editor.SetNodeDimensionVisibility(Node, RoomDimension.ThreeD, value);
        }
    }

    private sealed class GameObjectInspector : NodeInspector
    {
        public GameObjectInspector(RoomEditorControl editor, RoomNode node) : base(editor, node) { }

        [Category("Resource"), ReadOnly(true)]
        public string Prefab => Node.GameObject?.Prefab ?? string.Empty;

        [Category("Resource"), DisplayName("Override groups"), ReadOnly(true)]
        public int OverrideGroups => Node.GameObject?.ComponentOverrides.Count ?? 0;
    }

    private sealed class BackgroundInspector : NodeInspector
    {
        public BackgroundInspector(RoomEditorControl editor, RoomNode node) : base(editor, node) { }
        private RoomBackgroundData Data => Node.Background!;

        [Category("Resource"), ReadOnly(true)]
        public string Asset => Data.Asset;

        [Category("Background")]
        public RoomBackgroundMode Mode
        {
            get => Data.Mode;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' mode", () => Data.Mode, next => Data.Mode = next, value);
        }

        [Category("Background")]
        public RoomBackgroundLayout Layout
        {
            get => Data.Layout;
            set => Editor.SetBackgroundLayout(Node, value);
        }

        [Category("Background"), DisplayName("Scroll X")]
        public float ScrollX
        {
            get => Data.Scroll is { Length: > 0 } ? Data.Scroll[0] : 0f;
            set => Editor.SetBackgroundScroll(Node, value, ScrollY);
        }

        [Category("Background"), DisplayName("Scroll Y")]
        public float ScrollY
        {
            get => Data.Scroll is { Length: > 1 } ? Data.Scroll[1] : 0f;
            set => Editor.SetBackgroundScroll(Node, ScrollX, value);
        }

        [Category("Background"), DisplayName("Repeat X")]
        public bool RepeatX
        {
            get => Data.RepeatX;
            set => Editor.SetBackgroundRepeat(Node, value, Data.RepeatY);
        }

        [Category("Background"), DisplayName("Repeat Y")]
        public bool RepeatY
        {
            get => Data.RepeatY;
            set => Editor.SetBackgroundRepeat(Node, Data.RepeatX, value);
        }

        [Category("Background")]
        public float Opacity
        {
            get => Data.Opacity;
            set => Editor.SetBackgroundOpacity(Node, value);
        }

        [Category("Background"), DisplayName("Tint (#AARRGGBB)")]
        public string Tint
        {
            get => $"#{unchecked((uint)Data.TintArgb):X8}";
            set
            {
                string text = value?.Trim().TrimStart('#') ?? string.Empty;
                if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                {
                    Editor.SetNodeValue(Node, $"Set '{Node.Name}' tint", () => Data.TintArgb, next => Data.TintArgb = next, unchecked((int)argb));
                }
            }
        }

        [Category("Background"), DisplayName("Depth test")]
        public bool DepthTest
        {
            get => Data.DepthTest;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' depth test", () => Data.DepthTest, next => Data.DepthTest = next, value);
        }
    }

    private sealed class TileLayerInspector : NodeInspector
    {
        public TileLayerInspector(RoomEditorControl editor, RoomNode node) : base(editor, node) { }
        private RoomTileLayerData Data => Node.TileLayer!;

        [Category("Resource"), ReadOnly(true)]
        public string Tileset => Data.Tileset;

        [Category("Tiles"), DisplayName("Cell width")]
        public int CellWidth
        {
            get => Data.CellWidth;
            set => Editor.SetTileLayerCellSize(Node, value, Data.CellHeight);
        }

        [Category("Tiles"), DisplayName("Cell height")]
        public int CellHeight
        {
            get => Data.CellHeight;
            set => Editor.SetTileLayerCellSize(Node, Data.CellWidth, value);
        }

        [Category("Tiles")]
        public int Margin
        {
            get => Data.Margin;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' margin", () => Data.Margin, next => Data.Margin = Math.Max(0, next), Math.Max(0, value));
        }

        [Category("Tiles")]
        public int Separation
        {
            get => Data.Separation;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' separation", () => Data.Separation, next => Data.Separation = Math.Max(0, next), Math.Max(0, value));
        }

        [Category("Tiles"), DisplayName("Collision enabled")]
        public bool CollisionEnabled
        {
            get => Data.CollisionEnabled;
            set => Editor.SetTileLayerCollision(Node, value);
        }

        [Category("Tiles"), DisplayName("Painted cells"), ReadOnly(true)]
        public int PaintedCells => Data.Cells.Count;
    }

    private sealed class TerrainInspector : NodeInspector
    {
        public TerrainInspector(RoomEditorControl editor, RoomNode node) : base(editor, node) { }
        private RoomTerrainData Data => Node.Terrain!;

        [Category("Resource"), ReadOnly(true)]
        public string Asset => Data.Asset;

        [Category("Terrain")]
        public string Material
        {
            get => Data.Material;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' material", () => Data.Material, next => Data.Material = next ?? string.Empty, value ?? string.Empty);
        }

        [Category("Terrain")]
        public string Albedo
        {
            get => Data.Albedo;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' albedo", () => Data.Albedo, next => Data.Albedo = next ?? string.Empty, value ?? string.Empty);
        }

        [Category("Terrain"), DisplayName("UV scale")]
        public float UvScale
        {
            get => Data.UvScale;
            set => Editor.SetNodeValue(Node, $"Set '{Node.Name}' UV scale", () => Data.UvScale, next => Data.UvScale = Math.Clamp(next, .01f, 10_000f), Math.Clamp(value, .01f, 10_000f));
        }
    }
}
