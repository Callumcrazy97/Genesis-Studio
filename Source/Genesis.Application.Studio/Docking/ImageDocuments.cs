using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Images;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Application.Editors.Image.Imaging;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Docking;

public sealed class ImageEditorRequestedEventArgs(
    ResourceItem resource,
    ImageDocumentSession session,
    ImageWorkspace workspace) : EventArgs
{
    public ResourceItem Resource { get; } = resource;
    public ImageDocumentSession Session { get; } = session;
    public ImageWorkspace Workspace { get; } = workspace;
}

public sealed class ImageViewerDocument : GenesisDockContent, IStudioDocument,
    IResourceInspectorTarget, ILiveResourceInspectorTarget
{
    private readonly StudioLog _log;
    private readonly ImageViewerControl _viewer;
    private readonly System.Windows.Forms.Timer _liveRefresh;
    private readonly ImageResourceInspectorAdapter _inspectorAdapter;
    private bool _externalConflict;
    private Func<IReadOnlyList<string>>? _listTextureGroups;

    public ImageViewerDocument(ResourceItem resource, StudioLog log)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(resource.FullPath);
        Session = new ImageDocumentSession(loaded.Document, resource.FullPath, ImageDocumentAccess.Editor);
        _viewer = new ImageViewerControl(Session) { Dock = DockStyle.Fill };
        _viewer.EditRequested += OnEditRequested;
        _viewer.DirtyChanged += (_, _) => UpdateCaption();
        _inspectorAdapter = new ImageResourceInspectorAdapter(
            Session, Workspace, _viewer.RefreshFromDocument, () => _listTextureGroups?.Invoke());
        Session.Changed += OnSessionChanged;
        Controls.Add(_viewer);

        // Live viewer↔editor sync: the Image Editor window shares this workspace, so any
        // Touch() there refreshes the viewer immediately (coalesced) instead of waiting
        // for the tab to be re-activated.
        _liveRefresh = new System.Windows.Forms.Timer { Interval = 120 };
        _liveRefresh.Tick += (_, _) =>
        {
            _liveRefresh.Stop();
            if (!IsDisposed)
            {
                _viewer.RefreshFromDocument();
            }
        };
        Workspace.Changed += OnWorkspaceChanged;
        Text = resource.Name;
        TabText = resource.Name;
        ToolTipText = resource.Name;
        DockAreas = DockAreas.Document | DockAreas.Float;
        ShowHint = DockState.Document;
        HideOnClose = false;
        ThemeService.Apply(this);
        UpdateCaption();
    }

    public ResourceItem Resource { get; }
    public ImageDocumentSession Session { get; }
    public ImageWorkspace Workspace => _viewer.Workspace;
    public ImageViewerControl Viewer => _viewer;
    public string DocumentIdentity => Identity(Resource);
    public bool IsDirty => _viewer.IsDirty;
    public event EventHandler<ImageEditorRequestedEventArgs>? EditorRequested;
    public event EventHandler? InspectorStateChanged;

    /// <summary>Wires project Texture Group authoring into the Viewer and Inspector.</summary>
    public void BindTextureGroups(
        Func<IReadOnlyList<string>> listNames,
        Func<string, int, string?> tryAdd,
        Func<string, string?> tryDelete)
    {
        _listTextureGroups = listNames ?? throw new ArgumentNullException(nameof(listNames));
        _viewer.ListTextureGroups = listNames;
        _viewer.TryAddTextureGroup = tryAdd;
        _viewer.TryDeleteTextureGroup = tryDelete;
        _viewer.RefreshFromDocument();
    }

    public IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues() =>
        _inspectorAdapter.GetValues();

    public bool TryApplyLiveInspectorValue(string propertyPath, object? value) =>
        _inspectorAdapter.TryApply(propertyPath, value);

    public bool TryApplyInspectorValue(string propertyPath, object? value) =>
        _inspectorAdapter.TryApply(propertyPath, value);

    public bool CanExecute(StudioDocumentCommand command) =>
        command switch
        {
            StudioDocumentCommand.Save => IsDirty,
            StudioDocumentCommand.Undo => Session.History.CanUndo,
            StudioDocumentCommand.Redo => Session.History.CanRedo,
            _ => false,
        };

    public void Execute(StudioDocumentCommand command)
    {
        switch (command)
        {
            case StudioDocumentCommand.Save: Save(); break;
            case StudioDocumentCommand.Undo: _viewer.Undo(); break;
            case StudioDocumentCommand.Redo: _viewer.Redo(); break;
        }
        UpdateCaption();
    }

    public void Save()
    {
        _viewer.Save();
        MarkLocalImageWrite();
        _log.Information("Image Viewer", $"Saved '{Resource.Name}'.");
        UpdateCaption();
    }

    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        if (!changes.Affects(Resource.FullPath)) return;
        Resource.Name = ResourceDisplayName.Format(Resource.FullPath);
        UpdateCaption();
        if (changes.IsLocalWrite(Resource.FullPath)) return;
        if (IsDirty)
        {
            _externalConflict = true;
            _log.Warning("Image Editor", $"'{Resource.Name}' changed on disk; unsaved work was preserved.");
            UpdateCaption();
            return;
        }

        ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(Resource.FullPath);
        Session.ReloadFrom(loaded.Document);
        Workspace.ReplaceWith(ImageWorkspaceStorage.Load(Session));
        _viewer.RefreshFromDocument();
        _externalConflict = false;
        UpdateCaption();
        _log.Information("Image Editor", $"Live-reloaded '{Resource.Name}'.");
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _viewer.RefreshFromDocument();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!ConfirmClose()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Workspace.Changed -= OnWorkspaceChanged;
            Session.Changed -= OnSessionChanged;
            _liveRefresh.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        // Coalesce bursts of per-pixel edits into one refresh per timer tick.
        _liveRefresh.Stop();
        _liveRefresh.Start();
    }

    private void OnSessionChanged(object? sender, EventArgs e) =>
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);

    private void OnEditRequested(object? sender, ImageEditRequestedEventArgs e) =>
        EditorRequested?.Invoke(
            this,
            new ImageEditorRequestedEventArgs(Resource, e.Session, e.Workspace));

    private bool ConfirmClose()
    {
        if (!IsDirty) return true;

        if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
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
        Text = Resource.Name;
        ToolTipText = Resource.Name;
        TabText = _externalConflict ? Resource.Name + " ⚠" : IsDirty ? Resource.Name + " *" : Resource.Name;
    }

    private void MarkLocalImageWrite()
    {
        ProjectAssetWriteRegistry.MarkLocalWrite(Resource.FullPath);
        string data = ResourceAssociates.GetSpriteDataDirectory(Resource.FullPath);
        if (!Directory.Exists(data)) return;
        foreach (string path in Directory.EnumerateFiles(data, "*", SearchOption.AllDirectories))
            ProjectAssetWriteRegistry.MarkLocalWrite(path);
    }

    internal static string Identity(ResourceItem resource) =>
        resource.AssetId != Guid.Empty
            ? resource.AssetId.ToString("N")
            : Path.GetFullPath(resource.FullPath);
}

