using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Genesis.Application.Core;
using Genesis.Application.Core.Settings;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Editors.Suite.Rooms;
using Genesis.Application.Editors.Suite.Terrain;
using Genesis.Application.Editors.Suite.UiKit;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Projects;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Runtime;
using Genesis.Application.Studio;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Forms;
using Genesis.Application.Studio.Theme;
using Genesis.Runtime;
using Genesis.Runtime.Debugger;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scripting;
using Genesis.Shared.Interfaces;
using WeifenLuo.WinFormsUI.Docking;
using EcsWorld = Genesis.Runtime.ECS.World;
using RuntimeImageMetrics = Genesis.Application.Runtime.ImageMetrics;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// Image themes: the pictures that ship, the palettes derived from them, and the backdrop.
/// </summary>
/// <remarks>
/// The load-bearing case here is <c>DerivedPaletteStaysLegible</c>. Everything else about an image
/// theme is taste, and taste does not belong in a gate — but a palette derived from an arbitrary
/// picture can genuinely produce grey text on a grey panel, and that is a defect no amount of
/// liking the wallpaper makes acceptable.
/// </remarks>
internal static class ThemeImageSuite
{
    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Theme");

        RunDiscoveryCase(ctx.Report);
        RunLegibilityCase(ctx.Report);
        RunDistinctAccentCase(ctx.Report);
        RunBackdropCase(ctx.Report);
        RunDockingMdiBackdropCase(ctx.Report);
        RunBrandingCase(ctx.Report);
        RunPreferencesSeparationContractCase(ctx);
        RunThumbnailCacheCase(ctx.Report);
        RunGenesisDarkTokenStripCase(ctx);
        RunSharedUiKitShowcaseCase(ctx);
        RunProjectHubParityCase(ctx);
        RunStudioShellDockChromeCase(ctx);
        RunPreferencesChromeCase(ctx);
        RunNotesEditorChromeCase(ctx);
        RunImageViewerChromeCase(ctx);
        RunRoomEditorChromeCase(ctx);
        RunImageEditorChromeCase(ctx);
        RunModelEditorChromeCase(ctx);
        RunTerrainEditorChromeCase(ctx);
        RunShaderEditorChromeCase(ctx);
        RunParticleEditorChromeCase(ctx);
        RunPhysicsEditorChromeCase(ctx);
        RunAudioEditorChromeCase(ctx);
        RunF6HudChromeCase(ctx);
    }

    private static void RunDiscoveryCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Images.ShipAndAreDiscovered", () =>
        {
            IReadOnlyList<ThemeImage> images = ThemeCatalog.Images;
            HeadlessHarness.Assert(
                images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                $"Expected {ThemeCatalog.RequiredBuiltInImageThemes.Count} bundled theme images beside the executable, found {images.Count} "
                + $"in {ApplicationPaths.InstalledThemesDirectory}.");

            foreach (string expected in ThemeCatalog.RequiredBuiltInImageThemes)
            {
                HeadlessHarness.Assert(
                    images.Any(image => string.Equals(image.Name, expected, StringComparison.Ordinal)),
                    $"Bundled theme '{expected}' is missing. Discovered: "
                    + $"{string.Join(", ", images.Select(image => image.Name))}.");
            }

            HeadlessHarness.Assert(
                images.All(image => image.IsBuiltIn),
                "A non-built-in theme appeared in the installed Themes folder scan unexpectedly.");

            // The picker lists images before colour themes, and a colour theme still resolves.
            HeadlessHarness.Assert(
                ThemeCatalog.Names.Count == images.Count + ThemeCatalog.ColourNames.Count,
                "The theme list lost entries when image themes were merged in.");
            HeadlessHarness.Assert(
                ThemeCatalog.ColourNames.Count == 18,
                $"Colour theme catalogue shrank to {ThemeCatalog.ColourNames.Count}; only Dark may be retinted, never removed.");
            HeadlessHarness.Assert(
                ThemeCatalog.Get("Monokai").Canvas == Color.FromArgb(39, 40, 34),
                "A named colour theme stopped resolving once image themes joined the catalogue.");
            HeadlessHarness.Assert(
                ThemeCatalog.Get("Light").Canvas == Color.White,
                "Genesis Light / Light colour theme must remain selectable beside Genesis Dark.");
        });
    }

    /// <summary>
    /// Every shipped image must yield a palette a person can actually read.
    /// </summary>
    /// <remarks>
    /// Written against the contrast ratios rather than against specific colours, so re-tuning the
    /// derivation — or replacing an image — cannot quietly ship an unreadable theme. The floors are
    /// WCAG AA: 4.5 for body text, 3.0 for muted text and interface accents.
    /// </remarks>
    private static void RunLegibilityCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Images.DerivedPaletteStaysLegible", () =>
        {
            foreach (ThemeImage image in ThemeCatalog.Images)
            {
                ThemePalette palette = image.Palette;
                HeadlessHarness.Assert(!image.Failed, $"Theme image '{image.Name}' could not be read.");

                Check(image.Name, "text on canvas", palette.Text, palette.Canvas, 4.5);
                Check(image.Name, "text on surface", palette.Text, palette.Surface, 4.5);
                Check(image.Name, "text on raised surface", palette.Text, palette.SurfaceRaised, 4.5);
                Check(image.Name, "muted text on canvas", palette.TextMuted, palette.Canvas, 3.0);
                Check(image.Name, "accent on surface", palette.Accent, palette.Surface, 3.0);
                Check(image.Name, "error on surface", palette.Error, palette.Surface, 3.0);
                Check(image.Name, "warning on surface", palette.Warning, palette.Surface, 3.0);
                Check(image.Name, "success on surface", palette.Success, palette.Surface, 3.0);
            }

            // And the hostile inputs, which are the ones that actually broke it: a near-white image
            // drove the accent to pure white on a near-white surface (1.17:1) until the contrast
            // search learned to scan past the background rather than step greedily away from it.
            foreach ((string label, Color fill) in new[]
            {
                ("cream", Color.FromArgb(250, 244, 228)),
                ("near-white", Color.FromArgb(252, 252, 252)),
                ("near-black", Color.FromArgb(4, 4, 6)),
                ("flat grey", Color.FromArgb(128, 128, 128)),
                ("saturated red", Color.FromArgb(220, 20, 20)),
            })
            {
                using Bitmap flat = new(48, 48);
                using (Graphics graphics = Graphics.FromImage(flat)) graphics.Clear(fill);

                ThemePalette palette = PaletteExtractor.FromImage(flat);
                Check(label, "text on canvas", palette.Text, palette.Canvas, 4.5);
                Check(label, "accent on surface", palette.Accent, palette.Surface, 3.0);
            }
        });

        static void Check(string theme, string what, Color foreground, Color background, double floor)
        {
            double ratio = PaletteExtractor.ContrastRatio(foreground, background);
            HeadlessHarness.Assert(
                ratio >= floor,
                $"'{theme}': {what} is {ratio:N2}:1, below the {floor:N1}:1 floor "
                + $"(#{foreground.R:X2}{foreground.G:X2}{foreground.B:X2} on "
                + $"#{background.R:X2}{background.G:X2}{background.B:X2}).");
        }
    }

    /// <summary>
    /// The three bundled themes must not all derive the same accent.
    /// </summary>
    /// <remarks>
    /// This is a real regression, not a hypothetical. Pooling accent votes by RGB bucket let the
    /// large dark-navy field these images share outvote what makes each one distinctive, and all
    /// three produced the same blue — a picker offering three identical-looking themes. Votes are
    /// pooled by hue now, and this fails again if that changes.
    ///
    /// Measured as a spread of <b>hues</b>, deliberately. The first version of this check compared
    /// RGB distance, and the three wrong accents — <c>#5E97D4</c>, <c>#4591ED</c>, <c>#5E67D4</c> —
    /// were far enough apart in RGB to sail past it while being, to the eye, three shades of the
    /// same blue. A test that would not have caught the bug it was written for is worse than none,
    /// because it reports the ground as covered.
    /// </remarks>
    private static void RunDistinctAccentCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Images.AccentsDifferPerImage", () =>
        {
            List<(string Name, Color Accent)> accents =
                [.. ThemeCatalog.Images.Select(image => (image.Name, image.Palette.Accent))];

            double widest = 0;
            for (int first = 0; first < accents.Count; first++)
            {
                for (int second = first + 1; second < accents.Count; second++)
                {
                    widest = Math.Max(
                        widest, HueDistance(accents[first].Accent, accents[second].Accent));
                }
            }

            HeadlessHarness.Assert(
                accents.Count < 2 || widest > 60d,
                $"The bundled themes' accents span only {widest:N0}° of hue "
                + $"({string.Join(", ", accents.Select(a => $"{a.Name} #{a.Accent.R:X2}{a.Accent.G:X2}{a.Accent.B:X2}"))}). "
                + "The derivation is reading the dark field these images share rather than what "
                + "makes each one itself.");

            // The synthwave sunset's subject is a glowing orange sun. An accent that is not warm
            // means the scoring has gone back to preferring area over vividness.
            ThemeImage? synthwave = ThemeCatalog.FindImage("Synthwave Horizon");
            if (synthwave is not null)
            {
                Color accent = synthwave.Palette.Accent;
                HeadlessHarness.Assert(
                    accent.R > accent.B,
                    $"Synthwave Horizon derived a cool accent (#{accent.R:X2}{accent.G:X2}{accent.B:X2}); "
                    + "its defining feature is a warm sun.");
            }
        });
    }

    /// <summary>Shortest distance between two hues, in degrees, going either way round.</summary>
    private static double HueDistance(Color first, Color second)
    {
        double difference = Math.Abs(first.GetHue() - second.GetHue()) % 360d;
        return difference > 180d ? 360d - difference : difference;
    }

    private static void RunBackdropCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Backdrop.PaintsBehindControls", () =>
        {
            ThemeImage theme = ThemeCatalog.Images.First();
            ThemeService.SetPalette(theme.Palette);
            ThemeBackdrop.Use(theme);

            try
            {
                HeadlessHarness.Assert(ThemeBackdrop.IsActive, "The backdrop reported no picture to draw.");

                using Form host = GateSuite.NewHost(600, 400);
                Panel surface = new() { Dock = DockStyle.Fill };
                host.Controls.Add(surface);
                GateSuite.ShowHost(host);
                GateSuite.Pump(6, 25);

                using Bitmap canvas = new(surface.Width, surface.Height);
                using (Graphics graphics = Graphics.FromImage(canvas))
                {
                    HeadlessHarness.Assert(
                        ThemeBackdrop.Paint(graphics, surface, ThemeBackdrop.OpenScrim),
                        "The backdrop declined to paint into a shown control.");
                }

                // A picture, not a flat fill: sample a grid and require more than one colour. A
                // backdrop that silently degrades to the canvas colour would otherwise pass.
                HashSet<int> seen = [];
                for (int y = 10; y < canvas.Height - 10; y += 37)
                {
                    for (int x = 10; x < canvas.Width - 10; x += 37)
                    {
                        seen.Add(canvas.GetPixel(x, y).ToArgb());
                    }
                }

                HeadlessHarness.Assert(
                    seen.Count > 8,
                    $"The painted backdrop has only {seen.Count} distinct colours across the surface; "
                    + "it is a flat fill rather than the theme picture.");

                host.Close();
            }
            finally
            {
                // Later suites capture visuals against the plain palette; leaving a picture switched
                // on would change every screenshot that follows.
                ThemeBackdrop.Use(null);
                ThemeService.SetPalette(ThemePalette.Dark);
            }
        });
    }

    private static void RunDockingMdiBackdropCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Backdrop.PaintsDockingMdiDocumentRegion", () =>
        {
            ThemeImage theme = ThemeCatalog.Images.First();
            ThemeService.SetPalette(theme.Palette);
            ThemeBackdrop.Use(theme);

            try
            {
                using Form host = GateSuite.NewHost(760, 480);
                host.IsMdiContainer = true;
                PaintProbeDockPanel dock = new()
                {
                    BackColor = theme.Palette.Canvas,
                    Dock = DockStyle.Fill,
                    DocumentStyle = DocumentStyle.DockingMdi,
                    Theme = new VS2015DarkTheme(),
                };
                host.Controls.Add(dock);
                dock.BringToFront();
                BackdropSurface.Attach(dock, ThemeBackdrop.OpenScrim);

                // This is the shell's ordering. Applying the palette after the DockPanel theme
                // used to leave DockBackColor at VS grey, which DockPanelSuite painted over the
                // image after raising the normal Paint event.
                ThemeService.Apply(host);
                GateSuite.ShowHost(host);
                GateSuite.Pump(6, 25);

                HeadlessHarness.Assert(
                    host.Controls.OfType<MdiClient>().Any(),
                    "The DockingMdi host did not create the MDI document client under test.");

                Rectangle document = Rectangle.Inflate(dock.DocumentWindowBounds, -10, -10);
                HeadlessHarness.Assert(
                    document.Width > 0 && document.Height > 0,
                    $"DockingMdi exposed no document region ({dock.DocumentWindowBounds}).");

                using Bitmap canvas = dock.RenderPaintPipeline();

                HashSet<int> seen = [];
                for (int y = document.Top; y < document.Bottom; y += 37)
                {
                    for (int x = document.Left; x < document.Right; x += 37)
                    {
                        seen.Add(canvas.GetPixel(x, y).ToArgb());
                    }
                }

                HeadlessHarness.Assert(
                    seen.Count > 8,
                    $"The DockingMdi document region has only {seen.Count} distinct colours; "
                    + "DockPanelSuite painted a flat fill over the image-theme backdrop.");

                host.Close();
            }
            finally
            {
                ThemeBackdrop.Use(null);
                ThemeService.SetPalette(ThemePalette.Dark);
            }
        });
    }

    /// <summary>Renders DockPanelSuite's real paint-event/opaque-fill ordering into owned pixels.</summary>
    /// <remarks>
    /// <see cref="Control.DrawToBitmap(Bitmap, Rectangle)"/> does not preserve a DockPanel paint
    /// handler's pixels, while desktop capture has no application provenance. Calling the protected
    /// pipeline from a derived test control retains DockPanelSuite's exact <c>OnPaint</c> behaviour
    /// in a deterministic bitmap: the backdrop event runs first, then the library's conditional
    /// <see cref="DockPanel.DockBackColor"/> fill.
    /// </remarks>
    private sealed class PaintProbeDockPanel : DockPanel
    {
        public Bitmap RenderPaintPipeline()
        {
            Bitmap bitmap = new(Width, Height);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(BackColor);
            base.OnPaint(new PaintEventArgs(graphics, ClientRectangle));
            return bitmap;
        }
    }

    private static void RunBrandingCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Branding.ArtworkShipsBesideTheExecutable", () =>
        {
            HeadlessHarness.Assert(
                Branding.WindowIcon is not null,
                "Genesis.ico is missing from the output; windows would fall back to the default "
                + ".NET icon.");
            HeadlessHarness.Assert(
                Branding.Emblem is not null, "The Genesis emblem is missing from the output.");
            HeadlessHarness.Assert(
                Branding.Logo is not null, "The full Genesis logo is missing from the output.");

            // The emblem is masked to its disc so it composites onto a backdrop rather than sitting
            // on an opaque tile. A corner that is not transparent means the mask was lost.
            using Bitmap emblem = new(Branding.Emblem!);
            HeadlessHarness.Assert(
                emblem.GetPixel(2, 2).A == 0,
                "The emblem's corners are opaque; it will show as a square tile over the backdrop.");
        });
    }

    private static void RunPreferencesSeparationContractCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Preferences.SeparatesColourAndImageModes", () =>
        {
            try
            {
                string settingsDirectory = Path.Combine(ctx.OutputRoot, "ThemeModeCompatibility");
                Directory.CreateDirectory(settingsDirectory);
                string settingsFile = Path.Combine(settingsDirectory, "legacy-preferences.json");
                File.WriteAllText(
                    settingsFile,
                    """
                    {
                      "schemaVersion": 1,
                      "appearance": {
                        "theme": "Cosmic Nebula",
                        "density": "Comfortable",
                        "interfaceScale": 1.0,
                        "codeFontSize": 11,
                        "useAnimations": true
                      }
                    }
                    """);

                SettingsService settings = new(settingsFile);
                HeadlessHarness.Assert(
                    settings.Current.Appearance.Theme == "Cosmic Nebula",
                    "Loading a preferences file from before theme modes changed its theme name.");
                HeadlessHarness.Assert(
                    settings.Current.Appearance.ThemeMode == AppearanceThemeModes.Automatic,
                    "A legacy untyped theme was forced into the new colour mode instead of being inferred.");

                ThemeService.ApplySettings(settings.Current);
                HeadlessHarness.Assert(
                    string.Equals(
                        ThemeBackdrop.Current?.Name, "Cosmic Nebula", StringComparison.Ordinal),
                    "The legacy image-theme name no longer applies its backdrop after upgrade.");

                using PreferencesForm preferences = new(settings);
                Control? colourMode = FindControl(preferences, "ColourThemeMode");
                Control? imageMode = FindControl(preferences, "ImageThemeMode");
                Control? gallery = FindControl(preferences, "ImageThemeGallery");
                Control? colourPicker = FindControl(preferences, "ColourThemePicker");
                Control? apply = FindControl(preferences, "ApplyPreferences");

                HeadlessHarness.Assert(
                    colourMode is RadioButton && imageMode is RadioButton,
                    "Appearance still has one mixed theme picker instead of explicit colour/image modes.");
                HeadlessHarness.Assert(
                    gallery is ThemeImageGallery,
                    "Image mode has no thumbnail gallery.");
                HeadlessHarness.Assert(
                    imageMode is RadioButton { Checked: true }
                    && colourMode is RadioButton { Checked: false },
                    "The legacy image theme did not open Preferences in image mode.");

                ThemeImageGallery imageGallery = (ThemeImageGallery)gallery!;
                HeadlessHarness.Assert(
                    imageGallery.Images.Count == ThemeCatalog.Images.Count,
                    "The gallery does not offer every discovered image theme.");
                HeadlessHarness.Assert(
                    string.Equals(
                        imageGallery.SelectedTheme?.Name, "Cosmic Nebula", StringComparison.Ordinal),
                    "The gallery did not retain the legacy image-theme selection.");

                HeadlessHarness.Assert(
                    colourPicker is ComboBox && apply is Button,
                    "The separated theme controls cannot be applied through Preferences.");

                GateSuite.ShowHost(preferences);
                GateSuite.Pump(3, 10);
                ((Button)apply!).PerformClick();
                HeadlessHarness.Assert(
                    settings.Current.Appearance.ThemeMode == AppearanceThemeModes.Image
                    && settings.Current.Appearance.Theme == "Cosmic Nebula",
                    "Applying legacy Preferences did not persist an explicit image-mode selection.");

                ((RadioButton)colourMode!).Checked = true;
                ((ComboBox)colourPicker!).SelectedItem = "Monokai";
                ((Button)apply).PerformClick();
                HeadlessHarness.Assert(
                    settings.Current.Appearance.ThemeMode == AppearanceThemeModes.Colour
                    && settings.Current.Appearance.Theme == "Monokai",
                    "Colour mode and its independent palette selection did not persist together.");
                HeadlessHarness.Assert(
                    !ThemeBackdrop.IsActive
                    && ThemeService.Palette.Canvas == ThemeCatalog.GetColour("Monokai").Canvas,
                    "Explicit colour mode still applied an image backdrop or the wrong palette.");

                SettingsService reloaded = new(settingsFile);
                HeadlessHarness.Assert(
                    reloaded.Current.Appearance.ThemeMode == AppearanceThemeModes.Colour
                    && reloaded.Current.Appearance.Theme == "Monokai",
                    "The explicit theme mode did not survive a preferences.json round trip.");

                preferences.Close();
            }
            finally
            {
                ThemeBackdrop.Use(null);
                ThemeService.SetPalette(ThemePalette.Dark);
            }
        });

        static Control? FindControl(Control root, string name)
        {
            if (string.Equals(root.Name, name, StringComparison.Ordinal))
            {
                return root;
            }

            foreach (Control child in root.Controls)
            {
                Control? found = FindControl(child, name);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }
    }

    private static void RunThumbnailCacheCase(TestReport report)
    {
        HeadlessHarness.RunCase(report, "Theme.Preferences.GalleryCachesThumbnails", () =>
        {
            ThemeImage theme = ThemeCatalog.Images.First();
            ThemeThumbnailCache.Clear();

            Bitmap? first = ThemeThumbnailCache.Get(theme, new Size(154, 82));
            Bitmap? second = ThemeThumbnailCache.Get(theme, new Size(154, 82));
            HeadlessHarness.Assert(first is not null, "The gallery could not create a thumbnail.");
            HeadlessHarness.Assert(
                ReferenceEquals(first, second),
                "The same gallery card decoded/scaled a new thumbnail on its second repaint.");
            HeadlessHarness.Assert(
                ThemeThumbnailCache.CachedCount == 1,
                $"One image/size created {ThemeThumbnailCache.CachedCount} cached thumbnails.");
            HeadlessHarness.Assert(
                first!.Size == new Size(154, 82),
                $"Gallery thumbnail is {first.Size}, expected 154x82.");

            ThemeThumbnailCache.Clear();
            HeadlessHarness.Assert(
                ThemeThumbnailCache.CachedCount == 0,
                "Refreshing image themes would retain stale thumbnails.");
        });
    }

    /// <summary>
    /// Phase 0 visual parity: Genesis Dark is one palette across ThemeCatalog, EditorChrome, and
    /// ImageEditorChrome, and SuiteChromeBridge pushes both editor bridges.
    /// </summary>
    private static void RunGenesisDarkTokenStripCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.GenesisDarkTokenStrip", () =>
        {
            ThemePalette dark = ThemePalette.Dark;
            HeadlessHarness.Assert(
                dark.Canvas == Color.FromArgb(20, 22, 29)
                && dark.Surface == Color.FromArgb(28, 31, 40)
                && dark.Accent == Color.FromArgb(108, 140, 255),
                $"Genesis Dark catalog drifted from Application Design tokens (canvas={dark.Canvas}, accent={dark.Accent}).");
            HeadlessHarness.Assert(
                ThemeCatalog.ColourDisplayName("Dark") == "Genesis Dark",
                "Preferences should label Dark as Genesis Dark.");

            ThemeService.SetPalette(dark);
            SuiteChromeBridge.Push();

            HeadlessHarness.Assert(
                EditorChrome.Canvas == dark.Canvas
                && EditorChrome.Surface == dark.Surface
                && EditorChrome.Accent == dark.Accent,
                "Suite EditorChrome did not receive Genesis Dark from SuiteChromeBridge.");
            HeadlessHarness.Assert(
                ImageEditorChrome.Canvas == dark.Canvas
                && ImageEditorChrome.Surface == dark.Surface
                && ImageEditorChrome.Accent == dark.Accent,
                "ImageEditorChrome did not receive Genesis Dark from SuiteChromeBridge (live theme push missing).");

            using Form host = new()
            {
                Text = "Genesis Dark — design tokens",
                ClientSize = new Size(720, 420),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = dark.Canvas,
                ForeColor = dark.Text,
                Font = ThemeService.InterfaceFont,
            };

            Label title = new()
            {
                Text = "Genesis Dark",
                AutoSize = true,
                Font = ThemeService.HeadingFont,
                ForeColor = dark.Text,
                Location = new Point(24, 20),
            };
            Label subtitle = new()
            {
                Text = "ThemeCatalog · EditorChrome · ImageEditorChrome  (Phase 0)",
                AutoSize = true,
                ForeColor = dark.TextMuted,
                Location = new Point(26, 52),
            };
            host.Controls.Add(title);
            host.Controls.Add(subtitle);

            (string Name, Color Color)[] swatches =
            [
                ("Canvas", dark.Canvas),
                ("Surface", dark.Surface),
                ("Raised", dark.SurfaceRaised),
                ("Hover", dark.SurfaceHover),
                ("Border", dark.Border),
                ("Text", dark.Text),
                ("Muted", dark.TextMuted),
                ("Accent", dark.Accent),
                ("Success", dark.Success),
                ("Warning", dark.Warning),
                ("Error", dark.Error),
            ];

            const int cardW = 96;
            const int cardH = 88;
            const int gap = 12;
            int x0 = 24;
            int y0 = 96;
            for (int i = 0; i < swatches.Length; i++)
            {
                int col = i % 6;
                int row = i / 6;
                Panel card = new()
                {
                    Location = new Point(x0 + (col * (cardW + gap)), y0 + (row * (cardH + gap))),
                    Size = new Size(cardW, cardH),
                    BackColor = dark.Surface,
                };
                Panel chip = new()
                {
                    Location = new Point(8, 8),
                    Size = new Size(cardW - 16, 44),
                    BackColor = swatches[i].Color,
                };
                Label name = new()
                {
                    Text = swatches[i].Name,
                    AutoSize = true,
                    ForeColor = dark.Text,
                    Location = new Point(8, 58),
                };
                card.Controls.Add(chip);
                card.Controls.Add(name);
                host.Controls.Add(card);
            }

            Button primary = new()
            {
                Text = "Primary action",
                Location = new Point(24, 320),
                Size = new Size(140, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = dark.Accent,
                ForeColor = Color.White,
            };
            primary.FlatAppearance.BorderColor = dark.Accent;
            Button secondary = new()
            {
                Text = "Secondary",
                Location = new Point(176, 320),
                Size = new Size(120, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = dark.SurfaceRaised,
                ForeColor = dark.Text,
            };
            secondary.FlatAppearance.BorderColor = dark.Border;
            Panel sampleField = new()
            {
                Location = new Point(312, 320),
                Size = new Size(200, 36),
                BackColor = dark.SurfaceRaised,
                BorderStyle = BorderStyle.None,
            };
            Label fieldLabel = new()
            {
                Text = "Sample field",
                AutoSize = true,
                ForeColor = dark.TextMuted,
                Location = new Point(10, 9),
            };
            sampleField.Controls.Add(fieldLabel);
            sampleField.Paint += (_, e) =>
            {
                using Pen pen = new(dark.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, sampleField.Width - 1, sampleField.Height - 1);
            };

            host.Controls.Add(primary);
            host.Controls.Add(secondary);
            host.Controls.Add(sampleField);

            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);
            host.BringToFront();
            host.TopMost = true;
            GateSuite.Pump(4, 20);
            using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
            host.TopMost = false;

            string path = Path.Combine(ctx.Captures, "00-genesis-dark-token-strip.png");
            Directory.CreateDirectory(ctx.Captures);
            bitmap.Save(path, ImageFormat.Png);
            RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
            ctx.Report.Images.Add(new ImageResult(
                "Genesis Dark token strip",
                "00-genesis-dark-token-strip.png",
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance));
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 8,
                $"Token strip capture looks blank ({metrics.UniqueSampledColors} colours).");
        });
    }

    /// <summary>
    /// Phase 1: shared UI kit (preset cards, themed combo, flat scroll, toggle, empty state,
    /// inspector section) paints with Genesis Dark — without touching image-theme discovery.
    /// </summary>
    private static void RunSharedUiKitShowcaseCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.SharedUiKitShowcase", () =>
        {
            ThemePalette dark = ThemePalette.Dark;
            ThemeService.SetPalette(dark);
            SuiteChromeBridge.Push();

            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "UI kit work must not disturb built-in image theme discovery.");

            using Form host = new()
            {
                Text = "Shared UI kit — Phase 1",
                ClientSize = new Size(920, 560),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = EditorChrome.Canvas,
                ForeColor = EditorChrome.Text,
                Font = EditorChrome.BaseFont,
            };

            Label title = new()
            {
                Text = "Shared UI kit",
                AutoSize = true,
                Font = ThemeService.HeadingFont,
                ForeColor = EditorChrome.Text,
                Location = new Point(20, 16),
            };
            Label subtitle = new()
            {
                Text = "PresetGallery · ThemedComboBox · FlatScrollPanel · KitToggle · EmptyState · InspectorSection",
                AutoSize = true,
                ForeColor = EditorChrome.Muted,
                Location = new Point(22, 44),
            };
            host.Controls.Add(title);
            host.Controls.Add(subtitle);

            PresetGallery gallery = new()
            {
                Location = new Point(20, 76),
                Size = new Size(560, 160),
            };
            gallery.SetPresets(
            [
                ("2D Platformer", "Side-scroller starter", Color.FromArgb(90, 160, 255)),
                ("3D Nature Walk", "Terrain + foliage demo", Color.FromArgb(91, 202, 154)),
                ("3D World", "Physics playground", Color.FromArgb(245, 181, 81)),
            ]);
            if (gallery.Controls[0] is PresetCard first)
            {
                gallery.Select(first);
            }

            host.Controls.Add(gallery);

            Label comboLabel = new()
            {
                Text = "Themed combo",
                AutoSize = true,
                ForeColor = EditorChrome.Muted,
                Location = new Point(600, 76),
            };
            ThemedComboBox combo = new()
            {
                Location = new Point(600, 98),
                Width = 280,
            };
            combo.Items.AddRange(["Direct3D 11 (Recommended)", "Direct3D 12", "Vulkan", "OpenGL", "Software"]);
            combo.SelectedIndex = 0;
            host.Controls.Add(comboLabel);
            host.Controls.Add(combo);

            Label toggleLabel = new()
            {
                Text = "3D Grid",
                AutoSize = true,
                ForeColor = EditorChrome.Text,
                Location = new Point(600, 140),
            };
            KitToggle toggle = new()
            {
                Location = new Point(680, 136),
                Checked = true,
            };
            host.Controls.Add(toggleLabel);
            host.Controls.Add(toggle);

            InspectorSection section = new()
            {
                Title = "Transform",
                Location = new Point(600, 176),
                Size = new Size(280, 120),
            };
            section.Body.Controls.Add(new Label
            {
                Text = "X  0.00    Y  -1.30    Z  -3.50",
                AutoSize = true,
                ForeColor = EditorChrome.Text,
                Location = new Point(4, 4),
            });
            host.Controls.Add(section);

            FlatScrollPanel scroll = new()
            {
                Location = new Point(20, 252),
                Size = new Size(360, 180),
            };
            for (int i = 0; i < 16; i++)
            {
                Label row = new()
                {
                    Text = $"Scroll row {i + 1} — custom thin thumb",
                    AutoSize = false,
                    Size = new Size(330, 24),
                    Location = new Point(8, 8 + (i * 28)),
                    ForeColor = EditorChrome.Text,
                };
                scroll.Content.Controls.Add(row);
            }

            scroll.SetContentHeight(8 + (16 * 28) + 8);
            host.Controls.Add(scroll);

            EmptyStatePanel empty = new()
            {
                Location = new Point(400, 252),
                Size = new Size(480, 180),
                TitleText = "No particles yet",
                BodyText = "Pick a preset above — empty states beat a blank void.",
            };
            host.Controls.Add(empty);

            HeadlessHarness.Assert(gallery.SelectedCard is not null, "Preset gallery selection failed.");
            HeadlessHarness.Assert(combo.Items.Count == 5, "Themed combo lost its items.");
            HeadlessHarness.Assert(toggle.Checked, "Kit toggle should start on.");
            HeadlessHarness.Assert(section.Expanded, "Inspector section should start expanded.");
            HeadlessHarness.Assert(scroll.Content.Controls.Count == 16, "Flat scroll content was not filled.");

            GateSuite.ShowHost(host);
            GateSuite.Pump(6, 25);
            host.BringToFront();
            host.TopMost = true;
            GateSuite.Pump(4, 20);
            using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
            host.TopMost = false;

            string path = Path.Combine(ctx.Captures, "01-shared-ui-kit.png");
            Directory.CreateDirectory(ctx.Captures);
            bitmap.Save(path, ImageFormat.Png);
            RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
            ctx.Report.Images.Add(new ImageResult(
                "Shared UI kit showcase",
                "01-shared-ui-kit.png",
                metrics.Width,
                metrics.Height,
                metrics.UniqueSampledColors,
                metrics.AverageLuminance));
            HeadlessHarness.Assert(
                metrics.UniqueSampledColors >= 8,
                $"UI kit capture looks blank ({metrics.UniqueSampledColors} colours).");
        });
    }

    /// <summary>
    /// Phase 2: Project Hub matches WelcomeScreen chrome (brand, recent cards, nav) while keeping
    /// exactly six Available templates and all built-in image themes.
    /// </summary>
    private static void RunProjectHubParityCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ProjectHubParity", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Hub polish must not disturb built-in image themes.");
            HeadlessHarness.Assert(
                ProjectTemplateCatalog.Available.Count() == 6,
                "Hub must keep exactly six Available templates.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-hub-parity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                StudioServices services = new(
                    new SettingsService(Path.Combine(temporaryRoot, "preferences.json")),
                    new ProjectService(),
                    new ProjectValidator(),
                    new StudioLog(Path.Combine(temporaryRoot, "studio.log")));
                ProjectSession recent = services.Projects.CreateProject(temporaryRoot, "Solar Odyssey 3D");
                services.Settings.AddRecentProject(recent.Manifest.Name, recent.ProjectFile);

                using ProjectHubForm hub = new(services);
                GateSuite.ShowHost(hub);
                hub.ShowSection(HubSection.Projects);
                hub.ApplyResponsiveLayoutForTest();
                GateSuite.Pump(6, 25);

                NavigationBrandControl brand = HeadlessHarness.Require(
                    Descendants(hub)
                        .OfType<NavigationBrandControl>()
                        .FirstOrDefault(),
                    "Hub brand lock-up");
                HeadlessHarness.Assert(
                    brand.Title.Text == "Genesis Studio",
                    $"Hub brand title is '{brand.Title.Text}', expected Genesis Studio.");
                HeadlessHarness.Assert(
                    Descendants(hub.RecentProjects).OfType<Label>().Any(l => l.Name == "RecentProjectDate"),
                    "Recent cards must expose RecentProjectDate for layout gates.");
                HeadlessHarness.Assert(
                    Descendants(hub.RecentProjects).OfType<Label>().Any(l => l.Name == "RecentProjectPath"),
                    "Recent cards should show the project path (WelcomeScreen mock).");

                hub.BringToFront();
                hub.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap projectsBitmap = VisualCapture.CaptureWindowPixels(hub);
                hub.TopMost = false;
                SaveParityCapture(ctx, projectsBitmap, "02-project-hub.png", "Project Hub — Recent Projects");

                hub.ClickNavigation(HubSection.Templates);
                hub.ApplyResponsiveLayoutForTest();
                GateSuite.Pump(4, 20);
                hub.TopMost = true;
                GateSuite.Pump(3, 15);
                using Bitmap templatesBitmap = VisualCapture.CaptureWindowPixels(hub);
                hub.TopMost = false;
                SaveParityCapture(ctx, templatesBitmap, "02b-project-hub-templates.png", "Project Hub — Templates");
                hub.Hide();
                GateSuite.Pump(2, 15);

                using SplashForm splash = new();
                GateSuite.ShowHost(splash);
                splash.Invalidate(true);
                splash.Refresh();
                GateSuite.Pump(8, 30);
                splash.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap splashBitmap = CaptureFormPreferringClientPaint(splash);
                splash.TopMost = false;
                splash.Hide();
                SaveParityCapture(ctx, splashBitmap, "01-launch-splash.png", "Launch splash");

                using NewProjectDialog dialog = new("2D");
                GateSuite.ShowHost(dialog);
                dialog.Invalidate(true);
                dialog.Refresh();
                GateSuite.Pump(6, 25);
                dialog.TopMost = true;
                GateSuite.Pump(3, 15);
                using Bitmap dialogBitmap = CaptureFormPreferringClientPaint(dialog);
                dialog.TopMost = false;
                SaveParityCapture(ctx, dialogBitmap, "03-new-project.png", "New Project dialog");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Splash and some borderless forms can PrintWindow as a flat field; DrawToBitmap still gets
    /// OnPaintBackground / child paints for VisualParity gates.
    /// </summary>
    private static Bitmap CaptureFormPreferringClientPaint(Form form)
    {
        Bitmap printed = VisualCapture.CaptureWindowPixels(form);
        RuntimeImageMetrics printedMetrics = RuntimeImageMetrics.Measure(printed);
        if (printedMetrics.UniqueSampledColors >= 6)
        {
            return printed;
        }

        printed.Dispose();
        Size size = form.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            size = form.Size;
        }

        Bitmap drawn = new(Math.Max(1, size.Width), Math.Max(1, size.Height));
        form.DrawToBitmap(drawn, new Rectangle(Point.Empty, drawn.Size));
        return drawn;
    }

    /// <summary>
    /// D3D editors: PrintWindow often blanks ToolStrip/status while keeping the GPU surface.
    /// DrawToBitmap keeps WinForms chrome (viewport region black) and we blit a DX readback into
    /// the viewport bounds so VisualParity shows shell + scene together.
    /// </summary>
    private static Bitmap CaptureFormWithViewport3D(Form form, EditorViewport3D viewport)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(viewport);

        Size size = form.ClientSize;
        if (size.Width <= 0 || size.Height <= 0)
            size = form.Size;

        Bitmap composite = new(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppArgb);
        form.DrawToBitmap(composite, new Rectangle(Point.Empty, composite.Size));

        using Bitmap? frame = viewport.CaptureFrame(settleFrames: 4);
        if (frame is null)
            return composite;

        Rectangle bounds = form.RectangleToClient(viewport.RectangleToScreen(viewport.ClientRectangle));
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return composite;

        using Graphics graphics = Graphics.FromImage(composite);
        graphics.SetClip(new Rectangle(Point.Empty, composite.Size));
        graphics.DrawImage(frame, bounds);
        return composite;
    }

    private static bool ToolbarBandLooksLikeChrome(Bitmap bitmap)
    {
        // Sample the command-bar band: Genesis Dark raised/surface, not PrintWindow white.
        int whiteish = 0;
        int samples = 0;
        int y = Math.Clamp(18, 0, bitmap.Height - 1);
        for (int x = 40; x < bitmap.Width - 40; x += 24)
        {
            Color color = bitmap.GetPixel(x, y);
            samples++;
            if (color.R > 220 && color.G > 220 && color.B > 220)
                whiteish++;
        }

        return samples > 0 && whiteish * 3 < samples;
    }

    /// <summary>
    /// Phase 3: Studio shell dock chrome (tabs, tool captions, Assets/Inspector/Console)
    /// follows Genesis Dark tokens rather than stock VS2015 blues.
    /// </summary>
    private static void RunStudioShellDockChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.StudioShellDockChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Shell dock polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-shell-docks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                StudioServices services = new(
                    new SettingsService(Path.Combine(temporaryRoot, "preferences.json")),
                    new ProjectService(),
                    new ProjectValidator(),
                    new StudioLog(Path.Combine(temporaryRoot, "studio.log")));
                ProjectSession project = services.Projects.CreateProject(
                    temporaryRoot,
                    "Dock Chrome Walk",
                    "2D");

                using StudioShellForm studio = new(services, project, persistLayout: false);
                GateSuite.ShowHost(studio);
                GateSuite.Pump(10, 30);

                ThemeBase dockTheme = HeadlessHarness.Require(studio.DockPanel.Theme, "DockPanel theme");
                DockPanelColorPalette palette = HeadlessHarness.Require(
                    dockTheme.ColorPalette,
                    "DockPanel ColorPalette");
                Color tabAccent = palette.TabSelectedActive.Background;
                Color expectedAccent = ThemePalette.Dark.Accent;
                HeadlessHarness.Assert(
                    tabAccent.ToArgb() == expectedAccent.ToArgb(),
                    $"Document tab accent is #{tabAccent.ToArgb():X8}, expected Genesis Dark #{expectedAccent.ToArgb():X8}.");
                HeadlessHarness.Assert(
                    palette.ToolWindowCaptionActive.Background.ToArgb() == expectedAccent.ToArgb(),
                    "Tool-window caption is not Genesis Dark accent.");
                HeadlessHarness.Assert(
                    palette.MainWindowActive.Background.ToArgb() == ThemePalette.Dark.Canvas.ToArgb(),
                    "Dock MDI backdrop is not Genesis Dark canvas.");

                string? firstObject = Directory.EnumerateFiles(
                        Path.Combine(project.AssetsPath, "Objects"),
                        "*.object.json",
                        SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (firstObject is not null)
                {
                    HeadlessHarness.Assert(
                        studio.AssetBrowser.SelectPath(firstObject),
                        "Could not select a seeded object for the Inspector chrome capture.");
                }

                GateSuite.Pump(6, 25);
                studio.BringToFront();
                studio.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap shellBitmap = VisualCapture.CaptureWindowPixels(studio);
                studio.TopMost = false;
                SaveParityCapture(ctx, shellBitmap, "04-studio-shell-docks.png", "Studio shell docks");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 4: Preferences chrome (nav pill, filter, Appearance UiKit) matches Genesis Dark
    /// without inventing new settings.
    /// </summary>
    private static void RunPreferencesChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.PreferencesChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Preferences polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-prefs-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                SettingsService settings = new(Path.Combine(temporaryRoot, "preferences.json"));
                using PreferencesForm preferences = new(settings);
                GateSuite.ShowHost(preferences);
                GateSuite.Pump(6, 25);

                HeadlessHarness.Assert(
                    preferences.Text.Contains("Genesis Studio", StringComparison.Ordinal),
                    "Preferences window title lost the Genesis Studio brand.");
                HeadlessHarness.Assert(
                    preferences.SelectCategory("Appearance"),
                    "Could not open the Appearance preferences page.");
                GateSuite.Pump(4, 20);

                Control colourMode = HeadlessHarness.Require(
                    Descendants(preferences).FirstOrDefault(c => c.Name == "ColourThemeMode"),
                    "ColourThemeMode");
                Control gallery = HeadlessHarness.Require(
                    Descendants(preferences).FirstOrDefault(c => c.Name == "ImageThemeGallery"),
                    "ImageThemeGallery");
                Control colourPicker = HeadlessHarness.Require(
                    Descendants(preferences).FirstOrDefault(c => c.Name == "ColourThemePicker"),
                    "ColourThemePicker");
                HeadlessHarness.Assert(
                    colourPicker is ThemedComboBox,
                    "Colour theme picker is not a ThemedComboBox.");
                HeadlessHarness.Assert(
                    Descendants(preferences).OfType<KitToggle>().Any(t => t.Name == "AnimationsToggle"),
                    "Animations KitToggle is missing.");
                HeadlessHarness.Assert(
                    Descendants(preferences).Any(c => c.Name == "PreferencesCategoryFilter"),
                    "Category filter field is missing.");
                _ = colourMode;
                _ = gallery;

                preferences.BringToFront();
                preferences.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap appearanceBitmap = VisualCapture.CaptureWindowPixels(preferences);
                preferences.TopMost = false;
                SaveParityCapture(
                    ctx,
                    appearanceBitmap,
                    "05-preferences-appearance.png",
                    "Preferences — Appearance");

                HeadlessHarness.Assert(
                    preferences.SelectCategory("General"),
                    "Could not open the General preferences page.");
                GateSuite.Pump(3, 15);
                preferences.TopMost = true;
                GateSuite.Pump(3, 15);
                using Bitmap generalBitmap = VisualCapture.CaptureWindowPixels(preferences);
                preferences.TopMost = false;
                SaveParityCapture(
                    ctx,
                    generalBitmap,
                    "05b-preferences-general.png",
                    "Preferences — General");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 1: Notes Editor chrome (view toggles, library/outline, document stats)
    /// matches NotesEditor.png language without inventing note-library backends.
    /// </summary>
    private static void RunNotesEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.NotesEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Notes editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-notes-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            string notePath = Path.Combine(temporaryRoot, "Game Design Document.md");
            File.WriteAllText(
                notePath,
                """
                # Game Design Document

                ## Combat loop
                Player uses **dash** then *strike*. See [Player](Assets/Objects/Player.object.json).

                ## Economy
                Coins drop from [Coin](Assets/Objects/Coin.object.json).

                ```
                gold=100
                ```
                """);

            try
            {
                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Notes",
                };
                NoteEditorControl editor = new(notePath, temporaryRoot)
                {
                    Dock = DockStyle.Fill,
                    Name = "NotesEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                editor.SetViewMode(NoteEditorViewMode.Split);
                GateSuite.Pump(8, 30);

                HeadlessHarness.Assert(
                    editor.ViewMode == NoteEditorViewMode.Split,
                    "Notes editor should open in Split view (NotesEditor mock).");
                HeadlessHarness.Assert(
                    Descendants(host).Any(c => c.Name == "NoteStatsCard"),
                    "Document Stats card is missing.");
                HeadlessHarness.Assert(
                    Descendants(host).Any(c => c.Name == "NoteSummaryCard"),
                    "Note Library summary card is missing.");
                Label words = HeadlessHarness.Require(
                    Descendants(host).OfType<Label>().FirstOrDefault(l => l.Name == "NoteStatsWords"),
                    "Words stat");
                HeadlessHarness.Assert(
                    !string.IsNullOrWhiteSpace(words.Text) && words.Text != "0",
                    $"Word count should be populated, got '{words.Text}'.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                host.TopMost = false;
                SaveParityCapture(ctx, bitmap, "06-notes-editor.png", "Notes Editor — Split");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 2: Image Viewer chrome (tokens, themed combos, frame selection) without
    /// removing any Viewer tools, panels, or Texture Group behaviour.
    /// </summary>
    private static void RunImageViewerChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ImageViewerChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Image Viewer polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-image-viewer-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Image Viewer Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string imagePath = resources.CreateResource(
                    resources.AssetsRoot,
                    ResourceKind.Image,
                    "Hero Walk");

                ImageDocument document = ImageDocument.CreateDefault(64, 48);
                document.Usage.Allowed = ImageUsage.Sprite | ImageUsage.Texture;
                ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 48, Color.FromArgb(255, 40, 120, 220));
                workspace.AddFrame(duplicateCurrent: true);
                ImageWorkspaceStorage.SynchronizeDocument(document, workspace);
                ImageDocumentSession session = new(document, imagePath, ImageDocumentAccess.Editor);
                ImageWorkspaceStorage.Save(session, workspace);
                workspace = ImageWorkspaceStorage.Load(session);

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Image Viewer",
                };
                ImageViewerControl viewer = new(session, workspace)
                {
                    Dock = DockStyle.Fill,
                    Name = "ImageViewerChrome",
                };
                host.Controls.Add(viewer);
                GateSuite.ShowHost(host);
                GateSuite.Pump(10, 30);

                HeadlessHarness.Assert(
                    viewer.UsesTargetImageViewerShell,
                    "Image Viewer shell layout probes failed after chrome polish.");
                HeadlessHarness.Assert(
                    viewer.HasTextureGroupChrome,
                    "Texture Group chrome must remain present.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ImageThemedComboBox>()
                        .Any(c => c.Name == "TextureGroupPicker"),
                    "TextureGroupPicker must remain an ImageThemedComboBox (same Name, same behaviour).");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ListBox>().Any(list => list.DrawMode == DrawMode.OwnerDrawFixed),
                    "Frame list should keep owner-draw accent selection without losing items.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                host.TopMost = false;
                SaveParityCapture(ctx, bitmap, "07-image-viewer.png", "Image Viewer");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 3: Room Editor chrome (ThemedComboBox fields, Genesis Dark surfaces) without
    /// removing any Room tools, panels, placement, gizmos, or viewport/follow behaviour.
    /// </summary>
    private static void RunRoomEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.RoomEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Room Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-room-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Room Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string roomsFolder = Path.Combine(resources.AssetsRoot, "Rooms");
                Directory.CreateDirectory(roomsFolder);
                string roomPath = resources.CreateResource(
                    roomsFolder,
                    ResourceKind.Room,
                    "Chrome Arena");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Room Editor",
                };
                RoomEditorControl editor = new(roomPath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                    Name = "RoomEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(12, 30);

                HeadlessHarness.Assert(!editor.ViewMode3D, "New room should open in 2D for the chrome capture.");
                HeadlessHarness.Assert(
                    editor.Room.Nodes.Count == 0,
                    "Chrome capture room should start empty (no invented scene content).");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ThemedComboBox>().Any(),
                    "Room Editor should expose at least one ThemedComboBox after chrome polish.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ListView>().Any(list => list.Name == "RoomSceneOutliner"),
                    "Scene outliner must remain present.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                host.TopMost = false;
                SaveParityCapture(ctx, bitmap, "08-room-editor.png", "Room Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 4: Image Editor chrome (ImageThemedComboBox, accent lists, chrome surfaces)
    /// without removing any tools, panels, timeline, rigging, or material workflows.
    /// </summary>
    private static void RunImageEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ImageEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Image Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-image-editor-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Image Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string imagePath = resources.CreateResource(
                    resources.AssetsRoot,
                    ResourceKind.Image,
                    "Hero Paint");

                ImageDocument document = ImageDocument.CreateDefault(64, 48);
                document.Usage.Allowed = ImageUsage.Sprite | ImageUsage.Texture;
                ImageWorkspace workspace = ImageWorkspace.CreateBlank(64, 48, Color.FromArgb(255, 40, 120, 220));
                workspace.AddFrame(duplicateCurrent: true);
                ImageWorkspaceStorage.SynchronizeDocument(document, workspace);
                ImageDocumentSession session = new(document, imagePath, ImageDocumentAccess.Editor);
                ImageWorkspaceStorage.Save(session, workspace);
                workspace = ImageWorkspaceStorage.Load(session);

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Image Editor",
                };
                ImageEditorControl editor = new(session, workspace)
                {
                    Dock = DockStyle.Fill,
                    Name = "ImageEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(12, 30);

                HeadlessHarness.Assert(
                    editor.UsesTargetImageShell,
                    "Image Editor shell layout probe failed after chrome polish.");
                HeadlessHarness.Assert(
                    editor.IsToolsPanelVisible && editor.IsInspectorPanelVisible && editor.IsTimelinePanelVisible,
                    "Image Editor tools/inspector/timeline panels must remain visible.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ImageThemedComboBox>()
                        .Count(c => c.Name is "BlendModePicker" or "ChannelPicker" or "TimelineClipPicker" or "BrushShape") == 4,
                    "Blend/channel/timeline/current-tool shape pickers must remain ImageThemedComboBox instances.");
                HeadlessHarness.Assert(
                    !Descendants(host).Any(control => control.Name is "SideClipPicker" or "ImageEditorBoneList"),
                    "Removed right-panel Animation/Rigging controls must not remain in the editor layout.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ListBox>()
                        .Where(list => list.Name is "ImageEditorLayerList" or "ImageEditorTimelineList")
                        .All(list => list.DrawMode == DrawMode.OwnerDrawFixed && list.BorderStyle == BorderStyle.None),
                    "Layer/timeline lists should keep owner-draw accent selection without FixedSingle borders.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(4, 20);
                using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                host.TopMost = false;
                SaveParityCapture(ctx, bitmap, "09-image-editor.png", "Image Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 5: Model Composer chrome (ThemedComboBox production/animation fields, accent
    /// part list) without removing Viewer/Composer roles, modes, gizmos, or generation tools.
    /// </summary>
    private static void RunModelEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ModelEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Model Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-model-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Model Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string modelsFolder = Path.Combine(resources.AssetsRoot, "Models");
                Directory.CreateDirectory(modelsFolder);
                string modelPath = resources.CreateResource(
                    modelsFolder,
                    ResourceKind.Model,
                    "Chrome Hero");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Model Composer",
                };
                ModelEditorControl editor = new(modelPath, project.RootPath, ModelEditorRole.Composer)
                {
                    Dock = DockStyle.Fill,
                    Name = "ModelEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(12, 30);
                editor.FrameModelForTest();
                GateSuite.Pump(4, 20);

                HeadlessHarness.Assert(editor.IsComposer, "Chrome capture should host the Composer role.");
                HeadlessHarness.Assert(
                    editor.Mode == ModelEditorMode.Compose,
                    "Composer should open in Compose mode.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ThemedComboBox>().Any(combo => combo.Name == "ModelMaterialPicker"),
                    "The replacement material picker must use the shared themed dropdown.");
                ListBox parts = HeadlessHarness.Require(
                    Descendants(host).OfType<ListBox>().FirstOrDefault(list => list.Name == "ModelMeshGroups"),
                    "ModelMeshGroups");
                HeadlessHarness.Assert(
                    parts.DrawMode == DrawMode.OwnerDrawFixed && parts.BorderStyle == BorderStyle.None,
                    "Parts list should keep owner-draw accent selection.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = CaptureFormWithViewport3D(host, editor.Viewport);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Model Composer capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "10-model-editor.png", "Model Composer");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 6: Terrain Editor chrome (ThemedComboBox fields, Genesis Dark lists) without
    /// removing sculpt/paint/foliage/water/paths/entity tools. Uses viewport composite capture.
    /// </summary>
    private static void RunTerrainEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.TerrainEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Terrain Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-terrain-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Terrain Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string terrainFolder = Path.Combine(resources.AssetsRoot, "Terrain");
                Directory.CreateDirectory(terrainFolder);
                string terrainPath = resources.CreateResource(
                    terrainFolder,
                    ResourceKind.Terrain,
                    "Chrome Meadow");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Terrain Editor",
                };
                TerrainEditorControl editor = new(terrainPath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                    Name = "TerrainEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(14, 30);
                editor.SetMode(TerrainEditorControl.TerrainEditorMode.Sculpt);
                GateSuite.Pump(6, 25);

                HeadlessHarness.Assert(
                    editor.ActiveMode == TerrainEditorControl.TerrainEditorMode.Sculpt,
                    "Terrain chrome capture should open in Sculpt mode.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ThemedComboBox>().Any(),
                    "Terrain Editor should expose at least one ThemedComboBox after chrome polish.");
                ListBox layers = HeadlessHarness.Require(
                    Descendants(host).OfType<ListBox>().FirstOrDefault(list => list.Name == "TerrainPaintLayerList"),
                    "TerrainPaintLayerList");
                HeadlessHarness.Assert(
                    layers.DrawMode == DrawMode.OwnerDrawFixed && layers.BorderStyle == BorderStyle.None,
                    "Paint layer list should keep owner-draw accent selection.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = CaptureFormWithViewport3D(host, editor.Viewport);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Terrain Editor capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "11-terrain-editor.png", "Terrain Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 7: Shader Editor chrome (ThemedComboBox pickers, accent preset list) without
    /// removing Preset/Code modes, compile, bindings, or live preview. Uses viewport composite capture.
    /// </summary>
    private static void RunShaderEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ShaderEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Shader Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-shader-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Shader Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string shadersFolder = Path.Combine(resources.AssetsRoot, "Shaders");
                Directory.CreateDirectory(shadersFolder);
                string shaderPath = resources.CreateResource(
                    shadersFolder,
                    ResourceKind.Shader,
                    "Chrome Rainbow");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Shader Editor",
                };
                ShaderEditorControl editor = new(shaderPath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                    Name = "ShaderEditorChrome",
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(14, 30);
                if (editor.PresetNames.Count > 0)
                    editor.SelectPreset(editor.PresetNames[0]);
                editor.SetPreviewVisible(true);
                GateSuite.Pump(8, 30);

                HeadlessHarness.Assert(
                    editor.PreviewVisible,
                    "Shader preview should remain visible for the chrome capture.");
                HeadlessHarness.Assert(
                    Descendants(host).OfType<ThemedComboBox>()
                        .Count(c => c.Name is "ShaderTargetTypePicker" or "ShaderTargetAssetPicker"
                            or "ShaderTerrainComponentPicker" or "ShaderProfilePicker") >= 4,
                    "Shader target/profile pickers must remain ThemedComboBox instances.");
                ListBox presets = HeadlessHarness.Require(
                    Descendants(host).OfType<ListBox>().FirstOrDefault(list => list.Name == "ShaderPresetList"),
                    "ShaderPresetList");
                HeadlessHarness.Assert(
                    presets.DrawMode == DrawMode.OwnerDrawFixed && presets.BorderStyle == BorderStyle.None,
                    "Preset list should keep owner-draw accent selection.");

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = CaptureFormWithViewport3D(host, editor.Viewport);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Shader Editor capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "12-shader-editor.png", "Shader Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 8: Particle Editor chrome. Light gate — themes intact + composite capture only.
    /// </summary>
    private static void RunParticleEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.ParticleEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Particle Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-particle-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Particle Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string particlesFolder = Path.Combine(resources.AssetsRoot, "Particles");
                Directory.CreateDirectory(particlesFolder);
                string particlePath = resources.CreateResource(
                    particlesFolder,
                    ResourceKind.Particle,
                    "Chrome Spark");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Particle Editor",
                };
                ParticleEditorControl editor = new(particlePath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(14, 30);
                editor.SetAuthoringMode(ParticleAuthoringMode.Properties);
                GateSuite.Pump(4, 20);

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = CaptureFormWithViewport3D(host, editor.Viewport);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Particle Editor capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "13-particle-editor.png", "Particle Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 9: Physics Editor chrome. Light gate — themes intact + capture only.
    /// </summary>
    private static void RunPhysicsEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.PhysicsEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Physics Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-physics-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Physics Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string physicsFolder = Path.Combine(resources.AssetsRoot, "Physics");
                Directory.CreateDirectory(physicsFolder);
                string physicsPath = resources.CreateResource(
                    physicsFolder,
                    ResourceKind.Physics,
                    "Chrome Rubber");

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Physics Editor",
                };
                PhysicsEditorControl editor = new(physicsPath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(14, 30);
                editor.SetAuthoringMode(PhysicsAuthoringMode.Properties);
                GateSuite.Pump(6, 25);

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = CaptureFormWithViewport3D(host, editor.Viewport);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Physics Editor capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "14-physics-editor.png", "Physics Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
    /// Phase 5 slice 10: Audio Editor chrome. Light gate — themes intact + capture only.
    /// </summary>
    private static void RunAudioEditorChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.AudioEditorChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "Audio Editor polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "Genesis-audio-chrome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                ProjectSession project = new ProjectService().CreateProject(
                    temporaryRoot,
                    "Audio Editor Chrome",
                    "Blank");
                ResourceService resources = new(project);
                string audioFolder = Path.Combine(resources.AssetsRoot, "Audio");
                Directory.CreateDirectory(audioFolder);
                string audioPath = resources.CreateResource(
                    audioFolder,
                    ResourceKind.Audio,
                    "Chrome Tone");

                // Tiny silent WAV so the waveform panel has something to paint.
                string wavPath = Path.Combine(audioFolder, "Chrome Tone.wav");
                WriteSilentWav(wavPath, sampleRate: 22050, seconds: 0.25f);

                using Form host = new()
                {
                    BackColor = ThemeService.Palette.Canvas,
                    ClientSize = new Size(1280, 780),
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.Manual,
                    Text = "Genesis Studio — Audio Editor",
                };
                AudioEditorControl editor = new(audioPath, project.RootPath)
                {
                    Dock = DockStyle.Fill,
                };
                host.Controls.Add(editor);
                GateSuite.ShowHost(host);
                GateSuite.Pump(14, 30);

                host.BringToFront();
                host.TopMost = true;
                GateSuite.Pump(6, 25);
                using Bitmap bitmap = VisualCapture.CaptureWindowPixels(host);
                host.TopMost = false;
                HeadlessHarness.Assert(
                    ToolbarBandLooksLikeChrome(bitmap),
                    "Audio Editor capture toolbar band looks blank/white — chrome was not composited.");
                SaveParityCapture(ctx, bitmap, "15-audio-editor.png", "Audio Editor");
            }
            finally
            {
                try { Directory.Delete(temporaryRoot, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        });
    }

    /// <summary>
            /// Phase 6: Ember F6 HUD chrome. Light gate — compact card + footer over a live 3D
            /// scene matching DebugRuntimeF6.png (expanded panels available via Expand / Tab).
            /// </summary>
    private static void RunF6HudChromeCase(HeadlessContext ctx)
    {
        HeadlessHarness.RunCase(ctx.Report, "Theme.Visual.F6HudChrome", () =>
        {
            HeadlessHarness.Assert(
                ThemeCatalog.Images.Count >= ThemeCatalog.RequiredBuiltInImageThemes.Count,
                "F6 HUD polish must not disturb built-in image themes.");

            ThemeService.SetPalette(ThemePalette.Dark);
            SuiteChromeBridge.Push();

            string scenePath = Path.Combine(ctx.Captures, "_f6-hud-scene.png");
            Directory.CreateDirectory(ctx.Captures);

            using RuntimeViewportHarness harness = new(1280, 720);
            _ = harness.Capture3D(scenePath);
            IRenderController renderer = harness.Renderer;

            using RuntimeScene debugScene = new("F6 Hud Chrome");
            debugScene.Camera3D.AspectRatio = 1280f / 720f;
            SeedF6DebugMarkers(debugScene);

            using Bitmap scene = new(scenePath);
            using Bitmap composite = new(scene.Width, scene.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(composite))
            {
                g.DrawImage(scene, 0, 0, scene.Width, scene.Height);

                var overlay = new DebugOverlay();
                overlay.BindScene(debugScene, "Level 1");
                overlay.ShowExpandedPanels = false; // mock-faithful compact chrome
                overlay.ShowWireframe = true;
                overlay.ShowCameras = true;
                var input = new InputState();
                input.OnKeyDown(Key.F6);
                overlay.HandleInput(input, debugMode: true);
                overlay.Advance(1f / 60f);
                HeadlessHarness.Assert(overlay.IsVisible, "F6 did not show the debug overlay for capture.");
                HeadlessHarness.Assert(!overlay.ShowExpandedPanels, "F6 chrome capture must use compact mode.");

                var hud = new GdiHudCanvas(g, composite.Width, composite.Height);
                overlay.Draw(hud, renderer, composite.Width, composite.Height);
                overlay.Dispose();
            }

            SaveParityCapture(ctx, composite, "16-f6-debug-hud.png", "F6 Debug HUD");
            HeadlessHarness.Assert(
                ThemeService.Palette.Canvas.ToArgb() == ThemePalette.Dark.Canvas.ToArgb(),
                "F6 HUD capture must run under Genesis Dark.");
        });
    }

    private static void SeedF6DebugMarkers(RuntimeScene scene)
    {
        EcsWorld world = scene.World;

        void Light(float x, float y, float z)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new TransformComponent { X = x, Y = y, Z = z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(entity, new PointLightComponent
            {
                Enabled = true,
                Color = new System.Numerics.Vector3(1f, 0.95f, 0.85f),
                Intensity = 1f,
                Radius = 6f,
                Falloff = 2f,
            });
        }

        void Audio(float x, float y, float z)
        {
            var entity = world.CreateEntity();
            world.Set(entity, new TransformComponent { X = x, Y = y, Z = z, ScaleX = 1, ScaleY = 1, ScaleZ = 1 });
            world.Set(entity, new AudioComponent { Asset = "Assets/Audio/Ambient.audio.json", Volume = 1f });
        }

        Light(-2f, 3f, 0f);
        Light(2.5f, 4f, -1f);
        Light(0f, 2.5f, 2f);
        Audio(-1f, 0.5f, 1.5f);
        Audio(1.5f, 0.4f, -1.2f);
    }

    /// <summary>GDI+ <see cref="IHudCanvas"/> used to composite the F6 overlay onto a capture.</summary>
    private sealed class GdiHudCanvas : IHudCanvas
    {
        private readonly Graphics _g;
        private readonly FontCache _fonts = new();

        public GdiHudCanvas(Graphics g, int width, int height)
        {
            _g = g ?? throw new ArgumentNullException(nameof(g));
            Width = width;
            Height = height;
            _g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            _g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        }

        public int Width { get; }
        public int Height { get; }

        public void Text(string text, float x, float y, float size, System.Numerics.Vector4 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            using SolidBrush brush = new(ToColor(color));
            _g.DrawString(text, _fonts.Get(size), brush, x, y);
        }

        public void TextCentered(string text, float centerX, float y, float width, float size, System.Numerics.Vector4 color)
        {
            if (string.IsNullOrEmpty(text)) return;
            using SolidBrush brush = new(ToColor(color));
            Font font = _fonts.Get(size);
            SizeF measured = _g.MeasureString(text, font);
            _g.DrawString(text, font, brush, centerX - measured.Width * 0.5f, y);
        }

        public void Rect(float x, float y, float w, float h, System.Numerics.Vector4 color, bool filled = true)
        {
            if (w <= 0f || h <= 0f) return;
            using SolidBrush brush = new(ToColor(color));
            if (filled)
            {
                _g.FillRectangle(brush, x, y, w, h);
            }
            else
            {
                using Pen pen = new(ToColor(color), 1f);
                _g.DrawRectangle(pen, x, y, w - 1f, h - 1f);
            }
        }

        public void Line(float x1, float y1, float x2, float y2, System.Numerics.Vector4 color, float thickness = 1.5f)
        {
            using Pen pen = new(ToColor(color), Math.Max(1f, thickness));
            _g.DrawLine(pen, x1, y1, x2, y2);
        }

        private static Color ToColor(System.Numerics.Vector4 c)
        {
            int a = Math.Clamp((int)(c.W * 255f), 0, 255);
            int r = Math.Clamp((int)(c.X * 255f), 0, 255);
            int g = Math.Clamp((int)(c.Y * 255f), 0, 255);
            int b = Math.Clamp((int)(c.Z * 255f), 0, 255);
            return Color.FromArgb(a, r, g, b);
        }

        private sealed class FontCache
        {
            private readonly Dictionary<int, Font> _bySize = new();

            public Font Get(float size)
            {
                int key = Math.Max(8, (int)Math.Round(size));
                if (_bySize.TryGetValue(key, out Font? existing)) return existing;
                Font font = new("Segoe UI", key, FontStyle.Regular, GraphicsUnit.Pixel);
                _bySize[key] = font;
                return font;
            }
        }
    }

    private static void WriteSilentWav(string path, int sampleRate, float seconds)
    {
        int sampleCount = Math.Max(1, (int)(sampleRate * seconds));
        int dataBytes = sampleCount * 2; // mono 16-bit
        using FileStream stream = File.Create(path);
        using BinaryWriter writer = new(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }

    private static void SaveParityCapture(
        HeadlessContext ctx,
        Bitmap bitmap,
        string fileName,
        string label)
    {
        string path = Path.Combine(ctx.Captures, fileName);
        Directory.CreateDirectory(ctx.Captures);
        bitmap.Save(path, ImageFormat.Png);
        RuntimeImageMetrics metrics = RuntimeImageMetrics.Measure(bitmap);
        ctx.Report.Images.Add(new ImageResult(
            label,
            fileName,
            metrics.Width,
            metrics.Height,
            metrics.UniqueSampledColors,
            metrics.AverageLuminance));
        HeadlessHarness.Assert(
            metrics.UniqueSampledColors >= 6,
            $"'{fileName}' capture looks blank ({metrics.UniqueSampledColors} colours).");
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
