using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>
/// What Select / Erase / brushes are allowed to hit. Multi-check is allowed; the default
/// follows the active mode or the tree group you last clicked.
/// </summary>
[Flags]
public enum TerrainAffects
{
    None = 0,
    Heightfield = 1,
    Paint = 2,
    Paths = 4,
    Water = 8,
    Foliage = 16,
    Objects = 32,
    POI = 64,
}

/// <summary>Compact chip strip that lives under the component tree — not over the viewport.</summary>
public sealed class TerrainAffectsStrip : Panel
{
    private static readonly (TerrainAffects Flag, string Label)[] Chips =
    [
        (TerrainAffects.Heightfield, "Heightfield"),
        (TerrainAffects.Paint, "Paint"),
        (TerrainAffects.Paths, "Paths"),
        (TerrainAffects.Water, "Water"),
        (TerrainAffects.Foliage, "Foliage"),
        (TerrainAffects.Objects, "Objects"),
        (TerrainAffects.POI, "POI"),
    ];

    private readonly Dictionary<TerrainAffects, Button> _buttons = [];
    private TerrainAffects _value = TerrainAffects.Heightfield;
    private bool _suppress;

    public TerrainAffectsStrip()
    {
        BackColor = EditorChrome.Surface;
        Dock = DockStyle.Bottom;
        Height = 78;
        Padding = new Padding(8, 4, 8, 6);

        Label caption = new()
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = EditorChrome.HeadingFont,
            ForeColor = EditorChrome.Muted,
            Height = 18,
            Text = "AFFECTS",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        FlowLayoutPanel row = new()
        {
            AutoScroll = false,
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = Padding.Empty,
            WrapContents = true,
        };

        foreach ((TerrainAffects flag, string label) in Chips)
        {
            TerrainAffects captured = flag;
            Button button = new()
            {
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Font = EditorChrome.SmallFont,
                Margin = new Padding(0, 0, 4, 4),
                Padding = new Padding(6, 2, 6, 2),
                Text = label,
            };
            button.FlatAppearance.BorderSize = 1;
            button.Click += (_, _) => Toggle(captured);
            _buttons[flag] = button;
            row.Controls.Add(button);
        }

        Controls.Add(row);
        Controls.Add(caption);
        SyncButtons();
    }

    public event EventHandler? Changed;

    public TerrainAffects Value
    {
        get => _value;
        set
        {
            TerrainAffects next = value == TerrainAffects.None ? TerrainAffects.Heightfield : value;
            if (_value == next)
            {
                return;
            }

            _value = next;
            SyncButtons();
            if (!_suppress)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public static TerrainAffects ForMode(TerrainEditorControl.TerrainEditorMode mode) => mode switch
    {
        TerrainEditorControl.TerrainEditorMode.Paint => TerrainAffects.Paint,
        TerrainEditorControl.TerrainEditorMode.Paths => TerrainAffects.Paths,
        TerrainEditorControl.TerrainEditorMode.Foliage => TerrainAffects.Foliage,
        TerrainEditorControl.TerrainEditorMode.Water => TerrainAffects.Water,
        TerrainEditorControl.TerrainEditorMode.Entities => TerrainAffects.Objects,
        _ => TerrainAffects.Heightfield,
    };

    public static TerrainAffects ForKind(TerrainComponentsPanel.ComponentKind kind) => kind switch
    {
        TerrainComponentsPanel.ComponentKind.Layer => TerrainAffects.Paint,
        TerrainComponentsPanel.ComponentKind.Path => TerrainAffects.Paths,
        TerrainComponentsPanel.ComponentKind.Water => TerrainAffects.Water,
        TerrainComponentsPanel.ComponentKind.Foliage => TerrainAffects.Foliage,
        TerrainComponentsPanel.ComponentKind.PointOfInterest => TerrainAffects.POI,
        TerrainComponentsPanel.ComponentKind.Entity => TerrainAffects.Objects,
        _ => TerrainAffects.Heightfield,
    };

    public static TerrainAffects ForEntityType(TerrainEntityType type) => type switch
    {
        TerrainEntityType.Foliage or TerrainEntityType.Tree => TerrainAffects.Foliage,
        TerrainEntityType.Fluid => TerrainAffects.Water,
        TerrainEntityType.Terrain => TerrainAffects.Objects,
        TerrainEntityType.Environment => TerrainAffects.Objects,
        _ => TerrainAffects.Objects,
    };

    public string StatusLabel
    {
        get
        {
            List<string> parts = [];
            foreach ((TerrainAffects flag, string label) in Chips)
            {
                if (_value.HasFlag(flag))
                {
                    parts.Add(label);
                }
            }

            return parts.Count == 0 ? "Heightfield" : string.Join(" + ", parts);
        }
    }

    private void Toggle(TerrainAffects flag)
    {
        TerrainAffects next = _value.HasFlag(flag) ? _value & ~flag : _value | flag;
        Value = next;
    }

    private void SyncButtons()
    {
        _suppress = true;
        foreach ((TerrainAffects flag, Button button) in _buttons)
        {
            bool on = _value.HasFlag(flag);
            button.BackColor = on ? EditorChrome.Accent : EditorChrome.Raised;
            button.ForeColor = on ? Color.White : EditorChrome.Text;
            button.FlatAppearance.BorderColor = on ? EditorChrome.Accent : EditorChrome.Border;
        }

        _suppress = false;
    }
}
