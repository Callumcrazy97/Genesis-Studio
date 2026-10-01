using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Genesis.Application.Core.UI;
using Genesis.Application.Studio.Controls;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Forms;

/// <summary>What a line of a guide is, once its Markdown marker has been removed.</summary>
public enum GuideBlockKind
{
    Title,
    Heading,
    Paragraph,
    Bullet,
    Numbered,
}

/// <summary>One block of a guide: a heading, a paragraph or a list item.</summary>
/// <param name="Kind">How the block is laid out.</param>
/// <param name="Text">The block's text, still carrying its inline <c>**bold**</c>, <c>*italic*</c> and <c>`code`</c> marks.</param>
/// <param name="Number">The item's number, for <see cref="GuideBlockKind.Numbered"/> blocks.</param>
public sealed record GuideBlock(GuideBlockKind Kind, string Text, int Number = 0);

/// <summary>
/// Shows a short Markdown guide inside Studio, so reading it does not depend on the machine having
/// a Markdown viewer.
/// </summary>
/// <remarks>
/// It understands what the Genesis guides use: <c>#</c> and <c>##</c> headings, paragraphs wrapped
/// over several lines, <c>-</c> bullets, numbered items and inline bold, italic and code.
/// </remarks>
public sealed partial class GuideViewerForm : DpiAwareForm
{
    /// <summary>The beginner guide's file name inside the Documentation folder.</summary>
    public const string GettingStartedFile = "GettingStarted.md";

    private readonly IReadOnlyList<GuideBlock> _blocks;
    private readonly RichTextBox _page;

    public GuideViewerForm(string title, string markdown)
    {
        _blocks = Parse(markdown);
        ClientSize = new Size(780, 720);
        MinimumSize = new Size(460, 360);
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = title;
        KeyPreview = true;

        _page = new RichTextBox
        {
            Name = "GuidePage",
            AccessibleName = title,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            ShortcutsEnabled = true,
            // Authored runs own their fonts and colours; the theme must not flatten them.
            Tag = Genesis.Application.Editors.Suite.EditorChrome.FormattedTextTag,
        };

        Panel footer = new()
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            Padding = new Padding(16, 10, 16, 12),
            // Canvas, like the dialogs: the button's rounded corners are drawn on that colour.
            Tag = "canvas",
        };
        ModernButton close = new()
        {
            Name = "GuideClose",
            Accent = true,
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Right,
            Text = "Close",
            Width = 112,
        };
        close.Click += (_, _) => Close();
        footer.Controls.Add(close);

