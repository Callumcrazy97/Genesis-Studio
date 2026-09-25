using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Theme;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Forms;

/// <summary>
/// Dedicated model composer window. The docked Model Viewer stays a properties-and-preview
/// surface; kitbash, generation and gizmos open here from Edit Model.
/// </summary>
public sealed class ModelComposerWindow : DpiAwareForm, IStudioDocument
{
    private readonly StudioLog _log;
    private readonly ModelEditorControl _editor;

    public ModelComposerWindow(StudioLog log, ResourceItem resource, string projectRoot)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _editor = new ModelEditorControl(resource.FullPath, projectRoot, ModelEditorRole.Composer)
        {
            Dock = DockStyle.Fill,
        };
        _editor.DirtyChanged += (_, _) => UpdateCaption();

        Text = resource.Name + " — Model Editor";
        ClientSize = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimumSize = new Size(960, 640);
        Controls.Add(_editor);
        ThemeService.Apply(this);
        UpdateCaption();
    }

    public ResourceItem Resource { get; }

    public ModelEditorControl Editor => _editor;

    public string DocumentIdentity => SuiteEditorDocumentIdentity(Resource) + ":composer";

    public bool IsDirty => _editor.IsDirty;

    public event EventHandler? Saved;

    private Func<Keys, bool>? _projectShortcutRouter;

    internal void SetProjectShortcutRouter(Func<Keys, bool>? router) =>
        _projectShortcutRouter = router;

    public bool CanExecute(StudioDocumentCommand command) => command switch
    {
        StudioDocumentCommand.Save => true,
        StudioDocumentCommand.Undo => _editor.CanUndo,
        StudioDocumentCommand.Redo => _editor.CanRedo,
        _ => false,
    };

    public void Execute(StudioDocumentCommand command)
    {
        if (!CanExecute(command)) return;
        switch (command)
        {
            case StudioDocumentCommand.Save: Save(); break;
            case StudioDocumentCommand.Undo: _editor.Undo(); break;
            case StudioDocumentCommand.Redo: _editor.Redo(); break;
        }
    }

    public void Save()
    {
        _editor.Save();
        ProjectAssetWriteRegistry.MarkLocalWrite(Resource.FullPath);
        _log.Information("Model Composer", $"Saved '{Resource.Name}'.");
        UpdateCaption();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        if (!changes.Affects(Resource.FullPath) || IsDirty) return;
        if (changes.IsLocalWrite(Resource.FullPath)) return;
        _editor.HandleAssetChanges(changes);
        UpdateCaption();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_projectShortcutRouter?.Invoke(keyData) == true) return true;
        if (keyData == (Keys.Control | Keys.S))
        {
            Save();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Z) && _editor.CanUndo
            && !Editing.EditCommandRouter.IsTextEntry(Editing.EditCommandRouter.FocusedControl()))
        {
            _editor.Undo();
            return true;
        }

        if ((keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) && _editor.CanRedo
            && !Editing.EditCommandRouter.IsTextEntry(Editing.EditCommandRouter.FocusedControl()))
        {
            _editor.Redo();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmClose())
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    private bool ConfirmClose()
    {
        if (!IsDirty) return true;

        if (UnattendedSession.IsActive)
        {
            Save();
            return true;
        }

        DialogResult result = MessageBox.Show(
            this,
            $"Save changes to {Resource.Name}?",
            "Unsaved Model",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) Save();
        return true;
    }

    private void UpdateCaption()
    {
        string baseText = Resource.Name + " — Model Editor";
        Text = IsDirty ? baseText + " *" : baseText;
    }

    private static string SuiteEditorDocumentIdentity(ResourceItem resource) =>
        resource.AssetId != Guid.Empty
            ? resource.AssetId.ToString("N")
            : Path.GetFullPath(resource.FullPath);
}
