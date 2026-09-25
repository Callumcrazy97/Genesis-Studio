using System.Drawing;
using DrawingImage = System.Drawing.Image;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Inspector;

public sealed partial class AssetPickerModal
{
    private const int ThumbnailLimit = 512;
    private readonly Queue<ProjectAssetEntry> _thumbnailQueue = new();
    private readonly HashSet<string> _queuedThumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _filteredIndices = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _thumbnailAge = new();
    private readonly Dictionary<string, LinkedListNode<string>> _thumbnailAgeNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _thumbnailTimer = new() { Interval = 20 };
    private readonly CancellationTokenSource _previewLifetime = new();
    private bool _previewLoadingDisposed;
    private bool _thumbnailBusy;
    private bool _previewBusy;
    private int _thumbnailGeneration;
    private int _previewGeneration;
    private ProjectAssetEntry? _pendingPreview;

    internal int ThumbnailCount => _thumbnailIndices.Count;
    internal ListView ResultsControl => _results;
    internal Button FavouriteButton => _favourite;
    internal TreeView FolderTree => _folders;
    internal Button SelectButton => _select;
    internal PictureBox PreviewBox => _preview;

    private void InitializePreviewLoading() => _thumbnailTimer.Tick += ProcessThumbnailQueue;

    private void QueueThumbnail(ProjectAssetEntry entry)
    {
        if (_previewLoadingDisposed || _thumbnailIndices.ContainsKey(entry.Reference)
            || _queuedThumbnails.Contains(entry.Reference) || _thumbnailQueue.Count >= 256) return;
        _queuedThumbnails.Add(entry.Reference); _thumbnailQueue.Enqueue(entry);
        if (IsHandleCreated && !_thumbnailBusy) _thumbnailTimer.Start();
    }

    private void ResetThumbnailRequests()
    {
        _thumbnailGeneration++;
        _thumbnailQueue.Clear(); _queuedThumbnails.Clear();
    }

    private void TouchThumbnail(string reference)
    {
        if (!_thumbnailAgeNodes.TryGetValue(reference, out LinkedListNode<string>? node)) return;
        _thumbnailAge.Remove(node); _thumbnailAge.AddLast(node);
    }

    private async void ProcessThumbnailQueue(object? sender, EventArgs args)
    {
        _thumbnailTimer.Stop();
        if (_thumbnailBusy || _previewLoadingDisposed || !IsHandleCreated) return;
        _thumbnailBusy = true;
        CancellationToken token = _previewLifetime.Token;
        try
        {
            while (!_previewLoadingDisposed && _thumbnailQueue.TryDequeue(out ProjectAssetEntry? entry))
            {
                // Fast scrolling must not leave hundreds of offscreen requests ahead of the
                // resources currently on screen. Selected previews use their separate worker.
                if (!_filteredIndices.TryGetValue(entry.Reference, out int visibleIndex)
                    || visibleIndex >= _results.VirtualListSize
                    || !_results.ClientRectangle.IntersectsWith(_results.GetItemRect(visibleIndex)))
                {
                    _queuedThumbnails.Remove(entry.Reference);
                    continue;
                }
                int generation = _thumbnailGeneration;
                using Bitmap? loaded = await Task.Run(() => SafePreview(entry, new Size(48, 48)), token);
                if (_previewLoadingDisposed || IsDisposed) return;
                // Keep the in-flight reference marked until its decode finishes. A virtual
                // retrieval while awaiting must not enqueue a duplicate slot/LRU entry.
                if (generation != _thumbnailGeneration) continue;
                _queuedThumbnails.Remove(entry.Reference);
                if (!_filteredIndices.ContainsKey(entry.Reference)) continue;
                if (_thumbnailIndices.ContainsKey(entry.Reference)) { TouchThumbnail(entry.Reference); continue; }
                using Bitmap fallback = CreateKindThumbnail(entry.Kind);
                Bitmap image = loaded ?? fallback;
                // Create the native list before releasing input bitmaps. UI handles and controls
                // are only touched on the captured WinForms synchronization context.
                _ = _thumbnails.Handle;
                int slot;
                if (_thumbnailIndices.Count >= ThumbnailLimit && _thumbnailAge.First is { } oldest)
                {
                    slot = _thumbnailIndices[oldest.Value];
                    _thumbnailIndices.Remove(oldest.Value); _thumbnailAgeNodes.Remove(oldest.Value);
                    _thumbnailAge.RemoveFirst(); _thumbnails.Images[slot] = image;
                }
                else { slot = _thumbnails.Images.Count; _thumbnails.Images.Add(image); }
                _thumbnailIndices[entry.Reference] = slot;
                _thumbnailAgeNodes[entry.Reference] = _thumbnailAge.AddLast(entry.Reference);
                _results.Invalidate(); // Virtual retrieval only asks for the visible rows.
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _thumbnailBusy = false;
            if (!_previewLoadingDisposed && _thumbnailQueue.Count > 0) _thumbnailTimer.Start();
        }
    }

    private void QueueSelectionPreview(ProjectAssetEntry entry)
    {
        _previewGeneration++;
        _pendingPreview = entry;
        DrawingImage? old = _preview.Image; _preview.Image = null; old?.Dispose();
        _preview.Visible = false; _previewPlaceholder.Visible = true;
        if (!_previewBusy && IsHandleCreated) ProcessSelectionPreview();
    }

    private async void ProcessSelectionPreview()
    {
        if (_previewLoadingDisposed) return;
        _previewBusy = true;
        CancellationToken token = _previewLifetime.Token;
        try
        {
            // At most one preview decode per dialog. Rapid navigation replaces the pending
            // request rather than queuing hundreds of obsolete thread-pool operations.
            while (!_previewLoadingDisposed && _pendingPreview is { } entry)
            {
                _pendingPreview = null;
                int generation = _previewGeneration;
                Bitmap? image = await Task.Run(() => SafePreview(entry, new Size(320, 205)), token);
                if (_previewLoadingDisposed || IsDisposed || generation != _previewGeneration)
                { image?.Dispose(); continue; }
                DrawingImage? old = _preview.Image;
                _preview.Image = image; old?.Dispose();
                _preview.Visible = image is not null; _previewPlaceholder.Visible = image is null;
            }
        }
        catch (OperationCanceledException) { }
        finally { _previewBusy = false; }
    }

    private Bitmap? SafePreview(ProjectAssetEntry entry, Size size)
    {
        try { return AssetPickerPreviewCache.Get(_request.ProjectRoot, entry, size); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or ExternalException or OutOfMemoryException
            or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        {
            // A damaged/missing image is a missing preview, never a broken resource picker.
            return null;
        }
    }

    private void DisposePreviewLoading()
    {
        if (_previewLoadingDisposed) return;
        _previewLoadingDisposed = true;
        _previewLifetime.Cancel();
        _thumbnailTimer.Stop(); _thumbnailTimer.Dispose();
        _pendingPreview = null;
        _thumbnailQueue.Clear(); _queuedThumbnails.Clear();
        _thumbnailAge.Clear(); _thumbnailAgeNodes.Clear();
        _previewLifetime.Dispose();
    }
}
