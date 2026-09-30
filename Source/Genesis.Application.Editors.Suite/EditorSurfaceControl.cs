using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Shared.Assets;
using Genesis.Runtime.Assets;

namespace Genesis.Application.Editors.Suite;

/// <summary>
/// Contract every specialised editor exposes to the Studio shell. The shell wraps the surface
/// in a dock document; the surface never references shell types.
/// </summary>
public interface IEditorSurface
{
    Control AsControl { get; }

    string ResourcePath { get; }

    bool IsDirty { get; }

    bool CanUndo { get; }

    bool CanRedo { get; }

    event EventHandler? DirtyChanged;

    event EventHandler? AssetRefreshCompleted;

    void Save();

    void Undo();

    void Redo();

    void HandleAssetChanges(ProjectAssetChangeSet changes);
}

/// <summary>
/// Lets the resource Inspector apply a value to an already-open editor without replacing the
/// editor or discarding its unsaved work. Property paths are the stable paths shown by the
/// Inspector, for example <c>Parameters.Speed</c> or <c>Variables.moveSpeed</c>.
/// </summary>
public interface IResourceInspectorTarget
{
    bool TryApplyInspectorValue(string propertyPath, object? value);
}

/// <summary>
/// One contextual value supplied by an open editor. <c>Runtime.*</c> paths are transient play-mode
/// state; other paths may describe authored code values that the editor owns until Save.
/// </summary>
public sealed record ResourceInspectorLiveValue(
    string Group,
    string PropertyPath,
    string Label,
    object Value,
    bool ReadOnly = false,
    string? Description = null,
    IReadOnlyList<string>? Choices = null,
    ResourceKind? AssetKind = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    decimal? Increment = null,
    int? DecimalPlaces = null);

/// <summary>
/// Supplies contextual values to the production Inspector. Runtime paths mutate the retained play
/// instance; authored paths are routed through <see cref="IResourceInspectorTarget"/> so the open
/// editor retains its normal save boundary.
/// </summary>
public interface ILiveResourceInspectorTarget
{
    event EventHandler? InspectorStateChanged;

    IReadOnlyList<ResourceInspectorLiveValue> GetLiveInspectorValues();

    bool TryApplyLiveInspectorValue(string propertyPath, object? value);
}

/// <summary>
/// Base surface for the specialised editor suite: dirty tracking, a transactional
/// undo/redo journal (NEXT-007 for new editors), JSON helpers, and live chrome theming.
/// </summary>
public abstract class EditorSurfaceControl : UserControl, IEditorSurface
{
    private sealed record EditorEdit(string Label, Action Apply, Action Revert);

    private readonly Stack<EditorEdit> _undoStack = new();
    private readonly Stack<EditorEdit> _redoStack = new();
    private bool _dirty;

    protected EditorSurfaceControl(string resourcePath, string projectRoot)
    {
        ResourcePath = resourcePath ?? throw new ArgumentNullException(nameof(resourcePath));
        ProjectRoot = projectRoot ?? throw new ArgumentNullException(nameof(projectRoot));
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Font = EditorChrome.BaseFont;
        EditorChrome.Changed += OnChromeChangedInternal;
        Disposed += (_, _) => EditorChrome.Changed -= OnChromeChangedInternal;
    }

    public Control AsControl => this;

    public string ResourcePath { get; }

    public string ProjectRoot { get; }

    /// <summary>Available width in authored coordinates, including DPI and the interface-font preference.</summary>
    protected int LogicalClientWidth => (int)(ClientSize.Width / Math.Max(1f,
        DeviceDpi / 96f * EditorChrome.BaseFont.SizeInPoints / 9.5f));

    public bool IsDirty => _dirty;

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    public event EventHandler? DirtyChanged;

    public event EventHandler? AssetRefreshCompleted;

    /// <summary>
    /// Ask the Studio shell to open a linked resource (shader, model, image) in its own editor.
    /// Image Viewer vs Image Editor: inspect here, Edit opens the document.
    /// </summary>
    public event EventHandler<string>? OpenLinkedResourceRequested;

    public void RequestOpenLinkedResource(string path)
    {
        string resolved = ResourceNames.Resolve(ProjectRoot, path);
        if (resolved.Length > 0) path = resolved;
        if (!string.IsNullOrWhiteSpace(path))
        {
            OpenLinkedResourceRequested?.Invoke(this, path);
        }
    }

    /// <summary>Last dependency generation applied to this open preview.</summary>
    public long AssetRefreshGeneration { get; private set; }

    /// <summary>Latest journal label (surfaced by tests and the status bar).</summary>
    public string? LastEditLabel { get; private set; }

    public abstract void Save();

    /// <summary>
    /// Rebinds live preview state after the project graph reports a changed dependency. The shell
    /// recreates a clean surface when its own resource changed; this hook is therefore for assets
    /// consumed by the open document.
    /// </summary>
    public void HandleAssetChanges(ProjectAssetChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.Affects(ResourcePath) || changes.Changed(ResourcePath)) return;
        OnAssetDependenciesChanged(changes);
        AssetRefreshGeneration = changes.Generation;
        AssetRefreshCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected virtual void OnAssetDependenciesChanged(ProjectAssetChangeSet changes)
    {
        RuntimeAssetInvalidation.Invalidate(ProjectRoot);
        Invalidate(true);
    }

