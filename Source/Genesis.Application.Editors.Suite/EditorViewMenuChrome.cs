using System.Drawing;
using System.Numerics;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Shared View-menu toggles for 3D editor viewports (grid, floor styles, diagnostic overlays).
/// </summary>
internal static class EditorViewMenuChrome
{
    internal sealed class GridBinding
    {
        public required Func<bool> Read { get; init; }
        public required Action<bool> Write { get; init; }
        public required Action Invalidate { get; init; }
    }

    internal sealed class ToggleBinding
    {
        public required Func<bool> Read { get; init; }
        public required Action<bool> Write { get; init; }
        public Action? Invalidate { get; init; }
    }

    internal sealed class FloorStyleBinding
    {
        public required Func<EditorFloorStyle> Read { get; init; }
        public required Action<EditorFloorStyle> Write { get; init; }
        public Action? Invalidate { get; init; }
    }

    public static ToolStripDropDownButton BuildViewMenu(
        string tooltip,
        GridBinding? grid = null,
        ToggleBinding? floor = null,
        FloorStyleBinding? floorStyle = null,
        ToggleBinding? wireframe = null,
        ToggleBinding? fog = null,
        ToggleBinding? isolateSelection = null,
        Editor3DSession? session = null,
        IEnumerable<ToolStripItem>? leadingItems = null,
        IEnumerable<ToolStripItem>? extraItems = null,
        Func<EditorViewport3D?>? viewport = null)
    {
        ToolStripDropDownButton menu = new("View")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = tooltip,
        };

        if (leadingItems is not null)
        {
            foreach (ToolStripItem leading in leadingItems)
            {
                menu.DropDownItems.Add(leading);
            }

            if (menu.DropDownItems.Count > 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
            }
        }

        if (session is not null)
        {
            menu.DropDownItems.Add(BuildGridMenu(session));
        }
        else if (grid is not null)
        {
            ToolStripMenuItem showGrid = new("Show grid") { CheckOnClick = true, Checked = grid.Read() };
            showGrid.CheckedChanged += (_, _) =>
            {
                grid.Write(showGrid.Checked);
                grid.Invalidate();
            };
            menu.DropDownItems.Add(showGrid);
        }

        if (floorStyle is not null)
        {
            menu.DropDownItems.Add(BuildFloorStyleMenu(floorStyle));
        }
        else if (floor is not null)
        {
            ToolStripMenuItem showFloor = new("Checkerboard floor")
            {
                CheckOnClick = true,
                Checked = floor.Read(),
                ToolTipText = "Show the editor-only checkerboard reference floor",
            };
            showFloor.CheckedChanged += (_, _) =>
            {
                floor.Write(showFloor.Checked);
                floor.Invalidate?.Invoke();
            };
            menu.DropDownItems.Add(showFloor);
        }