        Controls.Add(_page);
        Controls.Add(footer);
        CancelButton = close;
        ThemeService.Apply(this);
        Render();
        UiTokens.Changed += OnThemeChanged;
    }

    /// <summary>The guide's blocks, in reading order.</summary>
    public IReadOnlyList<GuideBlock> Blocks => _blocks;

    /// <summary>The text as it is shown, without any Markdown marks.</summary>
    public string DisplayedText => _page.Text;

    /// <summary>Splits a Markdown guide into headings, paragraphs and list items.</summary>
    public static IReadOnlyList<GuideBlock> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        List<GuideBlock> blocks = [];
        GuideBlockKind openKind = GuideBlockKind.Paragraph;
        string? open = null;
        int openNumber = 0;

        void Close()
        {
            if (open is not null)
            {
                blocks.Add(new GuideBlock(openKind, open, openNumber));
                open = null;
            }
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Close();
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Close();
                blocks.Add(new GuideBlock(GuideBlockKind.Heading, line[3..].Trim()));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Close();
                blocks.Add(new GuideBlock(GuideBlockKind.Title, line[2..].Trim()));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Close();
                (openKind, open, openNumber) = (GuideBlockKind.Bullet, line[2..].Trim(), 0);
            }
            else if (NumberedItem().Match(line) is { Success: true } numbered)
            {
                Close();
                (openKind, open, openNumber) = (GuideBlockKind.Numbered, numbered.Groups[2].Value.Trim(),
                    int.Parse(numbered.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (open is not null)
            {
                // A wrapped line continues the paragraph or list item above it.
                open += " " + line;
            }
            else
            {
                (openKind, open, openNumber) = (GuideBlockKind.Paragraph, line, 0);
            }
        }

        Close();
        return blocks;
    }

    /// <summary>Removes inline marks, leaving the words a reader sees.</summary>
    public static string PlainText(string text) =>
        InlineMark().Replace(text, match => InlineContent(match));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UiTokens.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Render();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed)
        {
            Render();
        }
    }

    private void Render()
    {
        ThemePalette palette = ThemeService.Palette;
        Font body = ThemeService.InterfaceFont;
        float size = body.SizeInPoints + 1f;
        using Font regular = new(body.FontFamily, size, FontStyle.Regular);
        using Font bold = new(body.FontFamily, size, FontStyle.Bold);
        using Font italic = new(body.FontFamily, size, FontStyle.Italic);
        using Font code = new(ThemeService.CodeFont.FontFamily, size, FontStyle.Regular);
        using Font title = new(ThemeService.HeadingFont.FontFamily, size * 1.75f, FontStyle.Bold);
        using Font heading = new(ThemeService.HeadingFont.FontFamily, size * 1.25f, FontStyle.Bold);
        int margin = DpiLayout.Scale(this, 30);
        int hanging = DpiLayout.Scale(this, 24);

        _page.SuspendLayout();
        _page.BackColor = palette.SurfaceRaised;
        _page.Clear();

        void Begin(int indent, int hang)
        {
            _page.SelectionStart = _page.TextLength;
            _page.SelectionLength = 0;
            _page.SelectionIndent = margin + indent;
            _page.SelectionHangingIndent = hang;
            _page.SelectionRightIndent = margin;
        }

        void Run(string text, Font font, Color colour)
        {
            _page.SelectionStart = _page.TextLength;
            _page.SelectionLength = 0;
            _page.SelectionFont = font;
            _page.SelectionColor = colour;
            _page.AppendText(text);
        }

        void Inline(string text)
        {
            int position = 0;
            foreach (Match match in InlineMark().Matches(text))
            {
                if (match.Index > position)
                {
                    Run(text[position..match.Index], regular, palette.Text);
                }

                if (match.Groups[1].Success) Run(match.Groups[1].Value, bold, palette.Text);
                else if (match.Groups[2].Success) Run(match.Groups[2].Value, italic, palette.Text);
                else Run(match.Groups[3].Value, code, palette.Accent);
                position = match.Index + match.Length;
            }

            if (position < text.Length)
            {
                Run(text[position..], regular, palette.Text);
            }
        }

        // An empty first line is the page's top margin.
        Begin(0, 0);
        Run("\n", regular, palette.Text);
        GuideBlockKind? previous = null;
        foreach (GuideBlock block in _blocks)
        {
            bool list = block.Kind is GuideBlockKind.Bullet or GuideBlockKind.Numbered;
            bool afterList = previous is GuideBlockKind.Bullet or GuideBlockKind.Numbered;
            if (previous is not null && !(list && afterList))
            {
                // Space between blocks; consecutive list items stay together.
                Begin(0, 0);
                Run("\n", regular, palette.Text);
            }

            switch (block.Kind)
            {
                case GuideBlockKind.Title:
                    Begin(0, 0);
                    Run(PlainText(block.Text), title, palette.Text);
                    break;
                case GuideBlockKind.Heading:
                    Begin(0, 0);
                    Run(PlainText(block.Text), heading, palette.Accent);
                    break;
                case GuideBlockKind.Bullet:
                    Begin(0, hanging);
                    Run("•\t", bold, palette.Accent);
                    Inline(block.Text);
                    break;
                case GuideBlockKind.Numbered:
                    Begin(0, hanging);
                    Run(block.Number.ToString(System.Globalization.CultureInfo.CurrentCulture) + ".\t", bold, palette.Accent);
                    Inline(block.Text);
                    break;
                default:
                    Begin(0, 0);
                    Inline(block.Text);
                    break;
            }

            Run("\n", regular, palette.Text);
            previous = block.Kind;
        }

        // Tab stops are measured from the page edge, so this lines list text up with its hanging indent.
        _page.SelectAll();
        _page.SelectionTabs = [margin + hanging];
        _page.Select(0, 0);
        _page.ResumeLayout();
        _page.ScrollToCaret();
    }

    private static string InlineContent(Match match) =>
        match.Groups[1].Success ? match.Groups[1].Value
        : match.Groups[2].Success ? match.Groups[2].Value
        : match.Groups[3].Value;

    [GeneratedRegex(@"^(\d+)\.\s+(.*)$")]
    private static partial Regex NumberedItem();

    [GeneratedRegex(@"\*\*(.+?)\*\*|\*(.+?)\*|`(.+?)`")]
    private static partial Regex InlineMark();
}