    public virtual void Undo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        EditorEdit edit = _undoStack.Pop();
        edit.Revert();
        _redoStack.Push(edit);
        LastEditLabel = "Undo " + edit.Label;
        MarkDirty();
        OnJournalChanged();
    }

    public virtual void Redo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        EditorEdit edit = _redoStack.Pop();
        edit.Apply();
        _undoStack.Push(edit);
        LastEditLabel = "Redo " + edit.Label;
        MarkDirty();
        OnJournalChanged();
    }

    /// <summary>Default number of undo steps an editor retains.</summary>
    protected const int DefaultUndoLimit = 200;

    /// <summary>
    /// Records an edit whose effect is already applied. <paramref name="revert"/> restores the
    /// prior state; <paramref name="apply"/> re-applies it for redo. History is bounded: many edits
    /// capture whole-document snapshots, and an unbounded journal grew for the whole session.
    /// </summary>
    protected void PushEdit(string label, Action apply, Action revert, int maximumEntries = DefaultUndoLimit)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _undoStack.Push(new EditorEdit(label, apply, revert));
        if (_undoStack.Count > maximumEntries)
        {
            EditorEdit[] newest = _undoStack.Take(maximumEntries).Reverse().ToArray();
            _undoStack.Clear();
            foreach (EditorEdit retained in newest) _undoStack.Push(retained);
        }
        _redoStack.Clear();
        LastEditLabel = label;
        MarkDirty();
        OnJournalChanged();
    }

    protected void MarkDirty()
    {
        if (!_dirty)
        {
            _dirty = true;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected void AcceptSave()
    {
        // A completed save makes every frame-path cache (sprites, textures, models, shaders)
        // re-validate on its next use, so previews and play reflect the edit immediately rather
        // than after their bounded fallback interval.
        Genesis.Shared.Assets.RuntimeAssetPolicy.Invalidate();
        _dirty = false;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Discard commands that refer to a replaced draft document or skeleton.</summary>
    protected void ClearEditHistory()
    {
        _undoStack.Clear(); _redoStack.Clear(); LastEditLabel = null;
        OnJournalChanged();
    }

    protected virtual void OnJournalChanged()
    {
        foreach (EditorCommandBar commandBar in Controls.OfType<EditorCommandBar>())
        {
            commandBar.RefreshDocumentState();
        }
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (e.Control is EditorCommandBar commandBar)
        {
            commandBar.BindDocument(this);
        }
    }

    /// <summary>Applies chrome colors when the Studio theme changes. Override to re-style custom panels.</summary>
    protected virtual void OnChromeChanged()
    {
        BackColor = EditorChrome.Canvas;
        ForeColor = EditorChrome.Text;
        Invalidate(true);
    }

    /// <summary>Recomputes content-sized geometry after the shell has themed the complete control tree.</summary>
    public virtual void ApplyInterfaceLayout() { }

    private void OnChromeChangedInternal(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(OnChromeChanged);
        }
        else
        {
            OnChromeChanged();
        }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────────

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    protected T LoadJsonOrDefault<T>(Func<T> fallback)
    {
        try
        {
            if (File.Exists(ResourcePath))
            {
                string json = File.ReadAllText(ResourcePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    T? value = JsonSerializer.Deserialize<T>(json, JsonOptions);
                    if (value is not null)
                    {
                        return value;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            LoadWarning = exception.Message;
        }

        return fallback();
    }

    /// <summary>Non-null when the resource content could not be parsed and a default was used.</summary>
    public string? LoadWarning { get; protected set; }

    protected void WriteResourceText(string content)
    {
        // Every suite editor saves through here, so one call covers all of them. Taken from the
        // bytes still on disk, before they are replaced.
        Genesis.Application.Core.Projects.ResourceBackupService.BackupBeforeOverwrite(ResourcePath);
        ProjectAssetWriteRegistry.MarkLocalWrite(ResourcePath);
        content = ResourceReferenceRewriter.Normalize(ProjectRoot, ResourcePath, content);
        bool catalogued = ResourceNames.IsCatalogued(ProjectRoot, ResourcePath);
        File.WriteAllText(ResourcePath, content, new UTF8Encoding(false));
        // Rewriting an already catalogued resource cannot change names or identities; invalidating
        // anyway forced a synchronous rescan (and .meta parse) of the entire project on the next
        // lookup after every save.
        if (!catalogued) ResourceNames.Invalidate(ProjectRoot);
        Genesis.Shared.Assets.RuntimeAssetPolicy.Invalidate();
    }

    protected void SaveJson<T>(T value)
    {
        WriteResourceText(JsonSerializer.Serialize(value, JsonOptions));
    }
}
