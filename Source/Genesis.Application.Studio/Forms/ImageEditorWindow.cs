using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Studio.Docking;
using Genesis.Application.Studio.Theme;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Forms;

public sealed class ImageEditorWindow : DpiAwareForm, IStudioDocument
{
    private readonly StudioLog _log;
    private readonly ImageEditorControl _editor;

    public ImageEditorWindow(
        StudioLog log,
        ResourceItem resource,
        ImageDocumentSession session,
        ImageWorkspace workspace)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _editor = new ImageEditorControl(session, workspace) { Dock = DockStyle.Fill };
        _editor.DirtyChanged += (_, _) => UpdateCaption();

        Text = resource.Name + " — Image Editor";
        ClientSize = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimumSize = new Size(960, 640);
        Controls.Add(_editor);
        ThemeService.Apply(this);
        UpdateCaption();
    }

    public ResourceItem Resource { get; }
    public ImageDocumentSession Session { get; }
    public ImageEditorControl Editor => _editor;
    public string DocumentIdentity => ImageViewerDocument.Identity(Resource) + ":editor";
    public bool IsDirty => _editor.IsDirty;

    private Func<Keys, bool>? _projectShortcutRouter;

    internal void SetProjectShortcutRouter(Func<Keys, bool>? router) =>
        _projectShortcutRouter = router;

    public bool CanExecute(StudioDocumentCommand command) => command switch
    {
        StudioDocumentCommand.Save => true,
        StudioDocumentCommand.Undo => _editor.CanEdit(Core.Editing.EditCommand.Undo),
        StudioDocumentCommand.Redo => _editor.CanEdit(Core.Editing.EditCommand.Redo),
        _ => false,
    };

    public void Execute(StudioDocumentCommand command)
    {
        if (!CanExecute(command)) return;
        switch (command)
        {
            case StudioDocumentCommand.Save: Save(); break;
            case StudioDocumentCommand.Undo: _editor.TryEdit(Core.Editing.EditCommand.Undo); break;
            case StudioDocumentCommand.Redo: _editor.TryEdit(Core.Editing.EditCommand.Redo); break;
        }
    }

    public void Save()
    {
        _editor.Save();
        ProjectAssetWriteRegistry.MarkLocalWrite(Resource.FullPath);
        _log.Information("Image Editor", $"Saved '{Resource.Name}'.");
        UpdateCaption();
    }

    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        if (!changes.Affects(Resource.FullPath) || IsDirty) return;
        if (changes.IsLocalWrite(Resource.FullPath)) return;
        ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(Resource.FullPath);
        Session.ReloadFrom(loaded.Document);
        _editor.Workspace.ReplaceWith(ImageWorkspaceStorage.Load(Session));
        _editor.RefreshFromDocument();
        UpdateCaption();
    }

    /// <summary>
    /// The Image Editor is its own window, so it does its own focus-aware routing.
    /// </summary>
    /// <remarks>
    /// The shell's routing cannot help here — it only sees keys sent to the shell — so without this
    /// Ctrl+Z in the Image Editor did nothing at all. Text fields still keep their own editing:
    /// the router hands those keys straight back.
    /// </remarks>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_projectShortcutRouter?.Invoke(keyData) == true) return true;
        if (keyData == (Keys.Control | Keys.S))
        {
            Save();
            return true;
        }

        if (Editing.EditCommandRouter.Map(keyData) is { } command
            && !Editing.EditCommandRouter.IsTextEntry(Editing.EditCommandRouter.FocusedControl())
            && _editor.CanEdit(command)
            && _editor.TryEdit(command))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmClose())
            e.Cancel = true;
        base.OnFormClosing(e);
    }

    private bool ConfirmClose()
    {
        if (!IsDirty) return true;

        if (Core.Diagnostics.UnattendedSession.IsActive)
        {
            Save();
            return true;
        }

        DialogResult result = MessageBox.Show(
            this,
            $"Save changes to {Resource.Name}?",
            "Unsaved Image",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) Save();
        return true;
    }

    private void UpdateCaption()
    {
        string baseText = Resource.Name + " — Image Editor";
        Text = IsDirty ? baseText + " *" : baseText;
    }
}
