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
/// <remarks>Painted as a rounded pill: hover fades it in, and the selected item grows an accent bar.</remarks>
internal sealed class NavigationItem : Label
{
    private float _hover;
    private float _select;
    private bool _selected;

    public NavigationItem()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.SupportsTransparentBackColor, true);
    }

    public void RaiseClick() => OnClick(EventArgs.Empty);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            float from = _select, to = value ? 1f : 0f;
            UiMotion.Animate(this, "select", 240, t => { _select = UiMotion.Lerp(from, to, t); Invalidate(); });
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        float from = _hover;
        UiMotion.Animate(this, "hover", 140, t => { _hover = UiMotion.Lerp(from, 1f, t); Invalidate(); });
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        float from = _hover;
        UiMotion.Animate(this, "hover", 220, t => { _hover = UiMotion.Lerp(from, 0f, t); Invalidate(); });
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        ThemePalette palette = ThemeService.Palette;
        Graphics g = e.Graphics;
        Color behind = Parent?.BackColor is { A: 255 } solid ? solid : palette.Surface;
        g.Clear(behind);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Rectangle pill = ClientRectangle;
        pill.Inflate(-1, -2);
        Color fill = UiMotion.Mix(UiMotion.Mix(behind, palette.SurfaceHover, _hover * 0.85f), UiDrawing.Blend(palette.Accent, behind, 0.78f), _select);
        using (System.Drawing.Drawing2D.GraphicsPath path = UiDrawing.RoundedRectangle(pill, 9))
        using (SolidBrush brush = new(fill))
            g.FillPath(brush, path);
        if (_select > 0.01f)
        {
            int barHeight = Math.Max(2, (int)(pill.Height * 0.55f * _select));
            Rectangle bar = new(pill.Left + 5, pill.Top + (pill.Height - barHeight) / 2, 3, barHeight);
            using System.Drawing.Drawing2D.GraphicsPath barPath = UiDrawing.RoundedRectangle(bar, 1);
            using SolidBrush accent = new(palette.Accent);
            g.FillPath(accent, barPath);
        }
        Rectangle text = new(pill.Left + Padding.Left + 4, pill.Top, Math.Max(1, pill.Width - Padding.Left - 8), pill.Height);
        TextRenderer.DrawText(g, Text, Font, text, UiMotion.Mix(palette.TextMuted, palette.Text, Math.Max(_select, _hover)),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }
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
    private readonly TableLayoutPanel _shellLayout;
    private readonly Control _navigation;
    private bool _arranging;
    private HubSection _section = HubSection.Projects;

    public ProjectHubForm(StudioServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Canvas;
        Tag = ThemeService.MeasuredLayoutTag;
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
        _shellLayout = shell;
        _navigation = BuildNavigation();
        shell.Controls.Add(_navigation, 0, 0);

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
        if (UiMotion.Enabled)
        {
            // The Hub fades in rather than popping onto the screen.
            Opacity = 0;
            Shown += (_, _) => UiMotion.Animate(this, "fade", 220, t => Opacity = t);
        }
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
            item.Selected = selected;
            item.BackColor = selected ? Color.FromArgb(40, ThemeService.Palette.Accent) : Color.Transparent;
            item.ForeColor = selected ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted;
            item.Font = selected
                ? new Font(ThemeService.InterfaceFont, FontStyle.Bold)
                : ThemeService.InterfaceFont;
        }

        LayoutResponsiveSurfaces();
        RevealSection(section);
    }

    /// <summary>Fades the page's cards in one after another, so changing page reads as a transition.</summary>
    private void RevealSection(HubSection section)
    {
        IEnumerable<RoundedSurfacePanel> cards = section == HubSection.Projects
            ? _recentList.Controls.OfType<RoundedSurfacePanel>()
            : _templateCards;
        int index = 0;
        foreach (RoundedSurfacePanel card in cards) card.Reveal(Math.Min(index++, 10) * 45);
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
            "Choose a 2D or 3D starting point. Each project includes editable assets and gameplay you can learn from."), 0, 0);

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
        ProjectTemplateGallery.Fill(gallery, template => CreateProject(template.Id), _templateCards);

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
            Text = $"{StudioBuildInfo.VersionLabel}\nHotfix {StudioBuildInfo.Revision} · Build {StudioBuildInfo.BuildId}",
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
        return item;
    }

    private void LayoutResponsiveSurfaces()
    {
        if (_arranging || _projectsLayout is null) return;
        _arranging = true;
        try
        {
        NavigationBrandControl brand = _navigation.Controls.OfType<NavigationBrandControl>().Single();
        int navigationWidth = Math.Max(DpiLayout.Scale(this, NavigationWidth),
            Math.Max(brand.Title.PreferredSize.Width, brand.Edition.PreferredSize.Width) + brand.Logo.Width + 64);
        _shellLayout.ColumnStyles[0].Width = navigationWidth;
        brand.Width = navigationWidth - 40;
        brand.Height = Math.Max(brand.Logo.Height, brand.Title.Height + brand.Edition.Height + 12);
        Label start = _navigation.Controls.OfType<Label>().Single(label => label.Text == "START");
        start.Top = brand.Bottom + 20;
        int navY = start.Bottom + 12;
        foreach (NavigationItem item in _navigationItems)
        {
            item.Bounds = new Rectangle(18, navY, navigationWidth - 36, Math.Max(40, Font.Height + 18));
            navY = item.Bottom + 8;
        }
        Panel identityHost = _navigation.Controls.OfType<Panel>().Single(panel => panel.Dock == DockStyle.Bottom);
        Control identity = identityHost.Controls[0];
        ShellDialogLayout.StackLabels(identity, 14, 12, 6);
        identityHost.Height = identity.Controls.Cast<Control>().Max(control => control.Bottom) + identityHost.Padding.Vertical + 12;
        Panel projectHeader = (Panel)_projectsLayout.GetControlFromPosition(0, 0)!;
        Label projectTitle = projectHeader.Controls.OfType<Label>().Single();
        projectTitle.Font = ThemeService.HeadingFont;
        FlowLayoutPanel topActions = projectHeader.Controls.OfType<FlowLayoutPanel>().Single();
        topActions.Dock = DockStyle.None;
        foreach (Button button in topActions.Controls.OfType<Button>()) ShellDialogLayout.FitButton(button);
        topActions.Size = new Size(topActions.Controls.Cast<Control>().Sum(control => control.Width + control.Margin.Horizontal), Font.Height + 22);
        bool stackedHeader = projectTitle.Width + topActions.Width + 24 > projectHeader.ClientSize.Width;
        topActions.Location = new Point(stackedHeader ? 0 : Math.Max(0, projectHeader.ClientSize.Width - topActions.Width),
            stackedHeader ? projectTitle.Bottom + 8 : 4);
        _projectsLayout.RowStyles[0].Height = Math.Max(projectTitle.Bottom, topActions.Bottom) + 12;
        _projectsLayout.RowStyles[1].Height = _recentSearch.Font.Height + 16;
        Control templatesHeader = _templatesLayout.GetControlFromPosition(0, 0)!;
        ShellDialogLayout.StackLabels(templatesHeader, 2, 0, 8);
        _templatesLayout.RowStyles[0].Height = templatesHeader.Controls.Cast<Control>().Max(control => control.Bottom) + 16;

        int visibleContentWidth = Math.Max(
            DpiLayout.Scale(this, 240),
            ClientSize.Width
                - navigationWidth
                - DpiLayout.Scale(this, 38 + 38)
                - DpiLayout.Scale(this, 18));
        LayoutFlowCards(_templateGallery, (int)Math.Ceiling(254 * Math.Max(1, ThemeService.InterfaceScale * .8f)), 4, 410, DeviceDpi, visibleContentWidth);
        LayoutRecentCards(visibleContentWidth);
        _templateGallery.PerformLayout();
        _recentList.PerformLayout();
        }
        finally { _arranging = false; }
    }

    public override void ApplyInterfaceLayout() => LayoutResponsiveSurfaces();

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
                if (empty is Panel)
                {
                    ShellDialogLayout.StackLabels(empty, 20, 16, 10);
                    empty.Height = empty.Controls.Cast<Control>().Max(control => control.Bottom) + 16;
                }
            }

            return;
        }

        int minimumWidth = DpiLayout.Scale(250, DeviceDpi);
        int viewport = Math.Max(
            minimumWidth,
            Math.Min(_recentList.ClientSize.Width, visibleContentWidth)
                - _recentList.Padding.Horizontal
                - DpiLayout.Scale(18, DeviceDpi));
        foreach (ContinueProjectCard hero in _recentList.Controls.OfType<ContinueProjectCard>()) hero.ApplyLayout(viewport);
        int gap = DpiLayout.Scale(16, DeviceDpi);
        int columns = Math.Clamp((viewport + gap) / (minimumWidth + gap), 1, 4);
        int width = Math.Max(minimumWidth, (viewport - gap * (columns - 1)) / columns);
        int picture = width * 9 / 16;
        foreach (Control card in cards)
        {
            card.Margin = new Padding(0, 0, gap, gap);
            card.Width = width;
            if (card is RecentProjectTile tile) tile.SetThumbnailHeight(picture);
            int y = picture + DpiLayout.Scale(12, DeviceDpi);
            foreach (Label label in card.Controls.OfType<Label>().OrderBy(label => label.Top).ToArray())
            {
                label.Bounds = new Rectangle(DpiLayout.Scale(14, DeviceDpi), y, Math.Max(1, width - DpiLayout.Scale(28, DeviceDpi)), label.Font.Height + 4);
                y = label.Bottom + DpiLayout.Scale(2, DeviceDpi);
            }
            foreach (Button button in card.Controls.OfType<Button>())
            {
                ShellDialogLayout.FitButton(button);
                button.Location = new Point(DpiLayout.Scale(14, DeviceDpi), y + DpiLayout.Scale(4, DeviceDpi));
                y = button.Bottom;
            }
            card.Height = y + DpiLayout.Scale(16, DeviceDpi);
        }
    }

    // Shared by every recent card: a new options object per card rebuilt the manifest's serializer
    // contract each time, a visible pause per card when the Hub opened.
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static int LayoutFlowCards(
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
            if (card is Label) continue;
            card.Margin = new Padding(0, 0, gap, gap);
            card.Size = new Size(width, height);
            if (card is ProjectTemplateCard template) template.ApplyInterfaceLayout();
        }
        ProjectTemplateGallery.LayoutHeadings(flow, viewport, dpi);
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
            // The project worked on last leads the page as a large Continue card (not while searching).
            RecentProject? latest = query.Length == 0
                ? recents.Where(recent => File.Exists(recent.ProjectFile)).OrderByDescending(recent => recent.LastOpenedUtc).FirstOrDefault()
                : null;
            if (latest is not null)
                _recentList.Controls.Add(new ContinueProjectCard(latest, () => TryOpenProject(latest.ProjectFile), StartActions()));
            foreach (RecentProject recent in recents) _recentList.Controls.Add(CreateRecentCard(recent));
        }
        _recentList.ResumeLayout();
        LayoutResponsiveSurfaces();
        if (_section == HubSection.Projects) RevealSection(HubSection.Projects);
    }

    /// <summary>The Continue card's shortcuts: a blank project, each template, and the template page.</summary>
    private IEnumerable<LauncherAction> StartActions()
    {
        yield return new LauncherAction("＋", "Blank project", "An empty workspace", () => CreateProject());
        foreach (ProjectTemplate template in ProjectTemplateCatalog.Available)
        {
            string id = template.Id;
            yield return new LauncherAction(template.Glyph, template.Name, template.Tagline, () => CreateProject(id));
        }
        yield return new LauncherAction("▦", "All templates", "Browse 2D and 3D starters", () => ShowSection(HubSection.Templates));
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
                var manifest = JsonSerializer.Deserialize<ProjectManifest>(json, ManifestJsonOptions);
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

        RecentProjectTile card = new(recent, exists);

        // An animated project icon, when the project has one, takes the picture's place.
        if (exists && !string.IsNullOrWhiteSpace(projectIconPath))
        {
            AnimatedIconPlayer thumb = new() { Dock = DockStyle.Top, Height = card.ThumbnailHeight };
            thumb.LoadIcon(projectIconPath, projectIconFps);
            card.Controls.Add(thumb);
        }

        Label name = new()
        {
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            Font = new Font(ThemeService.InterfaceFont.FontFamily, ThemeService.InterfaceFont.Size + 1.5f, FontStyle.Bold),
            ForeColor = exists ? ThemeService.Palette.Text : ThemeService.Palette.TextMuted,
            Location = new Point(12, 1),
            Size = new Size(256, 24),
            Text = recent.Name,
            UseMnemonic = false,
        };
        card.Controls.Add(name);

        Label date = new()
        {
            Name = "RecentProjectDate",
            AutoEllipsis = true,
            AutoSize = false,
            BackColor = Color.Transparent,
            ForeColor = exists ? ThemeService.Palette.TextMuted : ThemeService.Palette.Error,
            Location = new Point(12, 2),
            Size = new Size(256, 18),
            Text = exists ? "Opened " + ProjectPreview.Ago(recent.LastOpenedUtc) : "Missing: moved or deleted",
        };
        card.Controls.Add(date);

        ToolTip pathTip = new();
        pathTip.SetToolTip(card, recent.ProjectFile);
        pathTip.SetToolTip(name, recent.ProjectFile);
        card.Disposed += (_, _) => pathTip.Dispose();

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
            // Shown until Studio's window is up: ProjectOpened builds it.
            using IDisposable notice = ProjectOpeningNotice.Begin(Path.GetFileNameWithoutExtension(path));
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
