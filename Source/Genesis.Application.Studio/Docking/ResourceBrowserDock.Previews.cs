using System.Windows.Forms;
using Genesis.Application.Core.Resources;
using Genesis.Application.Studio.Resources;

namespace Genesis.Application.Studio.Docking;

public sealed partial class ResourceBrowserDock
{
    // A single worker, no project-sized work queue. Each completion inspects today's viewport;
    // collapsed folders and old scroll positions never accumulate ahead of visible resources.
    private readonly System.Windows.Forms.Timer _visiblePreviewTimer = new() { Interval = 60 };
    private readonly CancellationTokenSource _previewLifetime = new();
    private bool _previewBusy;
    private bool _previewsDisposed;
    private int _previewGeneration;
    private int _previewStarts;

    internal int CachedPreviewCount => _thumbnails.Count;
    internal int PreviewStartCount => _previewStarts;
    internal bool PreviewBusy => _previewBusy;

    private void InitializeVisiblePreviews()
    {
        _visiblePreviewTimer.Tick += (_, _) => StartVisiblePreviewWork();
        HandleCreated += (_, _) => { if (!_previewsDisposed) _visiblePreviewTimer.Start(); };
        HandleDestroyed += (_, _) => { if (!_previewsDisposed) _visiblePreviewTimer.Stop(); };
        if (IsHandleCreated) _visiblePreviewTimer.Start();
    }

    private ResourceItem? NextVisiblePreview()
    {
        if (!Visible || !_tree.IsHandleCreated || _tree.ClientSize.Height == 0) return null;
        TreeNode? node = _tree.TopNode;
        // Bound UI work even with a pathological native viewport/item-height configuration.
        for (int i = 0; node is not null && i < 256; i++, node = node.NextVisibleNode)
        {
            if (node.Bounds.Top >= _tree.ClientSize.Height) break;
            if (node.Bounds.Bottom <= 0 || node.Tag is not ResourceItem item || _thumbnails.Contains(item)) continue;
            return item;
        }
        return null;
    }

    private async void StartVisiblePreviewWork()
    {
        if (_previewBusy || _previewsDisposed || IsDisposed || !IsHandleCreated) return;
        _previewBusy = true;
        CancellationToken token = _previewLifetime.Token;
        try
        {
            while (!_previewsDisposed && !IsDisposed && IsHandleCreated && NextVisiblePreview() is { } item)
            {
                int generation = _previewGeneration;
                _previewStarts++;
                using ResourceThumbnailCache.PreparedThumbnail prepared = await Task.Run(() => _thumbnails.Prepare(item), token);
                if (_previewsDisposed || IsDisposed) return;
                if (generation != _previewGeneration) continue;
                // A changed filter/deleted resource must not reappear when an old decode finishes.
                string key = ResourceBrowserProjection.Key(item);
                if (!_visibleNodes.TryGetValue(key, out TreeNode? node)) continue;
                _thumbnails.Accept(prepared);
                _tree.Invalidate(node.Bounds);
            }
        }
        catch (OperationCanceledException) { }
        finally { _previewBusy = false; }
    }

    private void ResetVisiblePreviews()
    {
        _previewGeneration++;
        _thumbnails.Invalidate();
    }

    private void DisposeVisiblePreviews()
    {
        if (_previewsDisposed) return;
        _previewsDisposed = true; _previewGeneration++;
        _visiblePreviewTimer.Stop(); _visiblePreviewTimer.Dispose();
        _previewLifetime.Cancel(); _previewLifetime.Dispose();
    }
}
