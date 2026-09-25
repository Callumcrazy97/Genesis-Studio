using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Docking;

/// <summary>
/// The Start page shown once a project is open.
/// </summary>
/// <remarks>
/// Deliberately shows the *project*, not the product: the previous version was three static
/// marketing cards ("2D Pixel", "3D Worlds", "Voxel") that had no click handler and repeated the
/// Project Hub's job inside a project you had already created. One of them advertised a Voxel
/// editor that no longer exists as a resource kind at all. Everything on this page is now derived
/// from the project on disk, so it says something true the moment it opens.
///
/// Layout is a docked/anchored <see cref="TableLayoutPanel"/> rather than hardcoded
/// <c>Location = new Point(x, y)</c>, so it survives resize and DPI the way the Project Hub brand
/// (NEXT-118) and the narrow Inspector (NEXT-119) were already fixed to.
/// </remarks>
public sealed class WelcomeDocument : GenesisDockContent
{
    private const int MaxScannedFiles = 20_000;
    private const int RecentCount = 6;

    private readonly ProjectSession _project;
    private readonly FlowLayoutPanel _recents;
    private readonly FlowLayoutPanel _stats;
    private readonly Label _recentsEmpty;

    public WelcomeDocument(ProjectSession project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _project = project;
        Text = "Start";
        TabText = "Start";
        DockAreas = DockAreas.Document | DockAreas.Float;
        ShowHint = DockState.Document;
        HideOnClose = false;

        TableLayoutPanel root = new()
        {
            AutoScroll = true,
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(32, 28, 32, 24),
            RowCount = 6,
            Tag = "canvas",
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 6; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        Controls.Add(root);

        root.Controls.Add(BuildHero(), 0, 0);
        root.Controls.Add(BuildActions(), 0, 1);
        root.Controls.Add(SectionHeading("Recently edited resources"), 0, 2);

        _recentsEmpty = new Label
        {
            AutoSize = true,
            ForeColor = ThemeService.Palette.TextMuted,
            Margin = new Padding(2, 2, 0, 6),
            Text = "Create your first resource from the Assets panel to get started.",
        };

        _recents = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 16),
            Tag = "transparent",
            WrapContents = true,
        };
        root.Controls.Add(_recents, 0, 3);

