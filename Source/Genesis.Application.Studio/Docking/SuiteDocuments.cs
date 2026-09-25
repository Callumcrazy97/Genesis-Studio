using System.Windows.Forms;
using Genesis.Application.Core.Diagnostics;
using Genesis.Application.Core.Resources;
using Genesis.Application.Editors.Image;
using Genesis.Application.Editors.Suite;
using Genesis.Application.Studio.Theme;
using WeifenLuo.WinFormsUI.Docking;
using Genesis.Shared.Assets;

namespace Genesis.Application.Studio.Docking;

/// <summary>
/// Dock document wrapper for every specialised editor surface in the suite (Room, Terrain,
/// Object, PGSL, TileSet, Audio, Shader, Model, Material, Particle, Physics, UI, Note,
/// Voxel Palette). The surface owns editing; this wrapper owns docking, captions, save
/// prompts, and the Studio command routing contract.
/// </summary>
public sealed class SuiteEditorDocument : GenesisDockContent, IStudioDocument
{
    private readonly StudioLog _log;
    private IEditorSurface _surface;
    private readonly string _editorTitle;
    private readonly Func<IEditorSurface>? _surfaceFactory;
    private bool _externalConflict;
    private ILiveResourceInspectorTarget? _liveInspectorTarget;

    public SuiteEditorDocument(
        ResourceItem resource,
        StudioLog log,
        IEditorSurface surface,
        string editorTitle,
        Func<IEditorSurface>? surfaceFactory = null)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _editorTitle = editorTitle;
        _surfaceFactory = surfaceFactory;

        AttachSurface(surface);
        string displayName = ResourceDisplayName.Format(resource.Name);
        Text = displayName;
        TabText = displayName;
        ToolTipText = $"{resource.Name} — {editorTitle}";
        DockAreas = DockAreas.Document | DockAreas.Float;
        ShowHint = DockState.Document;
        HideOnClose = false;
        if (surface is EditorSurfaceControl { LoadWarning: { } warning })
        {
            _log.Warning(editorTitle, $"'{resource.Name}' loaded with a fallback document: {warning}");
        }

