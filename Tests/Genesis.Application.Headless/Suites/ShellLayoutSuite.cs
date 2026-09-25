using System.Windows.Forms;
using Genesis.Application.Core.UI;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Settings;
using Genesis.Application.Studio;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Forms;

namespace Genesis.Application.Headless.Suites;

/// <summary>Deterministic geometry regressions for Studio shell surfaces.</summary>
internal static class ShellLayoutSuite
{
    /// <summary>Isolated entry point for spacing iteration; writes no shared test artefacts.</summary>
    public static int RunFocused()
    {
        TestReport report = new()
        {
            StartedUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            RuntimeVersion = Environment.Version.ToString(),
            OutputDirectory = string.Empty,
        };
        HeadlessHarness.BeginMajor(report, "Shell");
        RunCases(report);
        return report.Tests.All(test => test.Passed) ? 0 : 1;
    }

    public static void Run(HeadlessContext ctx)
    {
        RunCases(ctx.Report);
    }

    private static void RunCases(TestReport report)
    {
        HeadlessHarness.RunCase(
            report,
            "Shell.UI.ProjectHub.BrandLockupKeepsDpiScaledWhitespace",
            () =>
            {
                foreach (string density in new[] { "Compact", "Comfortable", "Spacious" })
                {
                    using DpiAwareForm host = new()
                    {
                        ClientSize = new Size(260, 120),
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                    };
                    using NavigationBrandControl brand = new(density);
                    host.Controls.Add(brand);
                    GateSuite.ShowHost(host);
                    System.Windows.Forms.Application.DoEvents();
                    brand.PerformLayout();
                    AssertBrandLayout(brand, density);
                    host.Hide();
                }

                // The gaps are logical UI measurements. This pure scale check catches a future
                // regression that silently returns to physical pixels even on a 200%-DPI screen.
                foreach (string density in new[] { "Compact", "Comfortable", "Spacious" })
                {
                    NavigationBrandMetrics at96 = NavigationBrandMetrics.For(density, 96);
                    NavigationBrandMetrics at192 = NavigationBrandMetrics.For(density, 192);
                    HeadlessHarness.Assert(
                        at192.LogoTextGap == at96.LogoTextGap * 2
                        && at192.TextLineGap == at96.TextLineGap * 2,
                        $"The {density} brand gaps do not scale with DPI.");
                }

                HeadlessHarness.Assert(
                    NavigationBrandMetrics.For("Compact", 96).TextLineGap
                    < NavigationBrandMetrics.For("Comfortable", 96).TextLineGap
                    && NavigationBrandMetrics.For("Comfortable", 96).TextLineGap
                    < NavigationBrandMetrics.For("Spacious", 96).TextLineGap,
                    "Project Hub brand spacing ignores the selected interface density.");
            });

        HeadlessHarness.RunCase(
            report,
            "Shell.UI.ProjectHub.RespondsAtRuntimeDpi",
            () =>
            {
                string temporaryRoot = Path.Combine(
                    Path.GetTempPath(),
                    "Genesis-shell-layout-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryRoot);
                try
                {
                    StudioServices services = new(
                        new SettingsService(Path.Combine(temporaryRoot, "preferences.json")),
                        new ProjectService(),
                        new ProjectValidator(),
                        new StudioLog(Path.Combine(temporaryRoot, "studio.log")));
                    ProjectSession recent = services.Projects.CreateProject(temporaryRoot, "Recent Date Fixture");
                    services.Settings.AddRecentProject(recent.Manifest.Name, recent.ProjectFile);
                    using ProjectHubForm hub = new(services);
                    AssertProjectHubBrandLayout(hub, Genesis.Application.Studio.Theme.ThemeService.Density);
                    AssertResponsiveProjectExperience(hub);
                }
                finally
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }
            });
    }

    /// <summary>Exercises the real Project Hub composition for the consolidated shell gate.</summary>
    internal static void AssertProjectHubBrandLayout(ProjectHubForm hub, string density)
    {
        bool wasVisible = hub.Visible;
        if (!wasVisible)
        {
            GateSuite.ShowHost(hub);
            System.Windows.Forms.Application.DoEvents();
        }

        NavigationBrandControl[] brands = Descendants(hub)
            .OfType<NavigationBrandControl>()
            .ToArray();
        HeadlessHarness.Assert(
            brands.Length == 1,
            $"The Project Hub contains {brands.Length} owned brand lock-ups instead of one.");

        brands[0].PerformLayout();
        AssertBrandLayout(brands[0], density);
        if (!wasVisible)
        {
            hub.Hide();
        }
    }

