using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>Which page of the hub is showing.</summary>
public enum HubSection
{
    Projects,
    Templates,
}

/// <summary>A navigation item whose real click path can be exercised by the headless gate.</summary>
internal sealed class NavigationItem : Label
{
    public void RaiseClick() => OnClick(EventArgs.Empty);
}

/// <summary>The responsive front door for creating, opening and resuming Genesis projects.</summary>
public sealed class ProjectHubForm : DpiAwareForm
{
    private const int NavigationWidth = 238;
    private readonly StudioServices _services;
    private readonly List<NavigationItem> _navigationItems = [];
    private readonly List<ProjectTemplateCard> _templateCards = [];
    private readonly Panel _projectsPage;
    private readonly Panel _templatesPage;
    private readonly TableLayoutPanel _projectsLayout;
    private readonly TableLayoutPanel _templatesLayout;
    private readonly FlowLayoutPanel _primaryActions;
    private readonly FlowLayoutPanel _recentList;
    private readonly FlowLayoutPanel _templateGallery;
    private readonly TextBox _recentSearch = new() { PlaceholderText = "Search recent projects by name or location…", Dock = DockStyle.Top, AccessibleName = "Search recent projects" };
    private HubSection _section = HubSection.Projects;

    public ProjectHubForm(StudioServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        ClientSize = new Size(1280, 780);
        DoubleBuffered = true;
        ForeColor = ThemeService.Palette.Text;
        Icon = Branding.WindowIcon ?? Icon;
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Text = StudioBuildInfo.WindowLabel + " — Project Hub";

        TableLayoutPanel shell = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 1,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavigationWidth));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        Controls.Add(shell);

        shell.Controls.Add(BuildNavigation(), 0, 0);

        Panel pageHost = new()
        {
            BackColor = ThemeService.Palette.Canvas,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        shell.Controls.Add(pageHost, 1, 0);

        (_projectsPage, _projectsLayout, _primaryActions, _recentList) = BuildProjectsPage();
        (_templatesPage, _templatesLayout, _templateGallery) = BuildTemplatesPage();
        pageHost.Controls.Add(_templatesPage);
        pageHost.Controls.Add(_projectsPage);

        pageHost.SizeChanged += (_, _) => LayoutResponsiveSurfaces();
        Shown += (_, _) => LayoutResponsiveSurfaces();

        ThemeService.Apply(this);
        ReloadRecentProjects();
        ShowSection(HubSection.Projects);
    }

    public event EventHandler<ProjectSessionEventArgs>? ProjectOpened;

    /// <summary>Which hub page is showing.</summary>
    public HubSection CurrentSection => _section;

    internal IReadOnlyList<ProjectTemplateCard> TemplateCards => _templateCards;

    internal FlowLayoutPanel TemplateGallery => _templateGallery;

    internal FlowLayoutPanel PrimaryActions => _primaryActions;

    internal FlowLayoutPanel RecentProjects => _recentList;

    /// <summary>Clicks the real navigation control so tests cover its event wiring.</summary>
    public bool ClickNavigation(HubSection section)
    {
        NavigationItem? item = _navigationItems.FirstOrDefault(
            candidate => candidate.Tag is HubSection tagged && tagged == section);
        if (item is null) return false;
        item.RaiseClick();
        return true;
    }

    /// <summary>Switch between recent projects and the production-ready template gallery.</summary>
    public void ShowSection(HubSection section)
    {
        _section = section;
        _projectsPage.Visible = section == HubSection.Projects;
        _templatesPage.Visible = section == HubSection.Templates;
        (section == HubSection.Projects ? _projectsPage : _templatesPage).BringToFront();

        foreach (NavigationItem item in _navigationItems)
        {
            bool selected = item.Tag is HubSection tagged && tagged == section;
            item.BackColor = selected ? Color.FromArgb(40, ThemeService.Palette.Accent) : Color.Transparent;
            item.ForeColor = selected ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted;
            item.Font = selected
                ? new Font(ThemeService.InterfaceFont, FontStyle.Bold)
                : ThemeService.InterfaceFont;
        }

        LayoutResponsiveSurfaces();
    }

    internal void ApplyResponsiveLayoutForTest()
    {
        PerformLayout();
        LayoutResponsiveSurfaces();
        System.Windows.Forms.Application.DoEvents();
    }

    private (Panel Page, TableLayoutPanel Layout, FlowLayoutPanel Actions, FlowLayoutPanel Recents) BuildProjectsPage()
    {
        Panel page = CreatePage();
        TableLayoutPanel layout = PageLayout(page, 56, 40);

        Panel headerPanel = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        Label titleLabel = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 20f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(0, 8),
            Text = "Recent Projects",
        };

        ModernButton newBtn = new()
        {
            Text = "New Project",
            Accent = true,
            Size = new Size(118, 34),
            Margin = new Padding(0, 0, 8, 0),
        };
        ModernButton openBtn = new()
        {
            Text = "Open Project",
            Size = new Size(118, 34),
        };

        newBtn.Click += (_, _) => CreateProject();
        openBtn.Click += (_, _) => OpenProject();

        FlowLayoutPanel topActions = new()
        {
            AutoSize = true,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0),
            WrapContents = false,
        };
        topActions.Controls.Add(newBtn);
        topActions.Controls.Add(openBtn);

        headerPanel.Controls.Add(titleLabel);
        headerPanel.Controls.Add(topActions);

        layout.Controls.Add(headerPanel, 0, 0);

        // Retained for shell-layout API compatibility; New/Open live in the page header and nav.
        FlowLayoutPanel actions = new()
        {
            AutoScroll = true,
            Visible = false,
        };

        FlowLayoutPanel recents = new()
        {
            AutoScroll = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = Padding.Empty,
            Padding = new Padding(0, 2, 10, 0),
            Tag = "transparent",
            WrapContents = true,
        };
        _recentSearch.TextChanged += (_, _) => ReloadRecentProjects();
        layout.Controls.Add(_recentSearch, 0, 1);
        layout.Controls.Add(recents, 0, 2);
        return (page, layout, actions, recents);
    }

    private (Panel Page, TableLayoutPanel Layout, FlowLayoutPanel Gallery) BuildTemplatesPage()
    {
        Panel page = CreatePage();
        TableLayoutPanel layout = PageLayout(page, 124);
        layout.Controls.Add(BuildPageHeader(
            "PROJECT TEMPLATES",
            "New Project and Templates",
            "Choose a 2D or 3D starting point. Each project includes editable assets and gameplay; Crypts of Genesis is a preview template."), 0, 0);

        FlowLayoutPanel gallery = new()
        {
            AutoScroll = true,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 8, 0),
            Tag = "transparent",
            WrapContents = true,
        };
        foreach (ProjectTemplate template in ProjectTemplateCatalog.Available)
        {
            ProjectTemplateCard card = new(template);
            card.CreateRequested += (_, _) => CreateProject(template.Id);
            _templateCards.Add(card);
            gallery.Controls.Add(card);
        }

        layout.Controls.Add(gallery, 0, 1);
        return (page, layout, gallery);
    }

    private static Panel CreatePage() => new()
    {
        BackColor = ThemeService.Palette.Canvas,
        Dock = DockStyle.Fill,
        Padding = new Padding(38, 34, 38, 28),
    };

    private static TableLayoutPanel PageLayout(Panel parent, params int[] fixedRows)
    {
        TableLayoutPanel layout = new()
        {
            BackColor = Color.Transparent,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = fixedRows.Length + 1,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        foreach (int row in fixedRows) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, row));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        parent.Controls.Add(layout);
        return layout;
    }

    private static Control BuildPageHeader(string eyebrow, string title, string description)
    {
        Panel header = new() { BackColor = Color.Transparent, Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            AutoSize = false,
            Font = new Font("Segoe UI Variable Text", 8f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(2, 0),
            Size = new Size(700, 20),
            Text = eyebrow,
        });
        Label titleLabel = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoEllipsis = false,
            AutoSize = false,
            Font = new Font("Segoe UI Variable Display", 23f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(0, 23),
            Size = new Size(900, 42),
            Text = title,
        };
        header.Controls.Add(titleLabel);
        Label descriptionLabel = new()
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoEllipsis = true,
            AutoSize = false,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(2, 73),
            Size = new Size(900, 36),
            Text = description,
        };
        header.Controls.Add(descriptionLabel);
        header.Resize += (_, _) =>
        {
            int inset = DpiLayout.Scale(header, 4);
            titleLabel.Width = Math.Max(DpiLayout.Scale(header, 100), header.ClientSize.Width - inset);
            descriptionLabel.Width = Math.Max(DpiLayout.Scale(header, 100), header.ClientSize.Width - inset);
        };
        return header;
    }

    private static RoundedSurfacePanel BuildActionCard(
        string glyph,
        string title,
        string description,
        Action action)
    {
        RoundedSurfacePanel card = new()
        {
            CornerRadius = 12,
            Cursor = Cursors.Hand,
            Height = 130,
            Interactive = true,
            Margin = new Padding(0, 0, 14, 12),
            Raised = true,
        };
        Label icon = new()
        {
            AutoSize = false,
            BackColor = ThemeService.Palette.SurfaceHover,
            Font = new Font("Segoe UI Symbol", 15f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Accent,
            Location = new Point(16, 17),
            Size = new Size(38, 38),
            Text = glyph,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Label titleLabel = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(66, 17),
            Size = new Size(220, 24),
            Text = title,
        };
        Label descriptionLabel = new()
        {
            AutoEllipsis = false,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(66, 45),
            Size = new Size(220, 62),
            Text = description,
        };
        card.Controls.Add(icon);
        card.Controls.Add(titleLabel);
        card.Controls.Add(descriptionLabel);
        card.Resize += (_, _) =>
        {
            int textInset = DpiLayout.Scale(card, 82);
            int minimumTextWidth = DpiLayout.Scale(card, 80);
            titleLabel.Width = Math.Max(minimumTextWidth, card.ClientSize.Width - textInset);
            descriptionLabel.Width = Math.Max(minimumTextWidth, card.ClientSize.Width - textInset);
        };
        void Invoke(object? _, EventArgs __) => action();
        card.Click += Invoke;
        foreach (Control child in card.Controls)
        {
            child.Cursor = Cursors.Hand;
            child.Click += Invoke;
        }
        return card;
    }

    private Control BuildNavigation()
    {
        Panel navigation = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        navigation.Controls.Add(new NavigationBrandControl
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(20, 22),
            Width = NavigationWidth - 40,
        });
        navigation.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, 7.2f, FontStyle.Bold),
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(29, 100),
            Text = "START",
        });
        navigation.Controls.Add(NavigationLabel("⌂", "Recent Projects", 124, HubSection.Projects));
        navigation.Controls.Add(NavigationAction("＋", "New Project", 172, () => CreateProject()));
        navigation.Controls.Add(NavigationLabel("▦", "Templates", 220, HubSection.Templates));

        Panel buildIdentityHost = new()
        {
            Dock = DockStyle.Bottom, Height = 128, Padding = new Padding(18, 14, 18, 14),
            BackColor = Color.Transparent,
        };
        RoundedSurfacePanel runtime = new() { Dock = DockStyle.Fill, CornerRadius = 10 };

        runtime.Controls.Add(new Label
        {
            AutoSize = true,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(14, 13),
            Text = "Genesis Studio",
        });
        runtime.Controls.Add(new Label
        {
            AutoSize = false,
            Name = "StudioBuildIdentity",
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(14, 38),
            Size = new Size(NavigationWidth - 64, 58),
            Text = $"Hotfix {StudioBuildInfo.Revision} · {StudioBuildInfo.Configuration}\nBuild {StudioBuildInfo.BuildId}",
        });
        buildIdentityHost.Controls.Add(runtime);
        navigation.Controls.Add(buildIdentityHost);
        return navigation;
    }

    private NavigationItem NavigationLabel(string glyph, string text, int y, HubSection section)
    {
        NavigationItem item = new()
        {
            AutoSize = false,
            Cursor = Cursors.Hand,
            Font = ThemeService.InterfaceFont,
            Location = new Point(18, y),
            Padding = new Padding(13, 0, 0, 0),
            Size = new Size(NavigationWidth - 36, 40),
            Tag = section,
            Text = $"{glyph}    {text}",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        item.Click += (_, _) => ShowSection(section);
        item.MouseEnter += (_, _) =>
        {
            if (_section != section) item.BackColor = ThemeService.Palette.SurfaceHover;
        };
        item.MouseLeave += (_, _) =>
        {
            if (_section != section) item.BackColor = Color.Transparent;
        };
        _navigationItems.Add(item);
        return item;
    }

    private NavigationItem NavigationAction(string glyph, string text, int y, Action onClick)
    {
        NavigationItem item = new()
        {
            AutoSize = false,
            Cursor = Cursors.Hand,
            Font = ThemeService.InterfaceFont,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(18, y),
            Padding = new Padding(13, 0, 0, 0),
            Size = new Size(NavigationWidth - 36, 40),
            Tag = "action",
            Text = $"{glyph}    {text}",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        item.Click += (_, _) => onClick();
        item.MouseEnter += (_, _) => item.BackColor = ThemeService.Palette.SurfaceHover;
        item.MouseLeave += (_, _) => item.BackColor = Color.Transparent;
        return item;
    }

    private void LayoutResponsiveSurfaces()
    {
        _projectsLayout.RowStyles[0].Height = DpiLayout.Scale(this, 56);
        _projectsLayout.RowStyles[1].Height = DpiLayout.Scale(this, 40);
        _templatesLayout.RowStyles[0].Height = DpiLayout.Scale(this, 124);

        int visibleContentWidth = Math.Max(
            DpiLayout.Scale(this, 240),
            ClientSize.Width
                - DpiLayout.Scale(this, NavigationWidth)
                - DpiLayout.Scale(this, 38 + 38)
                - DpiLayout.Scale(this, 18));
        LayoutFlowCards(_templateGallery, 254, 4, 410, DeviceDpi, visibleContentWidth);
        LayoutRecentCards(visibleContentWidth);
        _templateGallery.PerformLayout();
        _recentList.PerformLayout();
    }

    private void LayoutRecentCards(int visibleContentWidth)
    {
        Control[] cards = _recentList.Controls.Cast<Control>()
            .Where(control => control.Tag is RecentProject)
            .ToArray();
        if (cards.Length == 0)
        {
            int emptyWidth = Math.Max(
                DpiLayout.Scale(this, 240),
                Math.Min(_recentList.ClientSize.Width, visibleContentWidth)
                    - _recentList.Padding.Horizontal
                    - DpiLayout.Scale(this, 20));
            foreach (Control empty in _recentList.Controls)
            {
                empty.Width = emptyWidth;
            }

            return;
        }

        int minimumWidth = DpiLayout.Scale(280, DeviceDpi);
        int height = DpiLayout.Scale(210, DeviceDpi);
        int viewport = Math.Max(
            minimumWidth,
            Math.Min(_recentList.ClientSize.Width, visibleContentWidth)
                - _recentList.Padding.Horizontal
                - DpiLayout.Scale(18, DeviceDpi));
        int gap = DpiLayout.Scale(16, DeviceDpi);
        int columns = Math.Clamp((viewport + gap) / (minimumWidth + gap), 1, 3);
        int width = Math.Max(minimumWidth, (viewport - gap * (columns - 1)) / columns);
        foreach (Control card in cards)
        {
            card.Margin = new Padding(0, 0, gap, gap);
            card.Size = new Size(width, height);
        }
    }

    private static int LayoutFlowCards(
        FlowLayoutPanel flow,
        int logicalMinimumWidth,
        int maximumColumns,
        int logicalHeight,
        int dpi,
        int visibleWidth)
    {
        int minimumWidth = DpiLayout.Scale(logicalMinimumWidth, dpi);
        int height = DpiLayout.Scale(logicalHeight, dpi);
        int viewport = Math.Max(
            minimumWidth,
            Math.Min(flow.ClientSize.Width, visibleWidth)
                - flow.Padding.Horizontal
                - DpiLayout.Scale(18, dpi));
        int gap = DpiLayout.Scale(14, dpi);
        int columns = Math.Clamp((viewport + gap) / (minimumWidth + gap), 1, maximumColumns);
        int width = Math.Max(minimumWidth, (viewport - gap * (columns - 1)) / columns);
        foreach (Control card in flow.Controls)
        {
            card.Margin = new Padding(0, 0, gap, gap);
            card.Size = new Size(width, height);
        }
        return columns;
    }

    private void ReloadRecentProjects()
    {
        _recentList.SuspendLayout();
        foreach (Control oldCard in _recentList.Controls.Cast<Control>().ToArray()) oldCard.Dispose();
        string query = _recentSearch.Text.Trim();
        RecentProject[] recents = _services.Settings.Current.RecentProjects
            .Where(recent => query.Length == 0 || recent.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || recent.ProjectFile.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.LastOpenedUtc)
            .ToArray();
        if (recents.Length == 0)
        {
            _recentList.Controls.Add(query.Length == 0 ? BuildEmptyRecentState() : new Label
            {
                AutoSize = true, Text = "No matching projects. Try another name or location.",
                ForeColor = ThemeService.Palette.TextMuted, Padding = new Padding(8),
            });
        }
        else
        {
            foreach (RecentProject recent in recents) _recentList.Controls.Add(CreateRecentCard(recent));
        }
        _recentList.ResumeLayout();
        LayoutResponsiveSurfaces();
    }

    private static Control BuildEmptyRecentState()
    {
        RoundedSurfacePanel empty = new()
        {
            CornerRadius = 12,
            Height = 100,
            Margin = new Padding(0, 4, 0, 0),
        };
        empty.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = ThemeService.Palette.Text,
            Location = new Point(20, 20),
            Size = new Size(600, 24),
            Text = "Your recent work will appear here",
        });
        empty.Controls.Add(new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(20, 50),
            Size = new Size(600, 24),
            Text = "Create a project, choose a template, or open an existing .genesisproj file.",
        });
        return empty;
    }

    private Control CreateRecentCard(RecentProject recent)
    {
        bool exists = File.Exists(recent.ProjectFile);

        string projectIconPath = string.Empty;
        int projectIconFps = 15;
        if (exists)
        {
            try
            {
                string json = File.ReadAllText(recent.ProjectFile);
                var manifest = JsonSerializer.Deserialize<ProjectManifest>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest != null)
                {
                    if (!string.IsNullOrWhiteSpace(manifest.ProjectIcon))
                    {
                        projectIconPath = ResourceNames.Resolve(Path.GetDirectoryName(recent.ProjectFile)!, manifest.ProjectIcon, ResourceType.Image);
                    }

                    projectIconFps = manifest.ProjectIconFps;
                }
            }
            catch
            {
                // Icon is cosmetic; a missing/invalid icon must not block opening the project.
            }
        }

        RoundedSurfacePanel card = new()
        {
            CornerRadius = 10,
            Cursor = exists ? Cursors.Hand : Cursors.Default,
            Width = 280,
            Height = 210,
            Interactive = exists,
            Margin = new Padding(0, 0, 16, 16),
            Raised = true,
            Tag = recent,
        };

        AnimatedIconPlayer thumb = new()
        {
            Dock = DockStyle.Top,
            Height = 112,
        };

        if (exists)
        {
            if (!string.IsNullOrWhiteSpace(projectIconPath))
            {
                thumb.LoadIcon(projectIconPath, projectIconFps);
            }
            else
            {
                Label fallback = new()
                {
                    AutoSize = false,
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI Symbol", 24f),
                    ForeColor = ThemeService.Palette.TextMuted,
                    Text = "◈",
                    TextAlign = ContentAlignment.MiddleCenter,
                };
                thumb.Controls.Add(fallback);
            }
        }
        else
        {
            Label missing = new()
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Symbol", 24f),
                ForeColor = ThemeService.Palette.Error,
                Text = "⊘",
                TextAlign = ContentAlignment.MiddleCenter,
            };
            thumb.Controls.Add(missing);
        }

        card.Controls.Add(thumb);

        Label name = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont, FontStyle.Bold),
            ForeColor = exists ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted,
            Location = new Point(12, 120),
            Size = new Size(170, 20),
            Text = recent.Name,
        };
        card.Controls.Add(name);

        Label status = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = exists ? ThemeService.Palette.Success : ThemeService.Palette.Error,
            Location = new Point(180, 122),
            Size = new Size(88, 18),
            Text = exists ? "● Active" : "● Missing",
            TextAlign = ContentAlignment.TopRight,
        };
        card.Controls.Add(status);

        Label date = new()
        {
            Name = "RecentProjectDate",
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(12, 146),
            Size = new Size(256, 18),
            Text = "Last opened " + recent.LastOpenedUtc.ToLocalTime().ToString("d MMM yyyy"),
        };
        card.Controls.Add(date);

        Label path = new()
        {
            Name = "RecentProjectPath",
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = ThemeService.Palette.TextMuted,
            Location = new Point(12, 168),
            Size = new Size(256, 28),
            Text = recent.ProjectFile,
        };
        card.Controls.Add(path);

        ContextMenuStrip menu = new();
        menu.Items.Add("Locate project…", null, (_, _) => LocateRecentProject(recent));
        menu.Items.Add("Remove from recent projects", null, (_, _) => RemoveRecentProject(recent));
        card.ContextMenuStrip = menu;
        card.Disposed += (_, _) => menu.Dispose();
        if (!exists)
        {
            ModernButton locate = new() { Text = "Locate…", Location = new Point(12, 172), Size = new Size(90, 27) };
            locate.Click += (_, _) => LocateRecentProject(recent);
            card.Controls.Add(locate);
            locate.BringToFront();
            path.Visible = false;
        }

        if (exists)
        {
            void Invoke(object? _, EventArgs __) => TryOpenProject(recent.ProjectFile);
            card.Click += Invoke;
            foreach (Control child in card.Controls)
            {
                child.Cursor = Cursors.Hand;
                child.Click += Invoke;
                foreach (Control sub in child.Controls)
                {
                    sub.Cursor = Cursors.Hand;
                    sub.Click += Invoke;
                }
            }
        }

        return card;
    }

    internal void FilterRecentProjects(string query) => _recentSearch.Text = query;

    internal void RelocateRecentProject(RecentProject recent, string path)
    {
        ProjectSession session = _services.Projects.OpenProject(path);
        _services.Settings.Update(settings =>
        {
            settings.RecentProjects.RemoveAll(item => string.Equals(item.ProjectFile, session.ProjectFile, StringComparison.OrdinalIgnoreCase) && item != recent);
            recent.ProjectFile = session.ProjectFile;
            recent.Name = session.Manifest.Name;
        });
        ReloadRecentProjects();
    }

    private void RemoveRecentProject(RecentProject recent)
    {
        _services.Settings.Update(settings => settings.RecentProjects.Remove(recent));
        ReloadRecentProjects();
    }

    private void LocateRecentProject(RecentProject recent)
    {
        using OpenFileDialog dialog = new() { Title = "Locate " + recent.Name, Filter = "Genesis projects|*" + ProjectService.ProjectExtension, CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { RelocateRecentProject(recent, dialog.FileName); }
        catch (Exception exception) when (exception is IOException or ArgumentException or JsonException)
        {
            MessageBox.Show(this, exception.Message, "Could not open project", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CreateProject(string? templateId = null)
    {
        using NewProjectDialog dialog = new(templateId);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ProjectSession session = _services.Projects.CreateProject(
                dialog.ParentDirectory,
                dialog.ProjectName,
                dialog.SelectedTemplate,
                dialog.OverwriteExisting);
            _services.Settings.AddRecentProject(session.Manifest.Name, session.ProjectFile);
            _services.Log.Information("ProjectHub", $"Created project '{session.Manifest.Name}'.");
            ProjectOpened?.Invoke(this, new ProjectSessionEventArgs(session));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _services.Log.Error("ProjectHub", "Project creation failed.", exception);
            MessageBox.Show(this, exception.Message, "Could not create project", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenProject()
    {
        using OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            Filter = "Genesis Project (*.genesisproj)|*.genesisproj|All files (*.*)|*.*",
            Multiselect = false,
            RestoreDirectory = true,
            Title = "Open Genesis Project",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) TryOpenProject(dialog.FileName);
    }

    private void TryOpenProject(string path)
    {
        try
        {
            ProjectSession session = _services.Projects.OpenProject(path);
            _services.Settings.AddRecentProject(session.Manifest.Name, session.ProjectFile);
            _services.Log.Information("ProjectHub", $"Opened project '{session.Manifest.Name}'.");
            ProjectOpened?.Invoke(this, new ProjectSessionEventArgs(session));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _services.Log.Error("ProjectHub", "Project open failed.", exception);
            MessageBox.Show(this, exception.Message, "Could not open project", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

public sealed class ProjectSessionEventArgs(ProjectSession session) : EventArgs
{
    public ProjectSession Session { get; } = session;
}
