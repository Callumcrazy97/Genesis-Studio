using System.Drawing;
using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Docking;

public sealed class ConsoleDock : GenesisDockContent
{
    private readonly StudioLog _log;
    private readonly RichTextBox _output;
    private readonly ToolStripLabel _count;

    public ConsoleDock(StudioLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Text = "Console";
        TabText = "Console";
        DockAreas = DockAreas.DockBottom | DockAreas.Float;
        ShowHint = DockState.DockBottom;

        ToolStrip toolbar = new()
        {
            BackColor = ThemeService.Palette.Surface,
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = ThemeService.CreateToolStripRenderer(),
            Tag = ThemeService.DenseToolStripTag,
        };
        toolbar.Items.Add("Clear", null, (_, _) => Clear());
        toolbar.Items.Add("Copy all", null, (_, _) => CopyAll());
        toolbar.Items.Add(new ToolStripSeparator());
        _count = new ToolStripLabel("0 messages");
        toolbar.Items.Add(_count);
        Controls.Add(toolbar);

        _output = new RichTextBox
        {
            BackColor = ThemeService.Palette.Canvas,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            Dock = DockStyle.Fill,
            Font = ThemeService.CodeFont,
            ForeColor = ThemeService.Palette.TextMuted,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Tag = ThemeService.BorderlessCanvasTextTag,
            WordWrap = true,
        };
        Controls.Add(_output);
        _output.BringToFront();

        foreach (StudioLogEntry entry in _log.Entries)
        {
            Append(entry);
        }

        _log.EntryAdded += LogEntryAdded;
        ThemeService.Apply(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _log.EntryAdded -= LogEntryAdded;
        }

        base.Dispose(disposing);
    }

    private void LogEntryAdded(object? sender, StudioLogEntry entry)
    {
        if (IsDisposed || _output.IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(() => Append(entry));
            }
            catch (ObjectDisposedException)
            {
            }

            return;
        }

        Append(entry);
    }

    private void Append(StudioLogEntry entry)
    {
        if (IsDisposed || _output.IsDisposed)
        {
            return;
        }

        Color color = entry.Level switch
        {
            StudioLogLevel.Error => ThemeService.Palette.Error,
            StudioLogLevel.Warning => ThemeService.Palette.Warning,
            _ => ThemeService.Palette.TextMuted,
        };

        _output.SelectionStart = _output.TextLength;
        _output.SelectionColor = color;
        _output.AppendText(
            $"[{entry.TimestampUtc.ToLocalTime():HH:mm:ss}] " +
            $"{entry.Level,-11} {entry.Source}: {entry.Message}{Environment.NewLine}");
        _output.SelectionColor = ThemeService.Palette.TextMuted;
        _output.ScrollToCaret();
        _count.Text = $"{_log.Entries.Count} messages";
    }

    private void Clear()
    {
        _output.Clear();
        _count.Text = "0 messages";
    }

    private void CopyAll()
    {
        if (!string.IsNullOrWhiteSpace(_output.Text))
        {
            Clipboard.SetText(_output.Text);
        }
    }
}