public sealed class ImageEditorDocument : GenesisDockContent, IStudioDocument
{
    private readonly StudioLog _log;
    private readonly ImageEditorControl _editor;

    public ImageEditorDocument(
        ResourceItem resource,
        StudioLog log,
        ImageDocumentSession session,
        ImageWorkspace workspace)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _editor = new ImageEditorControl(session, workspace) { Dock = DockStyle.Fill };
        _editor.DirtyChanged += (_, _) => UpdateCaption();
        Controls.Add(_editor);
        Text = resource.Name + " — Image Editor";
        TabText = Text;
        ToolTipText = resource.Name;
        DockAreas = DockAreas.Document | DockAreas.Float;
        ShowHint = DockState.Document;
        HideOnClose = false;
        ThemeService.Apply(this);
        UpdateCaption();
    }

    public ResourceItem Resource { get; }
    public ImageDocumentSession Session { get; }
    public string DocumentIdentity => ImageViewerDocument.Identity(Resource) + ":editor";
    public bool IsDirty => _editor.IsDirty;

    public bool CanExecute(StudioDocumentCommand command) =>
        command switch
        {
            StudioDocumentCommand.Save => IsDirty,
            StudioDocumentCommand.Undo => Session.History.CanUndo,
            StudioDocumentCommand.Redo => Session.History.CanRedo,
            StudioDocumentCommand.SelectAll => true,
            StudioDocumentCommand.Copy => _editor.CanCutOrCopy,
            StudioDocumentCommand.Cut => _editor.CanCutOrCopy,
            _ => false,
        };

    public void Execute(StudioDocumentCommand command)
    {
        switch (command)
        {
            case StudioDocumentCommand.Save: Save(); break;
            case StudioDocumentCommand.Undo: _editor.Undo(); break;
            case StudioDocumentCommand.Redo: _editor.Redo(); break;
            case StudioDocumentCommand.SelectAll: _editor.SelectAll(); break;
        }
        UpdateCaption();
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
        if (!changes.Affects(Resource.FullPath)) return;
        Resource.Name = ResourceDisplayName.Format(Resource.FullPath);
        UpdateCaption();
        if (IsDirty) return;
        if (changes.IsLocalWrite(Resource.FullPath)) return;
        ImageDocumentLoadResult loaded = ImageDocumentSerializer.LoadAtomic(Resource.FullPath);
        Session.ReloadFrom(loaded.Document);
        _editor.Workspace.ReplaceWith(ImageWorkspaceStorage.Load(Session));
        _editor.RefreshFromDocument();
        UpdateCaption();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsDirty)
        {
            if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
            {
                Save();
                base.OnFormClosing(e);
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                $"Save changes to {Resource.Name}?",
                "Unsaved Image",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Warning);
            if (result == DialogResult.Cancel)
            {
                e.Cancel = true;
            }
            else if (result == DialogResult.Yes)
            {
                Save();
            }
        }
        base.OnFormClosing(e);
    }

    private void UpdateCaption()
    {
        string baseText = Resource.Name + " — Image Editor";
        Text = baseText;
        ToolTipText = Resource.Name;
        TabText = IsDirty ? baseText + " *" : baseText;
    }
}
