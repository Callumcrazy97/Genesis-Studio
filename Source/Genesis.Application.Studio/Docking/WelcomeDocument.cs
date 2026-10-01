using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.UI;
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
    private readonly StarterGallery _steps;
    private readonly StarterGallery _create;
    private readonly Label _recentsEmpty;
    private readonly Panel _scroll;
    private readonly Dictionary<Control, Action<float>> _scaledLayouts = [];

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
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(32, 28, 32, 24),
            RowCount = 10,
            Tag = "canvas",
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (int i = 0; i < 10; i++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _scroll = new Panel { AutoScroll = true, Dock = DockStyle.Fill, Tag = "canvas" };
        _scroll.Controls.Add(root);
        Controls.Add(_scroll);

        root.Controls.Add(BuildHero(), 0, 0);
        root.Controls.Add(BuildActions(), 0, 1);

        // "Your game in 4 steps": the whole path from nothing to a playable game, with the steps
        // already done ticked off from what is on disk.
        root.Controls.Add(SectionHeading("Your game in 4 steps"), 0, 2);
        _steps = HomeGallery("HomeGameSteps");
        root.Controls.Add(_steps, 0, 3);

        root.Controls.Add(SectionHeading("Continue where you left off"), 0, 4);

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
        root.Controls.Add(_recents, 0, 5);

        root.Controls.Add(SectionHeading("Create something new"), 0, 6);
        _create = HomeGallery("HomeCreateGallery");
        _create.SetItems(ResourceDefinitions.Creatable
            .OrderBy(definition => CategoryOrder(ResourceKindVisuals.Category(definition.Kind)))
            .Select(definition => new StarterItem(
                "Create_" + definition.Kind,
                definition.Kind == ResourceKind.Image ? "Image / sprite" : definition.DisplayName,
                ResourceKindVisuals.Purpose(definition.Kind))
            {
                Category = ResourceKindVisuals.Category(definition.Kind),
                Glyph = ResourceKindVisuals.Glyph(definition.Kind),
                Swatch = ResourceKindVisuals.Swatch(definition.Kind),
            }));
        root.Controls.Add(_create, 0, 7);

        root.Controls.Add(SectionHeading("Project at a glance"), 0, 8);
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
        root.Controls.Add(_stats, 0, 9);

        Refresh(scan: true);
        ThemeService.Apply(this);
    }

    public event EventHandler<string>? ActionRequested;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Dock activation can scroll to the first button while the document still has its
        // provisional size. Finish initial layout before setting the new page's starting position.
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed) return;
            ApplyInterfaceLayout();
            _scroll.AutoScrollPosition = Point.Empty;
        }));
    }

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
        _steps.SetItems(GameSteps(snapshot));

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
        ApplyInterfaceLayout();
    }

    internal void ApplyInterfaceLayout()
    {
        TableLayoutPanel root = _scroll.Controls.OfType<TableLayoutPanel>().Single();
        Point scroll = _scroll.AutoScrollPosition;
        _scroll.SuspendLayout();
        root.SuspendLayout();
        float scale = ThemeService.InterfaceScale * DeviceDpi / 96f;
        foreach (Control retired in _scaledLayouts.Keys.Where(control => control.IsDisposed).ToArray()) _scaledLayouts.Remove(retired);
        foreach (var layout in _scaledLayouts)
        {
            layout.Key.SuspendLayout(); layout.Value(scale); layout.Key.ResumeLayout(true);
        }
        root.ResumeLayout(true);
        _scroll.ResumeLayout(true);
        _scroll.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }

    private static void Place(Control control, float scale, int x, int y, int width, int height) =>
        control.SetBounds((int)Math.Round(x * scale), (int)Math.Round(y * scale), (int)Math.Round(width * scale), (int)Math.Round(height * scale));

    private static void SetHeadingFont(Label label, float size, FontStyle style = FontStyle.Regular)
    {
        float scaled = size * ThemeService.InterfaceScale;
        if (Math.Abs(label.Font.Size - scaled) > .01f || label.Font.Style != style
            || label.Font.FontFamily.Name != ThemeService.InterfaceFont.FontFamily.Name)
            label.Font = new Font(ThemeService.InterfaceFont.FontFamily, scaled, style);
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
        void ResizeText()
        {
            path.Width = Math.Max(DpiLayout.Scale(hero, 100),
                hero.ClientSize.Width - path.Left - DpiLayout.Scale(hero, 24));
            title.Width = path.Width;
        }
        hero.Resize += (_, _) => ResizeText();
        _scaledLayouts.Add(hero, scale =>
        {
            hero.MinimumSize = new Size((int)(320 * scale), (int)(146 * scale)); hero.Height = (int)(146 * scale);
            Place(logo, scale, 22, 31, 66, 66); Place(eyebrow, scale, 110, 20, 760, 20);
            SetHeadingFont(eyebrow, 8, FontStyle.Bold);
            Place(title, scale, 108, 44, 760, 46); SetHeadingFont(title, 26, FontStyle.Bold);
            Place(path, scale, 111, 96, 760, 22); path.Font = ThemeService.InterfaceFont;
            ResizeText();
        });
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
        _scaledLayouts.Add(actions, scale =>
        {
            run.Size = new Size((int)(150 * scale), (int)(44 * scale));
            room.Size = new Size((int)(168 * scale), (int)(44 * scale));
            validate.Size = new Size((int)(150 * scale), (int)(44 * scale));
        });

        return actions;
    }

    private StarterGallery HomeGallery(string name)
    {
        StarterGallery gallery = new(name)
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            ContentPadding = 0,
            FitsContent = true,
            Margin = new Padding(0, 0, 0, 6),
            Tag = "canvas",
        };
        gallery.ItemChosen += (_, item) =>
        {
            // Home is a launcher, not a picker: nothing stays highlighted after a click.
            gallery.SelectedId = null;
            ActionRequested?.Invoke(this, ActionFor(item.Id));
        };
        return gallery;
    }

    private static string ActionFor(string id) => id switch
    {
        "StepImage" => "Create:" + ResourceKind.Image,
        "StepObject" => "Create:" + ResourceKind.GameObject,
        "StepRoom" => "OpenOrCreateRoom",
        "StepRun" => "Run",
        _ when id.StartsWith("Create_", StringComparison.Ordinal) => "Create:" + id["Create_".Length..],
        _ => id,
    };

    private static int CategoryOrder(string category) => category switch
    {
        "Art and sound" => 0,
        "Worlds" => 1,
        _ => 2,
    };

    /// <summary>The four steps from an empty project to a playable game, ticked from disk.</summary>
    private static IEnumerable<StarterItem> GameSteps(ProjectSnapshot snapshot)
    {
        (string Id, string Title, string Description, ResourceKind Kind, string Glyph, bool Done)[] steps =
        [
            ("StepImage", "1  Draw a sprite", "Paint your player, an enemy or a tile in the Image editor.",
                ResourceKind.Image, UiGlyphs.Brush, snapshot.CountOf(ResourceKind.Image) > 0),
            ("StepObject", "2  Make an Object", "Give the sprite behaviour: movement, collisions and events.",
                ResourceKind.GameObject, UiGlyphs.Puzzle, snapshot.CountOf(ResourceKind.GameObject) > 0),
            ("StepRoom", "3  Build a Room", "Place your Objects in a level, in 2D or 3D.",
                ResourceKind.Room, UiGlyphs.Floor, snapshot.CountOf(ResourceKind.Room) > 0),
            ("StepRun", "4  Press Run", "Play your game. F5 runs it from anywhere in Studio.",
                ResourceKind.Unknown, UiGlyphs.Play, false),
        ];

        bool nextMarked = false;
        foreach (var step in steps)
        {
            string badge = string.Empty;
            StarterBadgeTone tone = StarterBadgeTone.Accent;
            if (step.Done)
            {
                badge = "✓ Done";
                tone = StarterBadgeTone.Success;
            }
            else if (!nextMarked)
            {
                badge = "Next";
                nextMarked = true;
            }

            yield return new StarterItem(step.Id, step.Title, step.Description)
            {
                Badge = badge,
                BadgeTone = tone,
                Glyph = step.Glyph,
                Swatch = step.Kind == ResourceKind.Unknown ? UiTokens.Success : ResourceKindVisuals.Swatch(step.Kind),
            };
        }
    }

    private Label SectionHeading(string text)
    {
        Label heading = new()
        {
            AutoSize = true,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 13f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Margin = new Padding(0, 10, 0, 6),
            Text = text,
            UseMnemonic = false,
        };
        // Headings follow the interface text size like the cards beneath them.
        _scaledLayouts.Add(heading, _ => SetHeadingFont(heading, 13, FontStyle.Bold));
        return heading;
    }

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
        _scaledLayouts.Add(card, scale =>
        {
            card.Size = new Size((int)(210 * scale), (int)(100 * scale));
            Place(glyph, scale, 14, 12, 34, 34); SetHeadingFont(glyph, 18);
            Place(name, scale, 52, 16, 144, 20); name.Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold);
            Place(meta, scale, 52, 38, 144, 18); meta.Font = ThemeService.InterfaceFont;
            Place(pathLabel, scale, 14, 70, 182, 18); pathLabel.Font = ThemeService.InterfaceFont;
        });

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

    private Control BuildStat(string caption, string value)
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
        _scaledLayouts.Add(tile, scale =>
        {
            tile.Size = new Size((int)(132 * scale), (int)(62 * scale));
            Place(captionLabel, scale, 14, 10, 108, 18); captionLabel.Font = ThemeService.InterfaceFont;
            Place(valueLabel, scale, 13, 28, 108, 26); SetHeadingFont(valueLabel, 15, FontStyle.Bold);
        });

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
