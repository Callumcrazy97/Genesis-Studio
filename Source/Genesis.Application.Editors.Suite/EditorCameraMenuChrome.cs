using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite;

/// <summary>Shared Camera menu for 3D editors: create, look, orbit, and remove the second-camera inset.</summary>
internal static class EditorCameraMenuChrome
{
    public static ToolStripDropDownButton BuildCameraMenu(
        EditorViewport3D viewport,
        IEnumerable<ToolStripItem>? extraItems = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);

        ToolStripDropDownButton menu = new("Camera")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Primary and second-camera options for this viewport",
        };

        SecondCameraCommands commands = CreateCommands(viewport);
        foreach (ToolStripItem item in commands.Items)
        {
            menu.DropDownItems.Add(item);
        }

        if (extraItems is not null)
        {
            menu.DropDownItems.Add(new ToolStripSeparator());
            foreach (ToolStripItem extra in extraItems)
            {
                menu.DropDownItems.Add(extra);
            }
        }

        menu.DropDownOpening += (_, _) => commands.Sync();
        return menu;
    }

    /// <summary>Nested View → Second camera menu with the same commands as Camera.</summary>
    public static ToolStripMenuItem BuildSecondCameraViewMenu(EditorViewport3D viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ToolStripMenuItem host = new("Second camera")
        {
            ToolTipText = "Create, orbit, look, and remove the inset camera",
        };
        SecondCameraCommands commands = CreateCommands(viewport, includePrimaryOrbitTarget: false);
        foreach (ToolStripItem item in commands.Items)
        {
            host.DropDownItems.Add(item);
        }

        host.DropDownOpening += (_, _) => commands.Sync();
        return host;
    }

    private sealed class SecondCameraCommands
    {
        public required ToolStripItem[] Items { get; init; }
        public required Action Sync { get; init; }
    }

    private static SecondCameraCommands CreateCommands(
        EditorViewport3D viewport,
        bool includePrimaryOrbitTarget = true)
    {
        ToolStripMenuItem create = new("Create second camera")
        {
            ToolTipText = "Open a live top-right inset from this view. Drag inside it to look.",
        };
        create.Click += (_, _) => viewport.PinSecondaryFromCurrentView();

        ToolStripMenuItem move = new("Move second camera to my position");
        move.Click += (_, _) => viewport.MoveSecondaryToCurrentView();

        ToolStripMenuItem look = new("Look at selected");
        look.Click += (_, _) => viewport.LookSecondaryAtSelection();

        ToolStripMenuItem orbitTarget = new("Set orbit target from selection")
        {
            ToolTipText = "Orbit the main editor camera around the current selection",
        };
        orbitTarget.Click += (_, _) => viewport.SetPrimaryOrbitTargetFromSelection();

        ToolStripMenuItem follow = new("Follow selected");
        follow.Click += (_, _) =>
        {
            if (!viewport.HasSecondaryCamera)
            {
                viewport.PinSecondaryFromCurrentView();
            }

            viewport.SetSecondaryMotion(EditorCameraMotionMode.FollowSelection);
        };

        ToolStripMenuItem orbit = new("Orbit selected")
        {
            ToolTipText = "Orbit the second camera around the selection",
        };
        orbit.Click += (_, _) =>
        {
            if (!viewport.HasSecondaryCamera)
            {
                viewport.PinSecondaryFromCurrentView();
            }

            viewport.LookSecondaryAtSelection();
            viewport.SetSecondaryMotion(EditorCameraMotionMode.OrbitPoint);
        };

        ToolStripMenuItem speed = BuildOrbitSpeedMenu(viewport);
        ToolStripMenuItem remove = new("Remove second camera");
        remove.Click += (_, _) => viewport.ClearSecondaryCamera();

        ToolStripItem[] items = includePrimaryOrbitTarget
            ?
            [
                create, move, new ToolStripSeparator(), look, orbitTarget, follow, orbit, speed,
                new ToolStripSeparator(), remove,
            ]
            :
            [
                create, move, new ToolStripSeparator(), look, follow, orbit, speed,
                new ToolStripSeparator(), remove,
            ];

        return new SecondCameraCommands
        {
            Items = items,
            Sync = () =>
            {
                bool hasSecondary = viewport.HasSecondaryCamera;
                bool hasSelection = viewport.SelectionWorldPoint?.Invoke() is not null;
                move.Enabled = hasSecondary;
                look.Enabled = hasSelection;
                orbitTarget.Enabled = hasSelection;
                follow.Enabled = hasSelection;
                orbit.Enabled = hasSelection;
                remove.Enabled = hasSecondary;
            },
        };
    }

    private static ToolStripMenuItem BuildOrbitSpeedMenu(EditorViewport3D viewport)
    {
        ToolStripMenuItem host = new("Orbit speed")
        {
            ToolTipText = "How fast the second camera orbits when Orbit is on",
        };
        (float Speed, string Label)[] options =
        [
            (9f, "9°/s"),
            (18f, "18°/s"),
            (36f, "36°/s"),
            (72f, "72°/s"),
        ];
        List<ToolStripMenuItem> items = [];
        foreach ((float speed, string label) in options)
        {
            float captured = speed;
            ToolStripMenuItem item = new(label);
            item.Click += (_, _) =>
            {
                if (!viewport.HasSecondaryCamera)
                {
                    viewport.PinSecondaryFromCurrentView();
                }

                if (viewport.SecondaryCamera is { } slot)
                {
                    slot.OrbitSpeedDegreesPerSecond = captured;
                }
            };
            items.Add(item);
            host.DropDownItems.Add(item);
        }

        host.DropDownOpening += (_, _) =>
        {
            float current = viewport.SecondaryCamera?.OrbitSpeedDegreesPerSecond ?? 18f;
            for (int i = 0; i < items.Count; i++)
            {
                items[i].Checked = Math.Abs(current - options[i].Speed) < 0.01f;
            }
        };
        return host;
    }
}
