using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Genesis.Application.Core.Images;
using Genesis.Application.Editors.Image.Imaging;
using Timer = System.Windows.Forms.Timer;

namespace Genesis.Application.Studio.Controls;

public sealed class AnimatedIconPlayer : PictureBox
{
    private Timer? _timer;
    private List<Bitmap> _frames = new();
    private int _currentFrame = 0;
    private int _fps = 15;
    private string _sourcePath = string.Empty;

    public AnimatedIconPlayer()
    {
        SizeMode = PictureBoxSizeMode.Zoom;
        BackColor = Color.FromArgb(40, 44, 52);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            ClearFrames();
        }
        base.Dispose(disposing);
    }

    public void LoadIcon(string path, int fps)
    {
        Stop();
        ClearFrames();
        _sourcePath = path;
        _fps = Math.Clamp(fps, 0, 60);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Image = null;
            return;
        }

        try
        {
            if (path.EndsWith(".image.json", StringComparison.OrdinalIgnoreCase))
            {
                LoadGenesisImage(path);
            }
            else
            {
                Image = Image.FromFile(path);
            }
        }
        catch
        {
            Image = null;
        }

        if (_frames.Count > 1 && _fps > 0)
        {
            _timer = new Timer { Interval = 1000 / _fps };
            _timer.Tick += (s, e) => AdvanceFrame();
            _timer.Start();
        }
        else if (_frames.Count > 0)
        {
            Image = _frames[0];
        }
    }

    private void AdvanceFrame()
    {
        if (_frames.Count == 0) return;
        _currentFrame = (_currentFrame + 1) % _frames.Count;
        Image = _frames[_currentFrame];
    }

    private void Stop()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Dispose();
            _timer = null;
        }
    }

    private void ClearFrames()
    {
        foreach (var bmp in _frames)
        {
            bmp.Dispose();
        }
        _frames.Clear();
        _currentFrame = 0;
        
        if (Image != null)
        {
            Image.Dispose();
            Image = null;
        }
    }

    private void LoadGenesisImage(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<ImageDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (document == null) return;

            var session = new ImageDocumentSession(document, path, ImageDocumentAccess.Viewer);
            var workspace = ImageWorkspaceStorage.Load(session);

            for (int i = 0; i < workspace.Frames.Count; i++)
            {
                var composite = workspace.CompositeCurrentFrameFor(i, ImageMaterialChannel.Color);
                if (composite.Pixels != null && composite.Pixels.Length > 0)
                {
                    _frames.Add(RgbaToBitmap(composite.Pixels, composite.Width, composite.Height));
                }
            }
        }
        catch
        {
            // Ignore corrupted images
        }
    }

    private static Bitmap RgbaToBitmap(byte[] rgba, int width, int height)
    {
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        bitmap.UnlockBits(data);
        return bitmap;
    }
}