        if ((grid is not null || session is not null)
            && (wireframe is not null || fog is not null || isolateSelection is not null || session is not null))
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
        }

        if (wireframe is not null && viewport is null)
        {
            ToolStripMenuItem item = new("Wireframe") { CheckOnClick = true, Checked = wireframe.Read() };
            item.CheckedChanged += (_, _) =>
            {
                wireframe.Write(item.Checked);
                wireframe.Invalidate?.Invoke();
            };
            menu.DropDownItems.Add(item);
        }

        if (fog is not null)
        {
            ToolStripMenuItem item = new("Fog") { CheckOnClick = true, Checked = fog.Read() };
            item.CheckedChanged += (_, _) =>
            {
                fog.Write(item.Checked);
                fog.Invalidate?.Invoke();
            };
            menu.DropDownItems.Add(item);
        }

        if (isolateSelection is not null)
        {
            ToolStripMenuItem item = new("Isolate selected component")
            {
                CheckOnClick = true,
                Checked = isolateSelection.Read(),
                ToolTipText = "When a component is selected in the list, hide everything else in the viewport",
            };
            item.CheckedChanged += (_, _) =>
            {
                isolateSelection.Write(item.Checked);
                isolateSelection.Invalidate?.Invoke();
            };
            menu.DropDownItems.Add(item);
        }

        if (session is not null)
        {
            if (menu.DropDownItems.Count > 0)
            {
                menu.DropDownItems.Add(new ToolStripSeparator());
            }

            var lightingMenu = BuildLightingMenu(session);
            if (viewport is not null) lightingMenu.Text = "Lighting settings";
            menu.DropDownItems.Add(lightingMenu);
            menu.DropDownItems.Add(BuildSkyMenu(session));
        }

        if (extraItems is not null)
        {
            bool addedSeparator = false;
            foreach (ToolStripItem extra in extraItems)
            {
                if (!addedSeparator && menu.DropDownItems.Count > 0)
                {
                    menu.DropDownItems.Add(new ToolStripSeparator());
                    addedSeparator = true;
                }

                menu.DropDownItems.Add(extra);
            }
        }

        if (viewport is not null)
            Editor3DViewMenu.AddTo(menu, viewport, wireframe, session is null ? null : new ToggleBinding
            {
                Read = () => session.Lit,
                Write = value => session.Lit = value,
            });
        return menu;
    }

    private static ToolStripMenuItem BuildFloorStyleMenu(FloorStyleBinding floorStyle)
    {
        ToolStripMenuItem host = new("Floor")
        {
            ToolTipText = "Editor-only ground reference — hidden in gameplay",
        };
        ToolStripMenuItem checker = StyleItem("Checkerboard floor", EditorFloorStyle.Checkerboard);
        ToolStripMenuItem plain = StyleItem("Plain floor", EditorFloorStyle.Plain);
        ToolStripMenuItem gridOnly = StyleItem("Grid only", EditorFloorStyle.GridOnly);
        ToolStripMenuItem none = StyleItem("No floor", EditorFloorStyle.None);
        host.DropDownItems.Add(checker);
        host.DropDownItems.Add(plain);
        host.DropDownItems.Add(gridOnly);
        host.DropDownItems.Add(none);
        host.DropDownOpening += (_, _) =>
        {
            EditorFloorStyle current = floorStyle.Read();
            checker.Checked = current == EditorFloorStyle.Checkerboard;
            plain.Checked = current == EditorFloorStyle.Plain;
            gridOnly.Checked = current == EditorFloorStyle.GridOnly;
            none.Checked = current == EditorFloorStyle.None;
        };
        return host;

        ToolStripMenuItem StyleItem(string caption, EditorFloorStyle style)
        {
            ToolStripMenuItem item = new(caption);
            item.Click += (_, _) =>
            {
                floorStyle.Write(style);
                floorStyle.Invalidate?.Invoke();
            };
            return item;
        }
    }

    private static ToolStripMenuItem BuildGridMenu(Editor3DSession session)
    {
        ToolStripMenuItem host = new("Grid")
        {
            ToolTipText = "World-axis reference grid — not terrain wireframe",
        };
        ToolStripMenuItem show = new("Show grid") { CheckOnClick = true };
        show.Click += (_, _) => session.ShowGrid = show.Checked;
        host.DropDownItems.Add(show);
        host.DropDownItems.Add(new ToolStripSeparator());
        float[] cells = [1f, 2f, 5f, 10f];
        List<ToolStripMenuItem> cellItems = [];
        foreach (float cell in cells)
        {
            float captured = cell;
            ToolStripMenuItem item = new($"{captured:0} u") { Tag = captured };
            item.Click += (_, _) => session.GridCellSize = captured;
            cellItems.Add(item);
            host.DropDownItems.Add(item);
        }

        host.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem snap = new("Snap to grid") { CheckOnClick = true };
        snap.Click += (_, _) => session.SnapToGrid = snap.Checked;
        host.DropDownItems.Add(snap);
        ToolStripMenuItem colour = new("Grid colour…");
        colour.Click += (_, _) =>
        {
            using ColorDialog dialog = new() { Color = session.GridColor, FullOpen = true };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                session.GridColor = dialog.Color;
            }
        };
        host.DropDownItems.Add(colour);
        host.DropDownOpening += (_, _) =>
        {
            show.Checked = session.ShowGrid;
            snap.Checked = session.SnapToGrid;
            foreach (ToolStripMenuItem item in cellItems)
            {
                item.Checked = item.Tag is float cell && Math.Abs(session.GridCellSize - cell) < 0.01f;
            }
        };
        return host;
    }

    private static ToolStripMenuItem BuildLightingMenu(Editor3DSession session)
    {
        ToolStripMenuItem host = new("Lighting")
        {
            ToolTipText = "Preview sun for this editor session — not a saved world light unless the editor stores it",
        };
        ToolStripMenuItem lit = new("Lit") { CheckOnClick = true };
        lit.Click += (_, _) => session.Lit = lit.Checked;
        ToolStripMenuItem sunDisc = new("Show sun disc") { CheckOnClick = true };
        sunDisc.Click += (_, _) => session.ShowSunVisual = sunDisc.Checked;
        ToolStripMenuItem sunColour = new("Sun colour…");
        sunColour.Click += (_, _) =>
        {
            using ColorDialog dialog = new() { Color = ToColor(session.SunColor), FullOpen = true };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                session.SunColor = ToVector(dialog.Color);
            }
        };
        host.DropDownItems.Add(lit);
        host.DropDownItems.Add(sunDisc);
        host.DropDownItems.Add(sunColour);
        host.DropDownItems.Add(IntensityMenu(session));
        host.DropDownItems.Add(SunOrbitMenu(session));
        ToolStripMenuItem reset = new("Reset lighting");
        reset.Click += (_, _) => session.ResetLighting();
        host.DropDownItems.Add(reset);
        host.DropDownOpening += (_, _) =>
        {
            lit.Checked = session.Lit;
            sunDisc.Checked = session.ShowSunVisual;
        };
        return host;
    }

    private static ToolStripMenuItem IntensityMenu(Editor3DSession session)
    {
        ToolStripMenuItem host = new("Sun intensity");
        float[] values = [0.5f, 1f, 1.25f, 2f, 3f];
        List<ToolStripMenuItem> items = [];
        foreach (float value in values)
        {
            float captured = value;
            ToolStripMenuItem item = new($"{captured:0.##}") { Tag = captured };
            item.Click += (_, _) => session.SunIntensity = captured;
            items.Add(item);
            host.DropDownItems.Add(item);
        }

        host.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in items)
            {
                item.Checked = item.Tag is float intensity && Math.Abs(session.SunIntensity - intensity) < 0.01f;
            }
        };
        return host;
    }

    private static ToolStripMenuItem SunOrbitMenu(Editor3DSession session)
    {
        ToolStripMenuItem host = new("Sun orbit speed");
        (float Speed, string Label)[] options =
        [
            (0f, "Off"),
            (9f, "9°/s"),
            (18f, "18°/s"),
            (36f, "36°/s"),
        ];
        List<ToolStripMenuItem> items = [];
        foreach ((float speed, string label) in options)
        {
            float captured = speed;
            ToolStripMenuItem item = new(label);
            item.Click += (_, _) => session.SunOrbitSpeedDegreesPerSecond = captured;
            items.Add(item);
            host.DropDownItems.Add(item);
        }

        host.DropDownOpening += (_, _) =>
        {
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Checked = Math.Abs(session.SunOrbitSpeedDegreesPerSecond - options[i].Speed) < 0.01f;
            }
        };
        return host;
    }

    private static ToolStripMenuItem BuildSkyMenu(Editor3DSession session)
    {
        ToolStripMenuItem host = new("Sky")
        {
            ToolTipText = "Temporary background colour for this viewport",
        };
        ToolStripMenuItem colour = new("Background colour…");
        colour.Click += (_, _) =>
        {
            using ColorDialog dialog = new() { Color = ToColor(session.BackgroundColor), FullOpen = true };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                session.BackgroundColor = ToVector(dialog.Color);
            }
        };
        ToolStripMenuItem reset = new("Reset sky");
        reset.Click += (_, _) => session.ResetSky();
        host.DropDownItems.Add(colour);
        host.DropDownItems.Add(reset);
        return host;
    }

    private static Color ToColor(Vector3 rgb)
    {
        int r = Math.Clamp((int)MathF.Round(rgb.X * 255f), 0, 255);
        int g = Math.Clamp((int)MathF.Round(rgb.Y * 255f), 0, 255);
        int b = Math.Clamp((int)MathF.Round(rgb.Z * 255f), 0, 255);
        return Color.FromArgb(255, r, g, b);
    }

    private static Vector3 ToVector(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f);
}