    /// <summary>Guards the complete project-entry flow at desktop and reduced window sizes.</summary>
    internal static void AssertResponsiveProjectExperience(ProjectHubForm hub)
    {
        string[] expectedIds = ["LuigisMansion", "2DShowcase", "2D", "2DDungeonCrawler", "3DNatureWalk", "3D"];
        HeadlessHarness.Assert(
            ProjectTemplateCatalog.Available.Select(template => template.Id).SequenceEqual(expectedIds),
            "The production gallery must contain four 2D templates and the two distinct 3D templates.");
        HeadlessHarness.Assert(
            ProjectTemplateCatalog.Find("3DSandbox") is null,
            "The duplicate 3D Sandbox template card returned to the public catalog.");

        GateSuite.ShowHost(hub);
        hub.ShowSection(HubSection.Projects);
        hub.ApplyResponsiveLayoutForTest();
        Label recentDate = Descendants(hub.RecentProjects)
            .OfType<Label>()
            .Single(label => label.Name == "RecentProjectDate");
        Size preferredDate = recentDate.GetPreferredSize(new Size(recentDate.ClientSize.Width, 0));
        HeadlessHarness.Assert(
            preferredDate.Height <= recentDate.ClientSize.Height,
            $"The recent-project date needs {preferredDate.Height}px but its label is only {recentDate.ClientSize.Height}px high.");
        Control[] recentCards = hub.RecentProjects.Controls.Cast<Control>()
            .Where(control => control.Tag is RecentProject)
            .ToArray();
        HeadlessHarness.Assert(recentCards.Length >= 1, "Recent Projects should show at least one seeded card.");
        AssertCardsFitViewport(hub.RecentProjects, recentCards, "initial recent projects");
        AssertViewportInsideWindow(hub, hub.RecentProjects, "initial recent projects");
        hub.ClientSize = new Size(
            DpiLayout.Scale(hub, 1440),
            DpiLayout.Scale(hub, 850));
        HeadlessHarness.Assert(
            hub.ClickNavigation(HubSection.Templates) && hub.CurrentSection == HubSection.Templates,
            "The Templates navigation item no longer opens its page through the real click handler.");
        hub.ApplyResponsiveLayoutForTest();

        HeadlessHarness.Assert(
            hub.TemplateCards.Count == expectedIds.Length
            && hub.TemplateCards.Select(card => card.Template.Id).SequenceEqual(expectedIds),
            "The visible gallery does not contain exactly the four production-ready templates.");
        HeadlessHarness.Assert(
            hub.TemplateCards.Select(card => card.Top).Distinct().Count() == 1,
            "All four template cards do not share one row at desktop/fullscreen width.");
        AssertCardsFitViewport(hub.TemplateGallery, hub.TemplateCards, "desktop template gallery");

        hub.ClientSize = new Size(
            DpiLayout.Scale(hub, 860),
            DpiLayout.Scale(hub, 600));
        hub.ApplyResponsiveLayoutForTest();
        AssertCardsFitViewport(hub.TemplateGallery, hub.TemplateCards, "reduced-window template gallery");
        HeadlessHarness.Assert(
            hub.TemplateCards.Select(card => card.Top).Distinct().Count() >= 2,
            "The template gallery did not wrap at the minimum supported window width.");

        hub.ShowSection(HubSection.Projects);
        hub.ApplyResponsiveLayoutForTest();
        recentCards = hub.RecentProjects.Controls.Cast<Control>()
            .Where(control => control.Tag is RecentProject)
            .ToArray();
        AssertCardsFitViewport(hub.RecentProjects, recentCards, "reduced-window recent projects");
        HeadlessHarness.Assert(
            recentCards.All(card => card.Bottom <= hub.RecentProjects.ClientSize.Height
                || hub.RecentProjects.AutoScroll),
            "A recent-project card is vertically clipped without scroll after the reduced-window wrap.");

        int recentCount = recentCards.Length;
        hub.FilterRecentProjects("__no_matching_genesis_project__");
        HeadlessHarness.Assert(!hub.RecentProjects.Controls.Cast<Control>().Any(control => control.Tag is RecentProject),
            "Recent-project search did not filter cards.");
        hub.FilterRecentProjects(string.Empty);
        HeadlessHarness.Assert(hub.RecentProjects.Controls.Cast<Control>().Count(control => control.Tag is RecentProject) == recentCount,
            "Clearing recent-project search lost entries.");

        using NewProjectDialog blank = new();
        HeadlessHarness.Assert(
            blank.SelectedTemplate == "Blank"
            && blank.DestinationPreview.Text.Contains(blank.ProjectName, StringComparison.Ordinal),
            "The redesigned blank-project dialog lost its template or live destination summary.");
        GateSuite.ShowHost(blank);
        foreach (Size size in new[] { new Size(820, 650), new Size(980, 640) })
        {
            blank.ClientSize = size;
            System.Windows.Forms.Application.DoEvents();
            foreach (Control field in Descendants(blank).Where(control => control is TextBox || control is Button))
                AssertViewportInsideWindow(blank, field, "New Project field " + field.Text);
        }
        blank.Hide();
        foreach (ProjectTemplate template in ProjectTemplateCatalog.Available)
        {
            using NewProjectDialog dialog = new(template.Id);
            HeadlessHarness.Assert(
                dialog.SelectedTemplate == template.Id
                && dialog.DestinationPreview.Text.Contains(dialog.ProjectName, StringComparison.Ordinal),
                $"The {template.Name} creation dialog is not bound to the selected template.");
        }

        hub.Hide();
    }

