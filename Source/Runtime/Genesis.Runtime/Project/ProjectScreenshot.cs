using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project
{
    /// <summary>Captures the current backbuffer to a PNG under the project's debug images folder.</summary>
    public static class ProjectScreenshot
    {
        public static string Capture(IRenderController renderer, string projectPath, string name, bool frameAlreadySubmitted = false)
        {
            if (renderer == null) return null;
            bool readOk;
            int w, h;
            byte[] bgra;
            readOk = frameAlreadySubmitted
                ? renderer.TryReadSubmittedFramePixels(out w, out h, out bgra)
                : renderer.TryReadFramePixels(out w, out h, out bgra);

            if (!readOk || bgra == null) return null;

            try
            {
                ProjectPaths.EnsureDebugDirs(projectPath);
                string fileName = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name : name + ".png";
                string path = Path.Combine(ProjectPaths.ImagesDir(projectPath), fileName);
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                if (data.Stride == w * 4)
                {
                    Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
                }
                else
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(bgra, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
                }
                bmp.UnlockBits(data);
                bmp.Save(path, ImageFormat.Png);
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
