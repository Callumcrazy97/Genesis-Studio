using System.Windows.Forms;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;

namespace Genesis.Application.Studio.Docking;

public abstract class GenesisDockContent : DockContent
{
    private bool _initialDpiScaleApplied;

    protected GenesisDockContent()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = ThemeService.Palette.Surface;
        ForeColor = ThemeService.Palette.Text;
        Font = ThemeService.InterfaceFont;
        HideOnClose = true;
    }

    // Runtime-only shortcut routing is deliberately a field + setter rather than a property.
    // WinForms' designer/analyser inspects control properties for serialization semantics; this
    // callback is shell wiring and must never participate in designer serialization.
    private Func<Keys, bool>? _projectShortcutRouter;

    internal void SetProjectShortcutRouter(Func<Keys, bool>? router) =>
        _projectShortcutRouter = router;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        _projectShortcutRouter?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);

    protected override void OnLoad(EventArgs e)
    {
        if (!_initialDpiScaleApplied)
        {
            _initialDpiScaleApplied = true;
            DpiLayout.ApplyDesignBaseline(this);
        }

        base.OnLoad(e);
    }

    protected override string GetPersistString() => GetType().FullName ?? GetType().Name;
}