        UpdateCaption();
    }

    public ResourceItem Resource { get; }

    public IEditorSurface Surface => _surface;

    public string DocumentIdentity =>
        Resource.AssetId != Guid.Empty
            ? Resource.AssetId.ToString("N")
            : Path.GetFullPath(Resource.FullPath);

    public bool IsDirty => _surface.IsDirty;

    public bool HasExternalConflict => _externalConflict;

    public event EventHandler? InspectorStateChanged;

    public bool CanExecute(StudioDocumentCommand command) => command switch
    {
        // Save is always available, not just when the dirty flag happens to be set — a
        // resaved-but-clean document is a harmless no-op, and gating on IsDirty was reported
        // as Save silently reading "not available" (e.g. right after a fresh sculpt/paint
        // stroke whose PushEdit hadn't yet round-tripped through DirtyChanged).
        StudioDocumentCommand.Save => true,
        StudioDocumentCommand.Undo => _surface.CanUndo,
        StudioDocumentCommand.Redo => _surface.CanRedo,
        _ => false,
    };

    public void Execute(StudioDocumentCommand command)
    {
        switch (command)
        {
            case StudioDocumentCommand.Save:
                Save();
                break;
            case StudioDocumentCommand.Undo:
                _surface.Undo();
                break;
            case StudioDocumentCommand.Redo:
                _surface.Redo();
                break;
        }

        UpdateCaption();
    }

    public void Save()
    {
        _surface.Save();
        ProjectAssetWriteRegistry.MarkLocalWrite(Resource.FullPath);
        _externalConflict = false;
        _log.Information(_editorTitle, $"Saved '{Resource.Name}'.");
        UpdateCaption();
    }

    public void ReloadSurface()
    {
        if (_surfaceFactory is null || IsDirty)
        {
            return;
        }

        ReplaceSurface(_surfaceFactory());
    }

    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        if (!changes.Affects(Resource.FullPath)) return;
        Resource.Name = ResourceDisplayName.Format(Resource.FullPath);
        UpdateCaption();
        if (changes.Changed(Resource.FullPath))
        {
            if (changes.IsLocalWrite(Resource.FullPath)) return;
            if (IsDirty)
            {
                _externalConflict = true;
                _log.Warning(_editorTitle,
                    $"'{Resource.Name}' changed on disk; unsaved editor work was preserved.");
                UpdateCaption();
                return;
            }

            if (_surfaceFactory is not null)
            {
                ReplaceSurface(_surfaceFactory());
                _log.Information(_editorTitle, $"Reloaded externally changed '{Resource.Name}'.");
            }
            return;
        }

        _surface.HandleAssetChanges(changes);
        _log.Information(_editorTitle,
            $"Refreshed preview for '{Resource.Name}' from {changes.ChangedPaths.Count} changed asset(s).");
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S))
        {
            Save();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (IsDirty)
        {
            // Nobody to ask in an automated run, and a dialog nobody can answer is a hung build.
            if (Genesis.Application.Core.Diagnostics.UnattendedSession.IsActive)
            {
                Save();
                base.OnFormClosing(e);
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                $"Save changes to {ResourceDisplayName.Format(Resource.Name)}?",
                "Unsaved " + _editorTitle,
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

    private void AttachSurface(IEditorSurface surface)
    {
        Control control = surface.AsControl;
        control.Dock = DockStyle.Fill;
        Controls.Add(control);
        surface.DirtyChanged += SurfaceDirtyChanged;
        if (surface is EditorSurfaceControl editor)
        {
            editor.OpenLinkedResourceRequested += SurfaceOpenLinkedResource;
        }
        if (surface is ILiveResourceInspectorTarget live)
        {
            _liveInspectorTarget = live;
            live.InspectorStateChanged += SurfaceInspectorStateChanged;
        }
    }

    private void ReplaceSurface(IEditorSurface replacement)
    {
        IEditorSurface previous = _surface;
        previous.DirtyChanged -= SurfaceDirtyChanged;
        if (previous is EditorSurfaceControl previousEditor)
        {
            previousEditor.OpenLinkedResourceRequested -= SurfaceOpenLinkedResource;
        }
        if (_liveInspectorTarget is not null)
        {
            _liveInspectorTarget.InspectorStateChanged -= SurfaceInspectorStateChanged;
            _liveInspectorTarget = null;
        }
        Controls.Remove(previous.AsControl);
        _surface = replacement;
        AttachSurface(replacement);
        previous.AsControl.Dispose();
        _externalConflict = false;
        UpdateCaption();
    }

    public event EventHandler<string>? OpenLinkedResourceRequested;

    private void SurfaceOpenLinkedResource(object? sender, string path) =>
        OpenLinkedResourceRequested?.Invoke(this, path);

    private void SurfaceDirtyChanged(object? sender, EventArgs e) => UpdateCaption();

    private void SurfaceInspectorStateChanged(object? sender, EventArgs e) =>
        InspectorStateChanged?.Invoke(this, EventArgs.Empty);

    private void UpdateCaption()
    {
        string name = ResourceDisplayName.Format(Resource.Name);
        ToolTipText = name + " — " + _editorTitle;
        Text = name;
        TabText = _externalConflict
            ? name + " ⚠"
            : IsDirty ? name + " •" : name;
    }
}

/// <summary>Pushes the live Studio palette/fonts into Suite and Image editor chrome bridges.</summary>
public static class SuiteChromeBridge
{
    public static void Push()
    {
        ThemePalette palette = ThemeService.Palette;
        EditorChrome.Update(
            palette.Canvas,
            palette.Surface,
            palette.SurfaceRaised,
            palette.SurfaceHover,
            palette.Border,
            palette.Text,
            palette.TextMuted,
            palette.Accent,
            palette.Success,
            palette.Warning,
            palette.Error,
            palette.IsDark,
            ThemeService.InterfaceFont,
            ThemeService.CodeFont);

        ImageEditorChrome.Update(
            palette.Canvas,
            palette.Surface,
            palette.SurfaceRaised,
            palette.SurfaceHover,
            palette.Border,
            palette.Text,
            palette.TextMuted,
            palette.Accent,
            palette.Success,
            palette.Warning,
            palette.Error,
            palette.IsDark,
            ThemeService.InterfaceFont,
            ThemeService.CodeFont);
    }
}
