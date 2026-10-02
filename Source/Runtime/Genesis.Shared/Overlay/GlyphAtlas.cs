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

    /// <summary>
    /// Newly packed glyphs the caller must copy into the atlas texture: one glyph, or a strip of
    /// glyphs that were packed side by side.
    /// </summary>
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

        /// <summary>
        /// Reserved opaque-white cell used by overlay rectangles and lines. Keeping shapes in the
        /// glyph atlas lets an ordered mixture of panels, rules, and text remain one sprite batch.
        /// </summary>
        public const int SolidCellSize = 4;

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

        private readonly struct LayoutKey : IEquatable<LayoutKey>
        {
            private readonly string _text;
            private readonly string _family;
            private readonly float _size;
            private readonly bool _bold;

            public LayoutKey(string text, string family, float size, bool bold)
            {
                _text = text;
                _family = family;
                _size = size;
                _bold = bold;
            }

            public bool Equals(LayoutKey other) =>
                _size.Equals(other._size) && _bold == other._bold
                && string.Equals(_text, other._text, StringComparison.Ordinal)
                && string.Equals(_family, other._family, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is LayoutKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(_text, _family, _size, _bold);
        }

        private readonly struct CachedRun
        {
            public readonly GlyphQuad[] Quads;
            public readonly float Advance;

            public CachedRun(GlyphQuad[] quads, float advance)
            {
                Quads = quads;
                Advance = advance;
            }
        }

        private readonly Dictionary<GlyphKey, GlyphEntry> _glyphs = new();
        private readonly Dictionary<(string Family, bool Bold), SKTypeface> _typefaces = new();
        private readonly Dictionary<(string Family, float Size, bool Bold), SKFont> _fonts = new();
        private readonly Dictionary<LayoutKey, CachedRun> _layoutCache = new();
        private readonly Queue<LayoutKey> _layoutOrder = new();
        private readonly List<GlyphUpload> _uploads = new();
        private readonly SKPaint _paint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };

        private int _penX = SolidCellSize + Padding;
        private int _penY;
        private int _shelfHeight = SolidCellSize + Padding;
        private int _resetGeneration;
        private bool _disposed;
        private const int LayoutCacheCapacity = 1024;

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
            CachedRun run = ResolveRun(text, resolvedFamily, resolvedSize, bold, font);
            for (int i = 0; i < run.Quads.Length; i++)
            {
                GlyphQuad quad = run.Quads[i];
                quads.Add(new GlyphQuad(
                    quad.X + x,
                    quad.Y + topY,
                    quad.Width,
                    quad.Height,
                    quad.U0,
                    quad.V0,
                    quad.U1,
                    quad.V1));
            }
        }

        private static readonly object MeasureLock = new();
        private static GlyphAtlas _measuring;

        /// <summary>
        /// The width and line height of text as the overlay draws it, for layout by code that has no
        /// renderer to ask. Nothing is rasterised; one line is measured, and a line break in the
        /// text is not a new line.
        /// </summary>
        public static System.Numerics.Vector2 Measure(string text, string family, float size, bool bold = false)
        {
            lock (MeasureLock)
            {
                _measuring ??= new GlyphAtlas();
                SKFont font = _measuring.GetFont(family, size, bold, out _, out _);
                SKFontMetrics metrics = font.Metrics;
                float width = string.IsNullOrEmpty(text) ? 0f : font.MeasureText(text, _measuring._paint);
                return new System.Numerics.Vector2(width, metrics.Descent - metrics.Ascent);
            }
        }

        /// <summary>Advance width of a run, for centring.</summary>
        public float MeasureRun(string text, string family, float size, bool bold)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(text)) return 0f;
            SKFont font = GetFont(family, size, bold, out string resolvedFamily, out float resolvedSize);
            return ResolveRun(text, resolvedFamily, resolvedSize, bold, font).Advance;
        }

        private CachedRun ResolveRun(string text, string family, float size, bool bold, SKFont font)
        {
            var key = new LayoutKey(text, family, size, bold);
            if (_layoutCache.TryGetValue(key, out CachedRun cached)) return cached;

            SKFontMetrics metrics = font.Metrics;
            ushort[] glyphIds = font.GetGlyphs(text);
            if (glyphIds.Length == 0)
            {
                cached = new CachedRun(Array.Empty<GlyphQuad>(), 0f);
                CacheRun(key, cached);
                return cached;
            }

            SKPoint[] positions = font.GetGlyphPositions(text, new SKPoint(0f, -metrics.Ascent));
            var runQuads = new List<GlyphQuad>(glyphIds.Length);
            for (int i = 0; i < glyphIds.Length && i < positions.Length; i++)
            {
                GlyphEntry entry = Resolve(family, size, bold, glyphIds[i], font);
                if (!entry.HasPixels) continue;

                runQuads.Add(new GlyphQuad(
                    positions[i].X + entry.OffsetX,
                    positions[i].Y + entry.OffsetY,
                    entry.Width,
                    entry.Height,
                    entry.U0,
                    entry.V0,
                    entry.U1,
                    entry.V1));
            }

            cached = new CachedRun(runQuads.ToArray(), font.MeasureText(text, _paint));
            CacheRun(key, cached);
            return cached;
        }

        private void CacheRun(LayoutKey key, CachedRun run)
        {
            while (_layoutCache.Count >= LayoutCacheCapacity && _layoutOrder.Count > 0)
                _layoutCache.Remove(_layoutOrder.Dequeue());
            _layoutCache[key] = run;
            _layoutOrder.Enqueue(key);
        }

        /// <summary>
        /// New glyph bitmaps since the last call. The caller uploads them into its atlas texture;
        /// the list is cleared, so an unchanged frame drains nothing.
        /// </summary>
        /// <remarks>
        /// Glyphs packed one after another along a shelf come back as one strip. A line of text in
        /// a size not used before is twenty or thirty new glyphs, and a texture update is not
        /// cheap on every graphics backend: on some each one is its own submission that is waited
        /// for, which made the frame that first showed such a line a tenth of a second long. As
        /// strips, the same line is one update, or two where it runs on to the next shelf.
        /// </remarks>
        public IReadOnlyList<GlyphUpload> DrainUploads()
        {
            ThrowIfDisposed();
            if (_uploads.Count == 0) return Array.Empty<GlyphUpload>();
            if (!StripsEnabled)
            {
                GlyphUpload[] singly = _uploads.ToArray();
                _uploads.Clear();
                return singly;
            }

            var drained = new List<GlyphUpload>();
            for (int start = 0; start < _uploads.Count;)
            {
                int end = start + 1;
                while (end < _uploads.Count && _uploads[end].Y == _uploads[start].Y
                    && _uploads[end].X == _uploads[end - 1].X + _uploads[end - 1].Width)
                    end++;
                drained.Add(end - start == 1 ? _uploads[start] : Strip(start, end));
                start = end;
            }

            _uploads.Clear();
            return drained;
        }

        /// <summary>
        /// Set <c>GENESIS_GLYPH_STRIPS=0</c> to send every new glyph as an update of its own, as
        /// engines before this did: for measuring what the strips save on a given machine.
        /// </summary>
        private static readonly bool StripsEnabled = Environment.GetEnvironmentVariable("GENESIS_GLYPH_STRIPS") != "0";

        /// <summary>
        /// One upload covering glyphs <paramref name="start"/> to <paramref name="end"/>, which lie
        /// side by side on one shelf. The strip is as tall as its tallest glyph; under a shorter
        /// glyph it is transparent white, which is what that part of the shelf holds anyway, since
        /// nothing else is ever packed there.
        /// </summary>
        private GlyphUpload Strip(int start, int end)
        {
            GlyphUpload first = _uploads[start], last = _uploads[end - 1];
            int width = last.X + last.Width - first.X, height = 0;
            for (int i = start; i < end; i++) height = Math.Max(height, _uploads[i].Height);
            byte[] pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
            }

            for (int i = start; i < end; i++)
            {
                GlyphUpload glyph = _uploads[i];
                int column = (glyph.X - first.X) * 4, bytes = glyph.Width * 4;
                for (int row = 0; row < glyph.Height; row++)
                    Buffer.BlockCopy(glyph.Pixels, row * bytes, pixels, (row * width * 4) + column, bytes);
            }

            return new GlyphUpload(first.X, first.Y, width, height, pixels);
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
            _layoutCache.Clear();
            _layoutOrder.Clear();
            _uploads.Clear();
            _penX = SolidCellSize + Padding;
            _penY = 0;
            _shelfHeight = SolidCellSize + Padding;
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
            string requestedFamily = resolvedFamily;
            bool isPayload = (requestedFamily.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || requestedFamily.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                && System.IO.File.Exists(requestedFamily);
            if (isPayload)
            {
                var info = new System.IO.FileInfo(requestedFamily);
                // A live replacement must invalidate both font metrics and already rasterised glyphs.
                resolvedFamily += "|" + info.LastWriteTimeUtc.Ticks + "|" + info.Length;
            }
            resolvedSize = size > 0.1f ? size : 12f;

            var key = (resolvedFamily, resolvedSize, bold);
            if (_fonts.TryGetValue(key, out SKFont cached)) return cached;

            var typefaceKey = (resolvedFamily, bold);
            if (!_typefaces.TryGetValue(typefaceKey, out SKTypeface typeface))
            {
                typeface = (isPayload ? LoadFontPayload(requestedFamily) : null)
                    ?? SKTypeface.FromFamilyName(
                    requestedFamily,
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

        private static SKTypeface LoadFontPayload(string path)
        {
            // FromFile memory-maps and locks the source on Windows, preventing live authoring edits.
            using SKData data = SKData.CreateCopy(System.IO.File.ReadAllBytes(path));
            return SKTypeface.FromData(data);
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
            _layoutCache.Clear();
            _layoutOrder.Clear();
            _uploads.Clear();
            _paint.Dispose();
        }
    }
}
