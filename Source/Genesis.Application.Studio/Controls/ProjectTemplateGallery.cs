using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Projects.Templates;
using Genesis.Application.Studio.Theme;

namespace Genesis.Application.Studio.Controls;

/// <summary>
/// Fills a card flow with the available templates under "2D games" and "3D games" headings. The
/// Project Hub's Templates page and New Project inside an open project share it, so both offer the
/// same starters in the same order.
/// </summary>
internal static class ProjectTemplateGallery
{
    public static void Fill(FlowLayoutPanel gallery, Action<ProjectTemplate> create, ICollection<ProjectTemplateCard> cards)
    {
        foreach (ProjectTemplateDimension dimension in new[] { ProjectTemplateDimension.TwoD, ProjectTemplateDimension.ThreeD })
        {
            gallery.Controls.Add(Heading(dimension == ProjectTemplateDimension.TwoD ? "2D games" : "3D games"));
            ProjectTemplate[] templates = [.. ProjectTemplateCatalog.Available.Where(template => template.Dimension == dimension)];
            if (templates.Length == 0)
            {
                gallery.Controls.Add(Note("No 3D starter yet. Start from a blank project for now."));
                continue;
            }
            foreach (ProjectTemplate template in templates)
            {
                ProjectTemplateCard card = new(template);
                card.CreateRequested += (_, _) => create(template);
                cards.Add(card);
                gallery.Controls.Add(card);
            }
        }
    }

    /// <summary>
    /// Sizes the gallery's text rows to the whole width so each section starts on its own line;
    /// the cards are sized by the caller.
    /// </summary>
    public static void LayoutHeadings(FlowLayoutPanel gallery, int width, int dpi)
    {
        foreach (Control control in gallery.Controls)
        {
            if (control is not Label label) continue;
            label.Margin = new Padding(0, 0, 0, DpiLayout.Scale(8, dpi));
            label.Size = new Size(width, label.GetPreferredSize(new Size(width, 0)).Height + DpiLayout.Scale(4, dpi));
        }
    }

    private static Label Heading(string text) => new()
    {
        AutoSize = false,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI Variable Display", 13f, FontStyle.Bold),
        ForeColor = ThemeService.Palette.Text,
        Text = text,
        UseMnemonic = false,
    };

    private static Label Note(string text) => new()
    {
        AutoSize = false,
        BackColor = Color.Transparent,
        ForeColor = ThemeService.Palette.TextMuted,
        Text = text,
        UseMnemonic = false,
    };
}