        root.Controls.Add(SectionHeading("Project at a glance"), 0, 4);
        _stats = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 2, 0, 8),
            Tag = "transparent",
            WrapContents = true,
        };
        root.Controls.Add(_stats, 0, 5);

        Refresh(scan: true);
        ThemeService.Apply(this);
    }

    public event EventHandler<string>? ActionRequested;

    /// <summary>Rebuilds the recents strip and counters from the project on disk.</summary>
    /// <remarks>
    /// Public so the shell can refresh the page after resources change without recreating the
    /// document. The scan is bounded (<see cref="MaxScannedFiles"/>) for the same reason the
    /// Finder's is: a pathological project must not make opening the Start page feel broken.
    /// </remarks>
    public void Refresh(bool scan)
    {
        if (!scan)
        {
            return;
        }

        ProjectSnapshot snapshot = ScanProject(_project.RootPath);

        _recents.SuspendLayout();
        foreach (Control previous in _recents.Controls.Cast<Control>().ToArray())
        {
            _recents.Controls.Remove(previous);
            if (previous != _recentsEmpty) previous.Dispose();
        }
        if (snapshot.Recent.Count == 0)
        {
            _recents.Controls.Add(_recentsEmpty);
        }
        else
        {
            foreach (RecentResource recent in snapshot.Recent)
            {
                _recents.Controls.Add(BuildRecentCard(recent));
            }
        }

        _recents.ResumeLayout(true);

        _stats.SuspendLayout();
        foreach (Control previous in _stats.Controls.Cast<Control>().ToArray()) previous.Dispose();
        _stats.Controls.Add(BuildStat("Resources", snapshot.Total.ToString()));
        _stats.Controls.Add(BuildStat("Rooms", snapshot.CountOf(ResourceKind.Room).ToString()));
        _stats.Controls.Add(BuildStat("Objects", snapshot.CountOf(ResourceKind.GameObject).ToString()));
        _stats.Controls.Add(BuildStat("Images", snapshot.CountOf(ResourceKind.Image).ToString()));
        _stats.Controls.Add(BuildStat("Scripts", snapshot.CountOf(ResourceKind.PgslScript).ToString()));
        _stats.ResumeLayout(true);
    }

    private Control BuildHero()
    {
        RoundedSurfacePanel hero = new()
        {
            CornerRadius = 14,
            Dock = DockStyle.Fill,
            Height = 146,
            Margin = new Padding(0, 0, 0, 14),
            MinimumSize = new Size(320, 146),
            Raised = true,
        };

        GenesisLogoControl logo = new()
        {
            Location = new Point(22, 31),
            Size = new Size(66, 66),
        };
        hero.Controls.Add(logo);

        Label eyebrow = new()
        {
            AutoSize = true,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 8f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(110, 20),
            Text = "CURRENT PROJECT",
            UseMnemonic = false,
        };
        hero.Controls.Add(eyebrow);

        Label title = new()
        {
            AutoSize = false,
            AutoEllipsis = true,
            Size = new Size(760, 46),
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 26f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(108, 44),
            Text = _project.Manifest.Name,
            UseMnemonic = false,
        };
        hero.Controls.Add(title);

        Label path = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(111, 96),
            Size = new Size(760, 22),
            Text = _project.RootPath,
            UseMnemonic = false,
        };
        hero.Controls.Add(path);
        hero.Resize += (_, _) =>
        {
            path.Width = Math.Max(DpiLayout.Scale(hero, 100),
                hero.ClientSize.Width - path.Left - DpiLayout.Scale(hero, 24));
            title.Width = path.Width;
        };
        return hero;
    }

    private Control BuildActions()
    {
        FlowLayoutPanel actions = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(0, 2, 0, 2),
            Tag = "transparent",
            WrapContents = true,
        };

        ModernButton run = new()
        {
            Accent = true,
            Glyph = "▶",
            Margin = new Padding(0, 0, 10, 0),
            Size = new Size(150, 44),
            Text = "Run",
        };
        run.Click += (_, _) => ActionRequested?.Invoke(this, "Run");
        actions.Controls.Add(run);

        ModernButton room = new()
        {
            Glyph = "▱",
            Margin = new Padding(0, 0, 10, 0),
            Size = new Size(168, 44),
            Text = "Open start room",
        };
        room.Click += (_, _) => ActionRequested?.Invoke(this, "OpenStartRoom");
        actions.Controls.Add(room);

        ModernButton validate = new()
        {
            Glyph = "✓",
            Margin = new Padding(0, 0, 0, 0),
            Size = new Size(150, 44),
            Text = "Validate",
        };
        validate.Click += (_, _) => ActionRequested?.Invoke(this, "Validate");
        actions.Controls.Add(validate);

        return actions;
    }

    private static Label SectionHeading(string text) => new()
    {
        AutoSize = true,
        Font = new Font(ThemeService.InterfaceFont.FontFamily, 13f, FontStyle.Bold),
        ForeColor = ThemeService.Palette.Text,
        Margin = new Padding(0, 10, 0, 6),
        Text = text,
        UseMnemonic = false,
    };

    private Control BuildRecentCard(RecentResource recent)
    {
        RoundedSurfacePanel card = new()
        {
            CornerRadius = 11,
            Cursor = Cursors.Hand,
            Interactive = true,
            Margin = new Padding(0, 0, 12, 12),
            Raised = true,
            Size = new Size(210, 100),
        };

        Label glyph = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 18f),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(14, 12),
            Size = new Size(34, 34),
            Text = recent.Glyph,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false,
        };
        card.Controls.Add(glyph);

        Label name = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(52, 16),
            Size = new Size(144, 20),
            Text = recent.Name,
            UseMnemonic = false,
        };
        card.Controls.Add(name);

        Label meta = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(52, 38),
            Size = new Size(144, 18),
            Text = $"{recent.KindName} · {recent.Age}",
            UseMnemonic = false,
        };
        card.Controls.Add(meta);

        Label pathLabel = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(14, 70),
            Size = new Size(182, 18),
            Text = "Open resource",
            UseMnemonic = false,
        };
        card.Controls.Add(pathLabel);

        void Open(object? sender, EventArgs args) =>
            ActionRequested?.Invoke(this, "Open:" + recent.Name);

        card.Click += Open;
        foreach (Control child in card.Controls)
        {
            child.Cursor = Cursors.Hand;
            child.Click += Open;
        }

        return card;
    }

    private static Control BuildStat(string caption, string value)
    {
        RoundedSurfacePanel tile = new()
        {
            CornerRadius = 10,
            Margin = new Padding(0, 0, 12, 0),
            Raised = true,
            Size = new Size(132, 62),
        };

        Label captionLabel = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(14, 10),
            Size = new Size(108, 18),
            Text = caption,
            UseMnemonic = false,
        };
        tile.Controls.Add(captionLabel);

        Label valueLabel = new()
        {
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 15f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(13, 28),
            Size = new Size(108, 26),
            Text = value,
            UseMnemonic = false,
        };
        tile.Controls.Add(valueLabel);

        return tile;
    }

    private static ProjectSnapshot ScanProject(string projectRoot)
    {
        ProjectSnapshot snapshot = new();
        string assets = Path.Combine(projectRoot, "Assets");
        if (!Directory.Exists(assets))
        {
            return snapshot;
        }

        List<(RecentResource Resource, DateTime Stamp)> recents = [];
        int scanned = 0;

        try
        {
            foreach (var resource in ResourceNames.For(projectRoot).Entries)
            {
                string file = resource.FullPath;
                if (++scanned > MaxScannedFiles)
                {
                    break;
                }

                ResourceDefinition? definition = ResourceDefinitions.FromPath(file);
                if (definition is null)
                {
                    continue;
                }

                snapshot.Add(definition.Kind);

                DateTime stamp;
                try
                {
                    stamp = File.GetLastWriteTimeUtc(file);
                }
                catch (IOException)
                {
                    // A file that cannot be stat'ed still counts toward the totals; it just cannot
                    // be ranked by recency. Skipping the whole scan for one locked file would be
                    // worse than showing a slightly shorter list.
                    continue;
                }

                string relative = Path.GetRelativePath(assets, file).Replace('\\', '/');
                string name = resource.Name;

                recents.Add((
                    new RecentResource(
                        name,
                        definition.DisplayName,
                        definition.IconGlyph,
                        relative,
                        DescribeAge(stamp)),
                    stamp));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Report what was reachable rather than failing the whole page.
        }
        catch (DirectoryNotFoundException)
        {
        }

        snapshot.Recent = recents
            .OrderByDescending(entry => entry.Stamp)
            .Take(RecentCount)
            .Select(entry => entry.Resource)
            .ToList();

        return snapshot;
    }

    private static string DescribeAge(DateTime utc)
    {
        TimeSpan age = DateTime.UtcNow - utc;
        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return age.TotalDays < 30
            ? $"{(int)age.TotalDays}d ago"
            : utc.ToLocalTime().ToString("d MMM yyyy");
    }

    private sealed record RecentResource(
        string Name,
        string KindName,
        string Glyph,
        string RelativePath,
        string Age);

    private sealed class ProjectSnapshot
    {
        private readonly Dictionary<ResourceKind, int> _counts = [];

        public int Total { get; private set; }

        public List<RecentResource> Recent { get; set; } = [];

        public void Add(ResourceKind kind)
        {
            Total++;
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        }

        public int CountOf(ResourceKind kind) => _counts.GetValueOrDefault(kind);
    }
}
