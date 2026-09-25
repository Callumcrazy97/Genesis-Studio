using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Scripts;

/// <summary>One syntax-highlight rule: a regex and the colour its matches take.</summary>
public sealed record HighlightRule(Regex Pattern, Color Color, bool Bold = false);

/// <summary>
/// Reusable code editing core for PGSL and shader surfaces: dark themed RichTextBox with a
/// line-number gutter, debounced syntax highlighting, completion, and signature help.
/// </summary>
public sealed class CodeEditor : UserControl
{
    private const int WmSetRedraw = 0x000B;
    private const int SignatureStripLogicalHeight = 44;

    private readonly RichTextBox _text;
    private readonly Panel _gutter;
    private readonly Panel _editingHost;
    private readonly System.Windows.Forms.Timer _highlightTimer;
    private readonly List<HighlightRule> _rules = [];
    private readonly HashSet<int> _breakpoints = [];
    private int _currentExecutionLine;
    private bool _suppressChange;

    // IntelliSense & Signature Help
    private readonly ListBox _autoCompleteBox;
    private readonly Panel _signatureStrip;
    private readonly RichTextBox _signatureText;
    private bool _suppressIntelligence;
    private int _completionReplacementStart;
    private int _completionReplacementLength;
    private Font? _signatureBoldFont;

    public event EventHandler<CodeIntelligenceRequestEventArgs>? IntelligenceRequested;
    public event EventHandler? BreakpointsChanged;


    public CodeEditor()
    {
        BackColor = EditorChrome.Canvas;
        Dock = DockStyle.Fill;

        _editingHost = new Panel
        {
            BackColor = EditorChrome.Canvas,
        };

        _gutter = new Panel
        {
            BackColor = EditorChrome.Surface,
            Dock = DockStyle.Left,
            Width = 44,
        };
        _gutter.Paint += PaintGutter;
        _gutter.MouseDown += ToggleBreakpointAt;

        _text = new RichTextBox
        {
            AcceptsTab = true,
            BackColor = EditorChrome.Canvas,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = EditorChrome.CodeFont,
            ForeColor = EditorChrome.Text,
            HideSelection = false,
            WordWrap = false,
        };
        _text.TextChanged += (_, _) =>
        {
            if (_suppressChange)
            {
                return;
            }

            TextChangedByUser?.Invoke(this, EventArgs.Empty);
            _highlightTimer!.Stop();
            _highlightTimer.Start();
            _gutter.Invalidate();
            RefreshIntelligence();
        };
        _text.VScroll += (_, _) => _gutter.Invalidate();
        _text.FontChanged += (_, _) => _gutter.Invalidate();

        // RichTextBox undoes with Ctrl+Z on its own but has no Ctrl+Y binding, so redo in a code
        // editor did nothing at all once the shell stopped claiming the key for the document.
        _text.KeyDown += (_, args) =>
        {
            if (args.Control && args.KeyCode == Keys.Y && _text.CanRedo)
            {
                _text.Redo();
                args.Handled = true;
                args.SuppressKeyPress = true;
                return;
            }

            if (_autoCompleteBox?.Visible == true)
            {
                if (args.KeyCode == Keys.Escape)
                {
                    _autoCompleteBox.Visible = false;
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                }
                else if (args.KeyCode == Keys.Down)
                {
                    if (_autoCompleteBox.SelectedIndex < _autoCompleteBox.Items.Count - 1)
                        _autoCompleteBox.SelectedIndex++;
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                }
                else if (args.KeyCode == Keys.Up)
                {
                    if (_autoCompleteBox.SelectedIndex > 0)
                        _autoCompleteBox.SelectedIndex--;
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                }
                else if (args.KeyCode is Keys.Tab or Keys.Enter or Keys.Space)
                {
                    CommitAutoComplete();
                    args.Handled = true;
                    args.SuppressKeyPress = true;
                }
            }
        };

        _editingHost.Controls.Add(_text);
        _editingHost.Controls.Add(_gutter);
        Controls.Add(_editingHost);

        // Signature Strip setup
        _signatureStrip = new Panel
        {
            Dock = DockStyle.None,
            Height = SignatureStripLogicalHeight,
            BackColor = EditorChrome.Surface,
            Visible = false
        };
        
        _signatureText = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Muted,
            Font = EditorChrome.SmallFont,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = RichTextBoxScrollBars.None
        };
        _signatureStrip.Controls.Add(_signatureText);
        Controls.Add(_signatureStrip);

