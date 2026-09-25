using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// Package Manager: toggles the project's engine packs (local, manifest-owned — no accounts
/// or cloud sources per the spec). Core packs stay locked on.
/// </summary>
public sealed class PackageManagerDialog : DpiAwareForm
{
    private static readonly (string Id, string Title, string Description, bool Locked)[] KnownPacks =
    [
        ("core", "Core", "Runtime core: ECS, timing, scenes, input.", true),
        ("rendering", "Rendering", "DX11 forward renderer, sprites, meshes, lighting.", true),
        ("physics", "Physics", "Bepu-backed 2D/3D physics and collision.", false),
        ("audio", "Audio", "XAudio2 playback, buses, spatial sources.", false),
        ("terrain", "Terrain", "Heightmap terrain streaming and collision.", false),
        ("voxel", "Voxels", "Chunked voxel worlds and meshing.", false),
        ("network", "Networking", "LiteNetLib sessions, replication, RPC tags.", false),
    ];

    private readonly ProjectSession _session;
    private readonly ProjectService _projects;
    private readonly List<(string Id, CheckBox Box)> _checks = [];

    public PackageManagerDialog(ProjectSession session, ProjectService projects)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));

        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 396);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Package Manager — " + session.Manifest.Name;

        Label heading = new()
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.TextMuted,
            Height = 40,
            Padding = new Padding(16, 12, 16, 0),
            Text = "Enabled packs are written to the project manifest and gate Engine.* capabilities.",
        };

        Panel list = new()
        {
            AutoScroll = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 6, 16, 6),
        };
        int y = 8;
        foreach ((string id, string title, string description, bool locked) in KnownPacks)
        {
            CheckBox box = new()
            {
                AutoSize = false,
                Checked = locked || _session.Manifest.EnabledPacks.Contains(id, StringComparer.OrdinalIgnoreCase),
                Enabled = !locked,
                Font = ThemeService.InterfaceFont,
                Location = new Point(16, y),
                Size = new Size(180, 24),
                Text = title + (locked ? "  (required)" : string.Empty),
            };
            Label info = new()
            {
                AutoSize = false,
                Font = ThemeService.InterfaceFont,
                ForeColor = ThemeService.Palette.TextMuted,
                Location = new Point(200, y + 3),
                Size = new Size(240, 34),
                Text = description,
            };
            list.Controls.Add(box);
            list.Controls.Add(info);
            _checks.Add((id, box));
            y += 44;
        }

        Panel footer = new() { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 10, 16, 12) };
        Button cancel = new() { DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Text = "Cancel", Width = 92 };
        Button apply = new() { Dock = DockStyle.Right, Text = "Apply", Width = 92 };
        apply.Click += (_, _) => ApplyPacks();
        footer.Controls.Add(cancel);
        footer.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
        footer.Controls.Add(apply);

        Controls.Add(list);
        Controls.Add(footer);
        Controls.Add(heading);
        CancelButton = cancel;
        ThemeService.Apply(this);
    }

    private void ApplyPacks()
    {
        List<string> packs = [];
        foreach ((string id, CheckBox box) in _checks)
        {
            if (box.Checked)
            {
                packs.Add(id);
            }
        }

        _session.Manifest.EnabledPacks = packs;
        _projects.Save(_session);
        DialogResult = DialogResult.OK;
        Close();
    }
}
