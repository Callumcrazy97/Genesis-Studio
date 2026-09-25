using System.Text;
using Genesis.Application.Core.Resources;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class NoteEditorControl
{
    private void RefreshNoteLibrary()
    {
        string query = _noteSearch.Text.Trim();
        _noteLibrary.BeginUpdate(); _noteLibrary.Nodes.Clear();
        TreeNode root = new("Project Notes") { ImageKey = "notes" };
        foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, ResourceKind.Note)
                     .Where(entry => query.Length == 0 || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || entry.Reference.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            string relative = Path.GetRelativePath(Path.Combine(ProjectRoot, "Assets"), entry.FullPath);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            TreeNodeCollection nodes = root.Nodes;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                TreeNode? folder = nodes.Cast<TreeNode>().FirstOrDefault(node => node.Tag is null && node.Text == parts[i]);
                if (folder is null) { folder = new TreeNode(parts[i]); nodes.Add(folder); }
                nodes = folder.Nodes;
            }
            nodes.Add(new TreeNode(entry.DisplayName) { Tag = entry.FullPath, ToolTipText = entry.Reference });
        }
        if (root.Nodes.Count == 0) root.Nodes.Add(new TreeNode("No matching notes") { ForeColor = EditorChrome.Muted });
        _noteLibrary.Nodes.Add(root); root.Expand(); _noteLibrary.EndUpdate();
    }

    private void CreateProjectNote()
    {
        string? name = PromptNoteName(); if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            string folder = Path.Combine(ProjectRoot, "Assets", "Notes"); Directory.CreateDirectory(folder);
            ResourceService service = ProjectAssetIndex.OpenResourceService(ProjectRoot);
            string path = service.CreateResource(folder, ResourceKind.Note, name);
            RefreshNoteLibrary(); RequestOpenLinkedResource(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(this, exception.Message, "Create Note", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? PromptNoteName()
    {
        using Form prompt = new() { Text = "New Project Note", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(420, 142), BackColor = EditorChrome.Surface, ForeColor = EditorChrome.Text, Font = EditorChrome.BaseFont, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        TextBox input = new() { Text = "New Note", Location = new Point(16, 42), Width = 388 }; EditorChrome.StyleField(input);
        Label label = new() { Text = "Note name", Location = new Point(16, 15), Size = new Size(388, 22), ForeColor = EditorChrome.Muted };
        Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(232, 92), Size = new Size(82, 30) }; EditorChrome.StyleField(cancel);
        Button create = new() { Text = "Create", DialogResult = DialogResult.OK, Location = new Point(322, 92), Size = new Size(82, 30), BackColor = EditorChrome.Accent }; EditorChrome.StyleField(create);
        prompt.Controls.AddRange([label, input, cancel, create]); prompt.AcceptButton = create; prompt.CancelButton = cancel;
        return prompt.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : null;
    }

    private IEnumerable<NoteAssetReference> DetectAssetReferences()
    {
        string text = _source.Text;
        foreach ((ResourceKind kind, string category, string icon) in new[]
        {
            (ResourceKind.Room, "Linked Rooms", "▱"),
            (ResourceKind.GameObject, "Linked Objects", "◇"),
            (ResourceKind.Audio, "Linked Audio", "♪"),
            (ResourceKind.Model, "Linked Models", "⬡"),
            (ResourceKind.Image, "Linked Images", "▣"),
        })
        {
            foreach (ProjectAssetEntry entry in ProjectAssetIndex.Enumerate(ProjectRoot, kind))
            {
                if (text.Contains(entry.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(entry.Reference, StringComparison.OrdinalIgnoreCase))
                    yield return new NoteAssetReference(icon, category, entry.DisplayName, entry.FullPath, true);
            }
        }
    }

    private void OpenSelectedAssetReference()
    {
        if (_links.SelectedItem is NoteAssetReference { Navigable: true } reference)
            RequestOpenLinkedResource(reference.Path);
    }

    private void ToggleTaskFromPreview(Point location)
    {
        int previewCharacter = _preview.GetCharIndexFromPosition(location);
        int lineIndex = _preview.GetLineFromCharIndex(previewCharacter);
        string[] lines = _source.Text.Replace("\r\n", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return;
        if (lines[lineIndex].StartsWith("- [ ] ", StringComparison.Ordinal)) lines[lineIndex] = "- [x] " + lines[lineIndex][6..];
        else if (lines[lineIndex].StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)) lines[lineIndex] = "- [ ] " + lines[lineIndex][6..];
        else return;
        _source.Text = string.Join(Environment.NewLine, lines); MarkDirty(); RefreshDerivedState();
    }

    private void ExportMarkdown()
    {
        using SaveFileDialog picker = new() { Title = "Export Note as Markdown", Filter = "Markdown (*.md)|*.md", FileName = ResourceDisplayName.Format(ResourcePath) + ".md" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(picker.FileName, _source.Text, new UTF8Encoding(false));
    }

    private void ExportPdf()
    {
        using SaveFileDialog picker = new() { Title = "Export Note as PDF", Filter = "PDF document (*.pdf)|*.pdf", FileName = ResourceDisplayName.Format(ResourcePath) + ".pdf" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        SimpleNotePdf.Write(picker.FileName, ExtractTitle(_source.Text), _source.Text);
    }

    private sealed record NoteAssetReference(string Icon, string Category, string Name, string Path, bool Navigable)
    {
        public override string ToString() => $"{Icon}  {Category}  ·  {Name}";
    }
}

internal static class SimpleNotePdf
{
    public static void Write(string path, string title, string markdown)
    {
        List<string> lines = Wrap(markdown).ToList(); if (lines.Count == 0) lines.Add(string.Empty);
        const int perPage = 48; int pageCount = (lines.Count + perPage - 1) / perPage; int fontObject = 3 + pageCount * 2;
        List<byte[]> objects = [];
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        string kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(index => $"{3 + index * 2} 0 R"));
        objects.Add(Ascii($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>"));
        for (int page = 0; page < pageCount; page++)
        {
            int contentObject = 4 + page * 2;
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {fontObject} 0 R >> >> /Contents {contentObject} 0 R >>"));
            StringBuilder stream = new("BT\n/F1 11 Tf\n14 TL\n50 790 Td\n");
            if (page == 0) { stream.Append("/F1 16 Tf\n(").Append(Escape(title)).Append(") Tj\n0 -24 Td\n/F1 11 Tf\n"); }
            foreach (string line in lines.Skip(page * perPage).Take(perPage)) stream.Append('(').Append(Escape(line)).Append(") Tj\nT*\n");
            stream.Append("ET\n"); byte[] bytes = Ascii(stream.ToString());
            objects.Add(Ascii($"<< /Length {bytes.Length} >>\nstream\n").Concat(bytes).Concat(Ascii("endstream")).ToArray());
        }
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));

        using FileStream output = File.Create(path); Write(output, "%PDF-1.4\n%Genesis\n");
        List<long> offsets = [0];
        for (int i = 0; i < objects.Count; i++) { offsets.Add(output.Position); Write(output, $"{i + 1} 0 obj\n"); output.Write(objects[i]); Write(output, "\nendobj\n"); }
        long xref = output.Position; Write(output, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Write(output, $"{offset:0000000000} 00000 n \n");
        Write(output, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static IEnumerable<string> Wrap(string markdown)
    {
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).Replace("☑", "[x]", StringComparison.Ordinal).Replace("□", "[ ]", StringComparison.Ordinal);
            while (line.Length > 88) { int split = line.LastIndexOf(' ', 88); if (split < 24) split = 88; yield return line[..split]; line = line[split..].TrimStart(); }
            yield return line;
        }
    }
    private static string Escape(string text) => Sanitize(text).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
    private static string Sanitize(string text) => new(text.Select(character => character is >= ' ' and <= '~' ? character : character switch { '–' or '—' => '-', '’' => '\'', '“' or '”' => '"', _ => '?' }).ToArray());
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
    private static void Write(Stream stream, string text) { byte[] bytes = Ascii(text); stream.Write(bytes); }
}
