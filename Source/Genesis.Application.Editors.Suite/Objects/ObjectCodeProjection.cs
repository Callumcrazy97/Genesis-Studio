using System.Text;
using System.Text.RegularExpressions;

namespace Genesis.Application.Editors.Suite.Objects;

/// <summary>
/// Keeps editor-only annotations out of the code pane, without regenerating user PGSL.
/// Character offsets let a text edit patch the original source and retain untouched annotations.
/// </summary>
internal sealed class ObjectCodeProjection
{
    private static readonly Regex Annotation = new(@"(?m)^[ \t]*// (?:</?(?:action|flow|branch)\b[^\r\n]*|@blueprint [^\r\n]*)(?:\r?\n|$)", RegexOptions.Compiled);
    private readonly string _source;
    private readonly List<int> _offsets = [];
    public string Text { get; }

    public ObjectCodeProjection(string source)
    {
        _source = source;
        var text = new StringBuilder();
        int cursor = 0;
        foreach (Match annotation in Annotation.Matches(source))
        {
            Append(cursor, annotation.Index); cursor = annotation.Index + annotation.Length;
        }
        Append(cursor, source.Length);
        Text = text.ToString();
        void Append(int start, int end)
        {
            // RichTextBox exposes LF even when its input used CRLF. Project that same text,
            // while keeping original file offsets so a small edit stays a small edit.
            for (int i = start; i < end; i++)
            {
                if (source[i] == '\r' && i + 1 < end && source[i + 1] == '\n') continue;
                text.Append(source[i]); _offsets.Add(i);
            }
        }
    }

    public int ToSource(int caret) => caret < _offsets.Count ? _offsets[Math.Max(0, caret)]
        : _offsets.Count > 0 ? _offsets[^1] + 1 : 0;

    public int ToVisible(int caret)
    {
        int index = _offsets.BinarySearch(caret);
        return index < 0 ? ~index : index;
    }

    public string ApplyEdit(string text)
    {
        if (text == Text) return _source;
        if (text.Length == 0) return string.Empty;
        int start = 0;
        while (start < text.Length && start < Text.Length && text[start] == Text[start]) start++;
        int end = Text.Length, newEnd = text.Length;
        while (end > start && newEnd > start && Text[end - 1] == text[newEnd - 1]) { end--; newEnd--; }
        int sourceStart = ToSource(start);
        int sourceEnd = end > start ? _offsets[end - 1] + 1 : sourceStart;
        return _source[..sourceStart] + text[start..newEnd] + _source[sourceEnd..];
    }
}
