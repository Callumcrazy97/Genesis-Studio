#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Imaging;

/// <summary>Renderer-owned dynamic texture for one live entity. Does not mutate an atlas/source image.</summary>
public sealed class PixelRigSprite : IDisposable
{
    private sealed class Upload { public TextureHandle Texture; public long Revision = -1; }
    private readonly PixelRigLayerStack? _layers;
    private ReadOnlyMemory<byte> RenderPixels => _layers?.GetPixels() ?? Player.GetPixels();
    private readonly Dictionary<IRenderController, Upload> _uploads = new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<IRenderController, List<WeakReference<PixelRigSprite>>> Owners = new();
    public PixelRigPlayer Player { get; }
    public string DescriptorPath { get; }
    public string RequestedRig { get; }
    public float OriginX { get; }
    public float OriginY { get; }
    public bool IsDisposed { get; private set; }
    public long UploadCount { get; private set; }

    public PixelRigSprite(PixelRigPlayer player, string descriptorPath, string requestedRig, float originX, float originY, PixelRigLayerStack? layers = null)
    { _layers = layers; Player = player; DescriptorPath = descriptorPath; RequestedRig = requestedRig; OriginX = originX; OriginY = originY; }

    public TextureHandle GetTexture(IRenderController renderer)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentNullException.ThrowIfNull(renderer);
        if (!_uploads.TryGetValue(renderer, out Upload? upload))
        {
            upload = new Upload();
            _uploads.Add(renderer, upload);
            List<WeakReference<PixelRigSprite>> owners = Owners.GetValue(renderer, _ => new());
            owners.RemoveAll(reference => !reference.TryGetTarget(out var target) || target.IsDisposed);
            owners.Add(new WeakReference<PixelRigSprite>(this));
        }
        if (!upload.Texture.IsValid)
        {
            upload.Texture = renderer.CreateTexture(Player.Width, Player.Height, RenderPixels.Span);
            upload.Revision = Player.Revision; UploadCount++;
        }
        else if (upload.Revision != Player.Revision)
        {
            renderer.UpdateTexture(upload.Texture, Player.Width, Player.Height, RenderPixels.Span);
            upload.Revision = Player.Revision; UploadCount++;
        }
        return upload.Texture;
    }

    public static void InvalidateRenderer(IRenderController renderer)
    {
        if (renderer is null || !Owners.TryGetValue(renderer, out var owners)) return;
        foreach (WeakReference<PixelRigSprite> reference in owners)
            if (reference.TryGetTarget(out var owner)) owner.ReleaseRenderer(renderer);
        Owners.Remove(renderer);
    }

    private void ReleaseRenderer(IRenderController renderer)
    {
        if (!_uploads.Remove(renderer, out Upload? upload)) return;
        if (upload.Texture.IsValid) renderer.ReleaseTexture(upload.Texture);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        foreach (var pair in _uploads)
            if (pair.Value.Texture.IsValid) pair.Key.ReleaseTexture(pair.Value.Texture);
        _uploads.Clear();
    }
}
