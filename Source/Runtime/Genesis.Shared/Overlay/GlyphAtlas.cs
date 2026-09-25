using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Genesis.Shared.Overlay
{
    /// <summary>One glyph's quad, in overlay pixel space, ready to submit as a textured sprite.</summary>
    public readonly struct GlyphQuad
    {
        public readonly float X, Y, Width, Height;
        public readonly float U0, V0, U1, V1;

        public GlyphQuad(float x, float y, float width, float height, float u0, float v0, float u1, float v1)
        {
            X = x; Y = y; Width = width; Height = height;
            U0 = u0; V0 = v0; U1 = u1; V1 = v1;
        }
    }

    /// <summary>A newly packed glyph the caller must copy into the atlas texture.</summary>
    public readonly struct GlyphUpload
    {
        public readonly int X, Y, Width, Height;

        /// <summary>Tightly packed BGRA, white with the glyph's coverage in alpha.</summary>
        public readonly byte[] Pixels;

        public GlyphUpload(int x, int y, int width, int height, byte[] pixels)
        {
            X = x; Y = y; Width = width; Height = height; Pixels = pixels;
        }
    }

    /// <summary>
    /// Rasterises each distinct glyph exactly once and packs it into a single GPU-resident atlas, so
    /// drawing text costs one textured quad per glyph and no CPU work at all.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this shape.</b> Turning a font outline into coverage is CPU work in every engine
    /// — Unity, Unreal, Godot and Dear ImGui all do it. What none of them do is repeat it per frame.
    /// The rule this class exists to enforce is <i>rasterise once, ever</i>: after a glyph's first
    /// appearance it is a texture lookup forever, so a HUD whose text changes every frame costs the
    /// same as one that never changes.</para>
    ///
    /// <para><b>White-with-coverage, not alpha-only.</b> Glyph pixels are stored as opaque white
    /// with coverage in alpha, which lets the existing sprite shader (<c>tex * tint</c>) colour text
    /// by tint alone. No new shader, no new pipeline state, and text batches with every other
    /// overlay primitive.</para>
    ///
    /// <para><b>Packing.</b> A shelf packer, which suits glyphs because they are similar-height runs.
    /// On overflow the atlas resets and the frame re-registers its glyphs once — bounded, and at
    /// 2048² it takes thousands of glyphs to reach.</para>
    /// </remarks>
    public sealed class GlyphAtlas : IDisposable
    {
        /// <summary>Square atlas edge. 2048² holds several thousand glyphs at HUD sizes.</summary>
        public const int AtlasSize = 2048;

        /// <summary>Keeps neighbouring glyphs from bleeding into each other under linear filtering.</summary>
        private const int Padding = 1;

        private readonly struct GlyphKey : IEquatable<GlyphKey>
        {
            private readonly string _family;
            private readonly float _size;
            private readonly bool _bold;
            private readonly ushort _glyph;

            public GlyphKey(string family, float size, bool bold, ushort glyph)
            {
                _family = family; _size = size; _bold = bold; _glyph = glyph;
            }

            public bool Equals(GlyphKey other) =>
                _glyph == other._glyph && _bold == other._bold && _size.Equals(other._size) &&
                string.Equals(_family, other._family, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is GlyphKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_family, _size, _bold, _glyph);
        }

        private readonly struct GlyphEntry
        {
            /// <summary>Offset from the pen's baseline origin to the quad's top-left, in pixels.</summary>
            public readonly float OffsetX, OffsetY;
            public readonly float Width, Height;
            public readonly float U0, V0, U1, V1;

            /// <summary>False for whitespace, which advances the pen but has nothing to draw.</summary>
            public readonly bool HasPixels;

            public GlyphEntry(float offsetX, float offsetY, float width, float height,
                float u0, float v0, float u1, float v1, bool hasPixels)
            {
                OffsetX = offsetX; OffsetY = offsetY; Width = width; Height = height;
                U0 = u0; V0 = v0; U1 = u1; V1 = v1; HasPixels = hasPixels;
            }
        }

        private readonly Dictionary<GlyphKey, GlyphEntry> _glyphs = new();
        private readonly Dictionary<(string Family, bool Bold), SKTypeface> _typefaces = new();
        private readonly Dictionary<(string Family, float Size, bool Bold), SKFont> _fonts = new();
        private readonly List<GlyphUpload> _uploads = new();
        private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };

        private int _penX, _penY, _shelfHeight;
        private int _resetGeneration;
        private bool _disposed;

        /// <summary>Increments whenever the atlas is cleared, so a caller can drop cached UVs.</summary>
        public int ResetGeneration => _resetGeneration;

        /// <summary>Glyphs packed since construction or the last reset.</summary>
        public int GlyphCount => _glyphs.Count;

        /// <summary>Total glyph rasterisations performed — the number this design exists to bound.</summary>
        public long RasterCount { get; private set; }

        /// <summary>
        /// Appends the quads for <paramref name="text"/> to <paramref name="quads"/>, laying it out
        /// with its <b>top</b> edge at <paramref name="topY"/> to match the overlay contract.
        /// </summary>
        public void LayoutRun(
            string text,
            string family,
            float size,
            bool bold,
            float x,
            float topY,
            List<GlyphQuad> quads)
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(quads);
            if (string.IsNullOrEmpty(text)) return;

            SKFont font = GetFont(family, size, bold, out string resolvedFamily, out float resolvedSize);
            SKFontMetrics metrics = font.Metrics;
            float baseline = topY - metrics.Ascent;

            ushort[] glyphIds = font.GetGlyphs(text);
            if (glyphIds.Length == 0) return;
            SKPoint[] positions = font.GetGlyphPositions(text, new SKPoint(x, baseline));

            for (int i = 0; i < glyphIds.Length && i < positions.Length; i++)
            {
                GlyphEntry entry = Resolve(resolvedFamily, resolvedSize, bold, glyphIds[i], font);
                if (!entry.HasPixels) continue;

                quads.Add(new GlyphQuad(
                    positions[i].X + entry.OffsetX,
                    positions[i].Y + entry.OffsetY,
                    entry.Width,
                    entry.Height,
                    entry.U0, entry.V0, entry.U1, entry.V1));
            }
        }

        /// <summary>Advance width of a run, for centring.</summary>
        public float MeasureRun(string text, string family, float size, bool bold)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(text)) return 0f;
            SKFont font = GetFont(family, size, bold, out _, out _);
            return font.MeasureText(text, _paint);
        }

        /// <summary>
        /// New glyph bitmaps since the last call. The caller uploads them into its atlas texture;
        /// the list is cleared, so an unchanged frame drains nothing.
        /// </summary>
        public IReadOnlyList<GlyphUpload> DrainUploads()
        {
            ThrowIfDisposed();
            if (_uploads.Count == 0) return Array.Empty<GlyphUpload>();
            GlyphUpload[] drained = _uploads.ToArray();
            _uploads.Clear();
            return drained;
        }

        private GlyphEntry Resolve(string family, float size, bool bold, ushort glyphId, SKFont font)
        {
            var key = new GlyphKey(family, size, bold, glyphId);
            if (_glyphs.TryGetValue(key, out GlyphEntry cached)) return cached;

            GlyphEntry entry = Rasterize(glyphId, font);
            _glyphs[key] = entry;
            return entry;
        }

        private GlyphEntry Rasterize(ushort glyphId, SKFont font)
        {
            using SKPath path = font.GetGlyphPath(glyphId);
            if (path is null || path.IsEmpty) return default;   // whitespace

            SKRect bounds = path.TightBounds;
            int width = (int)MathF.Ceiling(bounds.Width) + (Padding * 2);
            int height = (int)MathF.Ceiling(bounds.Height) + (Padding * 2);
            if (width <= Padding * 2 || height <= Padding * 2) return default;

            if (!TryPack(width, height, out int cellX, out int cellY))
            {
                Reset();
                if (!TryPack(width, height, out cellX, out cellY))
                {
                    // One glyph larger than the whole atlas. Nothing sane to do but skip it, and say so.
                    return default;
                }
            }

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKBitmap bitmap = new(info);
            using (SKCanvas canvas = new(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                // Move the glyph's tight bounds to the padded cell origin.
                canvas.Translate(Padding - bounds.Left, Padding - bounds.Top);
                canvas.DrawPath(path, _paint);
            }

            // White with coverage in alpha. The source is premultiplied against white, so the colour
            // channels already equal alpha — overwriting them with 255 is the un-premultiply.
            byte[] pixels = new byte[width * height * 4];
            ReadOnlySpan<byte> source = bitmap.GetPixelSpan();
            int stride = bitmap.RowBytes;
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    int s = (row * stride) + (column * 4);
                    int d = ((row * width) + column) * 4;
                    byte alpha = source[s + 3];
                    pixels[d + 0] = 255;
                    pixels[d + 1] = 255;
                    pixels[d + 2] = 255;
                    pixels[d + 3] = alpha;
                }
            }

            _uploads.Add(new GlyphUpload(cellX, cellY, width, height, pixels));
            RasterCount++;

            const float inv = 1f / AtlasSize;
            return new GlyphEntry(
                bounds.Left - Padding,
                bounds.Top - Padding,
                width,
                height,
                cellX * inv,
                cellY * inv,
                (cellX + width) * inv,
                (cellY + height) * inv,
                hasPixels: true);
        }

        private bool TryPack(int width, int height, out int x, out int y)
        {
            x = 0; y = 0;
            if (width > AtlasSize || height > AtlasSize) return false;

            if (_penX + width > AtlasSize)
            {
                // Next shelf.
                _penX = 0;
                _penY += _shelfHeight;
                _shelfHeight = 0;
            }

            if (_penY + height > AtlasSize) return false;

            x = _penX;
            y = _penY;
            _penX += width;
            if (height > _shelfHeight) _shelfHeight = height;
            return true;
        }

        private void Reset()
        {
            _glyphs.Clear();
            _uploads.Clear();
            _penX = 0;
            _penY = 0;
            _shelfHeight = 0;
            _resetGeneration++;
        }

        private SKFont GetFont(
            string family,
            float size,
            bool bold,
            out string resolvedFamily,
            out float resolvedSize)
        {
            resolvedFamily = string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family;
            resolvedSize = size > 0.1f ? size : 12f;

            var key = (resolvedFamily, resolvedSize, bold);
            if (_fonts.TryGetValue(key, out SKFont cached)) return cached;

            var typefaceKey = (resolvedFamily, bold);
            if (!_typefaces.TryGetValue(typefaceKey, out SKTypeface typeface))
            {
                typeface = SKTypeface.FromFamilyName(
                    resolvedFamily,
                    bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                    SKFontStyleWidth.Normal,
                    SKFontStyleSlant.Upright) ?? SKTypeface.CreateDefault();
                _typefaces[typefaceKey] = typeface;
            }

            var font = new SKFont(typeface, resolvedSize)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = true,
            };
            _fonts[key] = font;
            return font;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GlyphAtlas));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (SKFont font in _fonts.Values) font.Dispose();
            _fonts.Clear();
            foreach (SKTypeface typeface in _typefaces.Values) typeface.Dispose();
            _typefaces.Clear();
            _glyphs.Clear();
            _uploads.Clear();
            _paint.Dispose();
        }
    }
}
