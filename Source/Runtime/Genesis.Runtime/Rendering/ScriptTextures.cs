using System;
using System.Buffers;
using System.Collections.Generic;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Pictures a script paints at run time (PGSL TextureCreate): a map, a chart, a painted sign.
    /// The pixels are kept here as RGBA bytes (red, green, blue, alpha, rows from the top); when one
    /// is drawn after a change, only the rectangle that changed goes to the GPU, once, however many
    /// times it is drawn that frame. TextureDestroy frees both copies; a new game frees them all, and
    /// the renderer's own shutdown frees every GPU copy with it.
    /// </summary>
    public static class ScriptTextures
    {
        /// <summary>Widest and tallest a texture may be, in pixels.</summary>
        public const int MaxSize = 4096;

        /// <summary>Pixels all of a game's textures may hold together: 64 million, 256 MB as RGBA.</summary>
        public const long MaxTotalPixels = 64L * 1024 * 1024;

        private sealed class Picture
        {
            public int Width, Height;
            public byte[] Pixels;
            public bool Smooth;
            public TextureHandle Handle = TextureHandle.Invalid;
            public IRenderController Owner;
            // The rectangle changed since the GPU copy was made (empty when none).
            public int DirtyLeft, DirtyTop, DirtyRight, DirtyBottom;

            public bool Dirty => DirtyRight > DirtyLeft && DirtyBottom > DirtyTop;

            public void MarkDirty(int left, int top, int right, int bottom)
            {
                if (!Dirty)
                {
                    (DirtyLeft, DirtyTop, DirtyRight, DirtyBottom) = (left, top, right, bottom);
                    return;
                }
                DirtyLeft = Math.Min(DirtyLeft, left);
                DirtyTop = Math.Min(DirtyTop, top);
                DirtyRight = Math.Max(DirtyRight, right);
                DirtyBottom = Math.Max(DirtyBottom, bottom);
            }

            public void Clean() => DirtyLeft = DirtyTop = DirtyRight = DirtyBottom = 0;
        }

        private static readonly Dictionary<int, Picture> Pictures = new();
        private static int _next;
        private static long _totalPixels;
        private static bool _uploadFailureLogged;

        /// <summary>A new texture, all transparent; 0 when the size is not allowed or the game holds too many pixels.</summary>
        public static int Create(int width, int height)
        {
            if (width < 1 || height < 1 || width > MaxSize || height > MaxSize) return 0;
            long pixels = (long)width * height;
            lock (Pictures)
            {
                if (_totalPixels + pixels > MaxTotalPixels) return 0;
                int id = ++_next;
                Picture picture = new() { Width = width, Height = height, Pixels = new byte[pixels * 4] };
                picture.MarkDirty(0, 0, width, height);
                Pictures[id] = picture;
                _totalPixels += pixels;
                return id;
            }
        }

        public static bool Exists(int id) { lock (Pictures) return Pictures.ContainsKey(id); }

        public static int Width(int id) => TryGet(id, out Picture picture) ? picture.Width : 0;

        public static int Height(int id) => TryGet(id, out Picture picture) ? picture.Height : 0;

        /// <summary>Textures alive now (for checks).</summary>
        public static int Count { get { lock (Pictures) return Pictures.Count; } }

        public static void SetSmooth(int id, bool smooth)
        {
            if (TryGet(id, out Picture picture)) picture.Smooth = smooth;
        }

        public static void Destroy(int id)
        {
            Picture picture;
            lock (Pictures)
            {
                if (!Pictures.Remove(id, out picture)) return;
                _totalPixels -= (long)picture.Width * picture.Height;
            }
            Release(picture);
        }

        /// <summary>Forgets every texture (a new game); the GPU copies go with them.</summary>
        public static void Reset()
        {
            List<Picture> all;
            lock (Pictures)
            {
                all = new List<Picture>(Pictures.Values);
                Pictures.Clear();
                _totalPixels = 0;
            }
            foreach (Picture picture in all) Release(picture);
        }

        /// <summary>
        /// The pixels of a texture to change a rectangle of, which is marked to go to the GPU again;
        /// null when there is no such texture or the rectangle is empty. The caller writes only inside
        /// the rectangle, four bytes a pixel, rows <paramref name="width"/> pixels apart.
        /// </summary>
        internal static byte[] BeginWrite(int id, int left, int top, int right, int bottom, out int width, out int height)
        {
            width = height = 0;
            if (!TryGet(id, out Picture picture)) return null;
            width = picture.Width;
            height = picture.Height;
            left = Math.Clamp(left, 0, picture.Width); right = Math.Clamp(right, left, picture.Width);
            top = Math.Clamp(top, 0, picture.Height); bottom = Math.Clamp(bottom, top, picture.Height);
            if (right <= left || bottom <= top) return null;
            picture.MarkDirty(left, top, right, bottom);
            return picture.Pixels;
        }

        /// <summary>
        /// The renderer's texture for a script texture, made or brought up to date first when it
        /// changed since it was last drawn; Invalid when there is no such texture.
        /// </summary>
        public static TextureHandle Resolve(int id, IRenderController renderer, out bool smooth)
        {
            smooth = false;
            if (renderer == null || !TryGet(id, out Picture picture)) return TextureHandle.Invalid;
            smooth = picture.Smooth;
            try
            {
                if (!picture.Handle.IsValid || !ReferenceEquals(picture.Owner, renderer) || !renderer.IsTextureLive(picture.Handle))
                {
                    // Made anew for this renderer (the first draw, another renderer, or a device that
                    // dropped its textures): the old copy goes back to whichever renderer made it.
                    Release(picture);
                    picture.Handle = renderer.CreateTexture(picture.Width, picture.Height, picture.Pixels);
                    picture.Owner = renderer;
                    picture.Clean();
                    return picture.Handle;
                }
                if (picture.Dirty) Upload(picture, renderer);
            }
            catch (Exception ex)
            {
                // A device that refuses one upload must not stop the game: the texture draws as it was.
                if (!_uploadFailureLogged)
                {
                    _uploadFailureLogged = true;
                    Genesis.Rendering.Diagnostics.RenderLog.Line("Script texture upload failed: " + ex.Message);
                }
                picture.Clean();
            }
            return picture.Handle;
        }

        /// <summary>One pixel as 0xRRGGBBAA, for checks; 0 outside the texture or with no such texture.</summary>
        internal static uint ReadPixel(int id, int x, int y)
        {
            if (!TryGet(id, out Picture picture) || x < 0 || y < 0 || x >= picture.Width || y >= picture.Height) return 0;
            int at = ((y * picture.Width) + x) * 4;
            byte[] p = picture.Pixels;
            return ((uint)p[at] << 24) | ((uint)p[at + 1] << 16) | ((uint)p[at + 2] << 8) | p[at + 3];
        }

        /// <summary>Whether a texture's latest pixels are on the GPU of the renderer that last drew it.</summary>
        public static bool IsUploaded(int id) => TryGet(id, out Picture picture) && picture.Handle.IsValid && !picture.Dirty;

        private static void Upload(Picture picture, IRenderController renderer)
        {
            int left = picture.DirtyLeft, top = picture.DirtyTop;
            int width = picture.DirtyRight - left, height = picture.DirtyBottom - top;
            picture.Clean();
            int stride = picture.Width * 4;
            if (left == 0 && width == picture.Width)
            {
                // Whole rows are already one run of bytes.
                ReadOnlySpan<byte> rows = picture.Pixels.AsSpan(top * stride, height * stride);
                if (!renderer.TryUpdateTextureRegion(picture.Handle, 0, top, width, height, rows))
                    renderer.UpdateTexture(picture.Handle, picture.Width, picture.Height, picture.Pixels);
                return;
            }
            int bytes = width * height * 4;
            byte[] packed = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                for (int row = 0; row < height; row++)
                    picture.Pixels.AsSpan(((top + row) * stride) + (left * 4), width * 4).CopyTo(packed.AsSpan(row * width * 4));
                if (!renderer.TryUpdateTextureRegion(picture.Handle, left, top, width, height, packed.AsSpan(0, bytes)))
                    renderer.UpdateTexture(picture.Handle, picture.Width, picture.Height, picture.Pixels);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packed);
            }
        }

        private static void Release(Picture picture)
        {
            if (!picture.Handle.IsValid) return;
            try { picture.Owner?.ReleaseTexture(picture.Handle); }
            catch (Exception) { /* The renderer is gone already, and its textures with it. */ }
            picture.Handle = TextureHandle.Invalid;
            picture.Owner = null;
        }

        private static bool TryGet(int id, out Picture picture)
        {
            lock (Pictures) return Pictures.TryGetValue(id, out picture);
        }
    }
}