        // AutoComplete Box setup
        _autoCompleteBox = new ListBox
        {
            Visible = false,
            BackColor = EditorChrome.Surface,
            ForeColor = EditorChrome.Text,
            Font = EditorChrome.CodeFont,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            ItemHeight = 18,
            Height = 168,
            Width = 440
        };
        
        // Host the autocomplete box in the text control so it floats over it
        _text.Controls.Add(_autoCompleteBox);

        _autoCompleteBox.MouseClick += (_, _) => CommitAutoComplete();
        
        _text.SelectionChanged += OnSelectionChanged;

        _highlightTimer = new System.Windows.Forms.Timer { Interval = 260 };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            HighlightNow();
        };

        EditorChrome.Changed += OnChrome;
        Disposed += (_, _) =>
        {
            EditorChrome.Changed -= OnChrome;
            _signatureBoldFont?.Dispose();
            _highlightTimer.Dispose();
        };
    }

    /// <summary>Raised only for user/document edits — never for re-highlight passes.</summary>
    public event EventHandler? TextChangedByUser;

    public RichTextBox TextBox => _text;

    public bool CompletionVisible => _autoCompleteBox.Visible;

    public bool SignatureVisible => _signatureStrip.Visible;

    public string SignatureText => _signatureText.Text;

    public IReadOnlyList<CodeCompletionItem> CompletionItems =>
        _autoCompleteBox.Items.Cast<CodeCompletionItem>().ToArray();

    public IReadOnlyCollection<int> Breakpoints => _breakpoints;

    public int CurrentExecutionLine
    {
        get => _currentExecutionLine;
        set
        {
            _currentExecutionLine = Math.Max(0, value);
            _gutter.Invalidate();
            if (_currentExecutionLine > 0) ScrollToLine(_currentExecutionLine);
        }
    }

    public void SetBreakpoints(IEnumerable<int> oneBasedLines)
    {
        _breakpoints.Clear();
        foreach (int line in oneBasedLines.Where(line => line > 0)) _breakpoints.Add(line);
        _gutter.Invalidate();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public string CodeText
    {
        get => _text.Text;
        set
        {
            _suppressChange = true;
            _text.Text = value;
            _suppressChange = false;
            HighlightNow();
            TextChangedByUser?.Invoke(this, EventArgs.Empty);
            RefreshIntelligence();
        }
    }

    /// <summary>Move the caret and immediately refresh editor assistance.</summary>
    public void MoveCaret(int position)
    {
        _text.Select(Math.Clamp(position, 0, _text.TextLength), 0);
        RefreshIntelligence();
    }

    /// <summary>Re-evaluate completion and signature help for the current caret.</summary>
    public void RefreshIntelligence()
    {
        if (_suppressChange || _suppressIntelligence || _text.SelectionLength != 0)
        {
            return;
        }

        string source = _text.Text;
        int caret = _text.SelectionStart;

        if (CodeContextAnalyzer.TryGetActiveCall(
                source,
                caret,
                out string commandName,
                out int activeParameterIndex))
        {
            IntelligenceRequested?.Invoke(
                this,
                CodeIntelligenceRequestEventArgs.Signature(commandName, activeParameterIndex));
        }
        else
        {
            HideSignature();
        }

        if (CodeContextAnalyzer.TryGetCompletion(
                source,
                caret,
                out string prefix,
                out int replacementStart,
                out int replacementLength))
        {
            IntelligenceRequested?.Invoke(
                this,
                CodeIntelligenceRequestEventArgs.Completion(prefix, replacementStart, replacementLength));
        }
        else
        {
            HideAutoComplete();
        }
    }

    public void SetRules(IEnumerable<HighlightRule> rules)
    {
        _rules.Clear();
        _rules.AddRange(rules);
        HighlightNow();
    }

    public void InsertAtCaret(string snippet)
    {
        _text.SelectedText = snippet;
        _text.Focus();
    }

    public void HighlightNow()
    {
        if (!_text.IsHandleCreated || _text.TextLength > 160_000)
        {
            return;
        }

        string code = _text.Text;
        int selectionStart = _text.SelectionStart;
        int selectionLength = _text.SelectionLength;

        _ = SendMessage(_text.Handle, WmSetRedraw, 0, IntPtr.Zero);
        _suppressChange = true;
        try
        {
            _text.SelectAll();
            _text.SelectionColor = EditorChrome.Text;
            foreach (HighlightRule rule in _rules)
            {
                foreach (Match match in rule.Pattern.Matches(code))
                {
                    Group group = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1] : match.Groups[0];
                    _text.Select(group.Index, group.Length);
                    _text.SelectionColor = rule.Color;
                }
            }

            _text.Select(selectionStart, selectionLength);
        }
        finally
        {
            _suppressChange = false;
            _ = SendMessage(_text.Handle, WmSetRedraw, 1, IntPtr.Zero);
            _text.Invalidate();
            _gutter.Invalidate();
        }
    }

    private void PaintGutter(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(EditorChrome.Surface);
        int firstChar = _text.GetCharIndexFromPosition(new Point(2, 2));
        int firstLine = _text.GetLineFromCharIndex(firstChar);
        int lineHeight = TextRenderer.MeasureText("0", _text.Font).Height;
        if (lineHeight <= 0)
        {
            return;
        }

        int visible = _gutter.Height / lineHeight + 2;
        using SolidBrush brush = new(EditorChrome.Muted);
        for (int i = 0; i < visible; i++)
        {
            int line = firstLine + i;
            if (line >= Math.Max(1, _text.Lines.Length))
            {
                break;
            }

            int charIndex = _text.GetFirstCharIndexFromLine(line);
            if (charIndex < 0)
            {
                break;
            }

            Point position = _text.GetPositionFromCharIndex(charIndex);
            int oneBasedLine = line + 1;
            if (_breakpoints.Contains(oneBasedLine))
            {
                using SolidBrush breakpoint = new(EditorChrome.Error);
                e.Graphics.FillEllipse(breakpoint, 4, position.Y + 3, 10, 10);
            }
            if (_currentExecutionLine == oneBasedLine)
            {
                using SolidBrush current = new(EditorChrome.Warning);
                Point[] arrow =
                [
                    new Point(17, position.Y + 3),
                    new Point(27, position.Y + 8),
                    new Point(17, position.Y + 13),
                ];
                e.Graphics.FillPolygon(current, arrow);
            }
            e.Graphics.DrawString(
                oneBasedLine.ToString(),
                EditorChrome.SmallFont,
                brush,
                new RectangleF(0, position.Y, _gutter.Width - 6, lineHeight),
                new StringFormat { Alignment = StringAlignment.Far });
        }
    }

    private void ToggleBreakpointAt(object? sender, MouseEventArgs e)
    {
        int character = _text.GetCharIndexFromPosition(new Point(2, e.Y));
        int line = _text.GetLineFromCharIndex(character) + 1;
        if (line <= 0) return;
        if (!_breakpoints.Add(line)) _breakpoints.Remove(line);
        _gutter.Invalidate();
        BreakpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScrollToLine(int oneBasedLine)
    {
        int index = _text.GetFirstCharIndexFromLine(oneBasedLine - 1);
        if (index < 0) return;
        int selection = _text.SelectionStart;
        int length = _text.SelectionLength;
        _text.Select(index, 0);
        _text.ScrollToCaret();
        _text.Select(selection, length);
    }

    private void OnChrome(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        BackColor = EditorChrome.Canvas;
        _editingHost.BackColor = EditorChrome.Canvas;
        _gutter.BackColor = EditorChrome.Surface;
        _text.BackColor = EditorChrome.Canvas;
        _text.ForeColor = EditorChrome.Text;
        _text.Font = EditorChrome.CodeFont;
        
        _signatureStrip.BackColor = EditorChrome.Surface;
        _signatureText.BackColor = EditorChrome.Surface;
        _signatureText.ForeColor = EditorChrome.Muted;
        _signatureBoldFont?.Dispose();
        _signatureBoldFont = null;
        
        _autoCompleteBox.BackColor = EditorChrome.Surface;
        _autoCompleteBox.ForeColor = EditorChrome.Text;
        
        HighlightNow();
    }

    public void ShowSignature(string signature, int activeParameterIndex, string description = "")
    {
        if (string.IsNullOrEmpty(signature))
        {
            HideSignature();
            return;
        }

        _signatureStrip.Visible = true;
        _signatureText.Text = string.IsNullOrWhiteSpace(description)
            ? signature
            : signature + Environment.NewLine + description;
        _signatureText.SelectAll();
        _signatureText.SelectionColor = EditorChrome.Muted;
        _signatureText.SelectionFont = EditorChrome.SmallFont;

        if (CodeContextAnalyzer.TryGetParameterSpan(
                signature,
                activeParameterIndex,
                out int parameterStart,
                out int parameterLength))
        {
            _signatureBoldFont?.Dispose();
            _signatureBoldFont = new Font(EditorChrome.SmallFont, FontStyle.Bold);
            _signatureText.Select(parameterStart, parameterLength);
            _signatureText.SelectionColor = EditorChrome.Text;
            _signatureText.SelectionFont = _signatureBoldFont;
        }

        _signatureText.Select(0, 0);
        PerformLayout();
        _text.Focus();
    }

    public void HideSignature()
    {
        if (!_signatureStrip.Visible)
        {
            return;
        }

        _signatureStrip.Visible = false;
        PerformLayout();
    }

    public void ShowAutoComplete(
        IEnumerable<CodeCompletionItem> items,
        int replacementStart,
        int replacementLength)
    {
        _autoCompleteBox.BeginUpdate();
        _autoCompleteBox.Items.Clear();
        foreach (CodeCompletionItem item in items)
        {
            _autoCompleteBox.Items.Add(item);
        }
        _autoCompleteBox.EndUpdate();

        _completionReplacementStart = Math.Clamp(replacementStart, 0, _text.TextLength);
        _completionReplacementLength = Math.Clamp(
            replacementLength,
            0,
            _text.TextLength - _completionReplacementStart);

        if (_autoCompleteBox.Items.Count > 0)
        {
            _autoCompleteBox.SelectedIndex = 0;
            Point p = _text.GetPositionFromCharIndex(_text.SelectionStart);
            p.Y += (int)(_text.Font.Height * 1.5);
            p.X = Math.Clamp(p.X, 0, Math.Max(0, _text.ClientSize.Width - _autoCompleteBox.Width));
            p.Y = Math.Clamp(p.Y, 0, Math.Max(0, _text.ClientSize.Height - _autoCompleteBox.Height));
            _autoCompleteBox.Location = p;
            _autoCompleteBox.Visible = true;
            _autoCompleteBox.BringToFront();
        }
        else
        {
            HideAutoComplete();
        }
    }

    public void HideAutoComplete() => _autoCompleteBox.Visible = false;

    public bool CommitSelectedCompletion()
    {
        if (_autoCompleteBox.SelectedItem is not CodeCompletionItem item || !_autoCompleteBox.Visible)
        {
            return false;
        }

        _suppressIntelligence = true;
        try
        {
            _text.Select(_completionReplacementStart, _completionReplacementLength);
            _text.SelectedText = item.InsertText;
            _text.Select(_completionReplacementStart + item.InsertText.Length, 0);
            HideAutoComplete();
        }
        finally
        {
            _suppressIntelligence = false;
        }

        RefreshIntelligence();
        _text.Focus();
        return true;
    }

    private void CommitAutoComplete() => CommitSelectedCompletion();

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        RefreshIntelligence();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_signatureStrip is null)
        {
            _editingHost.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            return;
        }

        int footerHeight = _signatureStrip.Visible
            ? Math.Max(SignatureStripLogicalHeight, SignatureStripLogicalHeight * DeviceDpi / 96)
            : 0;
        _editingHost.SetBounds(0, 0, ClientSize.Width, Math.Max(0, ClientSize.Height - footerHeight));
        _signatureStrip.SetBounds(
            0,
            Math.Max(0, ClientSize.Height - footerHeight),
            ClientSize.Width,
            footerHeight);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}