    private static void AssertCardsFitViewport(
        FlowLayoutPanel viewport,
        IReadOnlyCollection<Control> cards,
        string surface)
    {
        int rightEdge = viewport.ClientSize.Width - viewport.Padding.Right;
        HeadlessHarness.Assert(viewport.AutoScroll, $"The {surface} cannot scroll when its content wraps.");
        foreach (Control card in cards)
        {
            HeadlessHarness.Assert(
                card.Left >= viewport.Padding.Left && card.Right <= rightEdge,
                $"A card is horizontally clipped in the {surface}: card={card.Bounds}, client={viewport.ClientRectangle}.");
        }
    }

    private static void AssertViewportInsideWindow(Form window, Control viewport, string surface)
    {
        Rectangle windowBounds = window.RectangleToScreen(window.ClientRectangle);
        Rectangle viewportBounds = viewport.RectangleToScreen(viewport.ClientRectangle);
        HeadlessHarness.Assert(
            windowBounds.Contains(viewportBounds),
            $"The {surface} extends outside the window: viewport={viewportBounds}, window={windowBounds}.");
    }

    private static void AssertBrandLayout(NavigationBrandControl brand, string density)
    {
        NavigationBrandMetrics expected = NavigationBrandMetrics.For(density, brand.DeviceDpi);
        Label title = brand.Title;
        Label edition = brand.Edition;

        HeadlessHarness.Assert(
            title.Left == edition.Left,
            $"GENESIS begins at {title.Left}px but APPLICATION begins at {edition.Left}px.");
        HeadlessHarness.Assert(
            edition.Top - title.Bottom == expected.TextLineGap,
            $"The {density} title/subtitle gap is {edition.Top - title.Bottom}px; "
            + $"expected {expected.TextLineGap}px at {brand.DeviceDpi} DPI.");
        HeadlessHarness.Assert(
            title.Left - brand.Logo.Right == expected.LogoTextGap,
            $"The {density} emblem/text gap is {title.Left - brand.Logo.Right}px; "
            + $"expected {expected.LogoTextGap}px at {brand.DeviceDpi} DPI.");
        HeadlessHarness.Assert(
            title.Top >= 0 && edition.Bottom <= brand.ClientSize.Height,
            "The Project Hub brand text is clipped vertically by its parent.");
        HeadlessHarness.Assert(
            title.Right <= brand.ClientSize.Width && edition.Right <= brand.ClientSize.Width,
            "The Project Hub brand text is clipped horizontally by its parent.");

        int doubledTextCentre = title.Top + edition.Bottom;
        int doubledLogoCentre = brand.Logo.Top + brand.Logo.Bottom;
        HeadlessHarness.Assert(
            Math.Abs(doubledTextCentre - doubledLogoCentre) <= 2,
            "The two-line brand text is not vertically centred beside the emblem.");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
