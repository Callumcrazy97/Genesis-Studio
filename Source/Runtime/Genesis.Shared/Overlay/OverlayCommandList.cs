using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Shared.Overlay
{
    /// <summary>What an <see cref="OverlayCommand"/> draws.</summary>
    public enum OverlayCommandKind
    {
        Text,
        TextCentered,
        Line,
        Rect,
        Sprite,
        /// <summary>A run of filled rectangles kept beside the list: A is the first, B how many.</summary>
        Rectangles,
    }

    /// <summary>
    /// One recorded overlay draw. Deliberately a flat value type with positional fields rather than
    /// a per-kind hierarchy: the list is rebuilt every frame and hashed field-by-field, and both of
    /// those are cheaper and harder to get wrong over one uniform shape.
    /// </summary>
    public readonly struct OverlayCommand
    {
        public readonly OverlayCommandKind Kind;
        public readonly string Text;
        public readonly string FontFamily;
        public readonly bool Bold;
        public readonly bool Filled;
        public readonly float A, B, C, D, Size, Stroke;
        public readonly Vector4 Color;
        /// <summary>Letter spacing in pixels between the glyphs of a text command.</summary>
        public readonly float Tracking;
        /// <summary>Screen rectangle (x, y, width, height) the command is limited to; zero size is none.</summary>
        public readonly Vector4 Clip;
        /// <summary>The textured quad of a <see cref="OverlayCommandKind.Sprite"/> command.</summary>
        public readonly SpriteDrawCall Sprite;
        /// <summary>Blended in linear light rather than on the stored sRGB values (DrawSetBlendLinear).</summary>
        public readonly bool Linear;

        public OverlayCommand(
            OverlayCommandKind kind,
            string text,
            string fontFamily,
            bool bold,
            bool filled,
            float a,
            float b,
            float c,
            float d,
            float size,
            float stroke,
            Vector4 color,
            float tracking = 0f,
            Vector4 clip = default,
            SpriteDrawCall sprite = default,
            bool linear = false)
        {
            Linear = linear;
            Tracking = tracking;
            Clip = clip;
            Sprite = sprite;
            Kind = kind;
            Text = text;
            FontFamily = fontFamily;
            Bold = bold;
            Filled = filled;
            A = a;
            B = b;
            C = c;
            D = d;
            Size = size;
            Stroke = stroke;
            Color = color;
        }
    }

    /// <summary>
    /// Records an overlay frame's draws so they can be rasterised once, by whichever backend is
    /// running, instead of being issued directly against a graphics API.
    /// </summary>
    /// <remarks>
    /// Recording rather than drawing immediately is what makes <see cref="ContentHash"/> possible,
    /// and that hash is what keeps the overlay affordable: a HUD is identical for most consecutive
    /// frames, so the rasteriser can skip the CPU raster and the texture upload entirely and simply
    /// re-composite the texture it already has.
    /// </remarks>
    public sealed class OverlayCommandList : IOverlayCanvas
    {
        private readonly List<OverlayCommand> _commands = new();
        // Rectangle batches (DrawFilledRects): one command each, the rectangles themselves here.
        private readonly List<GuiRectangle> _rectangles = new();
        private ulong _hash = Fnv1aOffset;
        private Vector4 _clip;
        private bool _linear;

        private const ulong Fnv1aOffset = 14695981039346656037;
        private const ulong Fnv1aPrime = 1099511628211;

        public int Width { get; private set; }

        public int Height { get; private set; }

        public int Count => _commands.Count;

        public IReadOnlyList<OverlayCommand> Commands => _commands;

        /// <summary>
        /// Order-sensitive digest of every recorded command, including the surface size. Equal
        /// hashes mean the rasterised result would be pixel-identical.
        /// </summary>
        public ulong ContentHash => _hash;

        /// <summary>Starts a new overlay frame at the given surface size.</summary>
        public void Reset(int width, int height)
        {
            _commands.Clear();
            _rectangles.Clear();
            _clip = default;
            _linear = false;
            HasLinearCommands = false;
            Width = width;
            Height = height;
            _hash = Fnv1aOffset;
            Hash(width);
            Hash(height);
        }

        public void DrawText(
            string text,
            Vector2 position,
            float size,
            Vector4 color,
            string fontFamily = "Segoe UI",
            bool bold = false,
            float maxWidth = 4096f,
            float maxHeight = 4096f)
        {
            if (string.IsNullOrEmpty(text)) return;
            Add(new OverlayCommand(
                OverlayCommandKind.Text, text, fontFamily, bold, filled: true,
                position.X, position.Y, maxWidth, maxHeight, size, 0f, color));
        }

        public void DrawTrackedText(string text, Vector2 position, float size, Vector4 color, string fontFamily, float tracking)
        {
            if (string.IsNullOrEmpty(text)) return;
            Add(new OverlayCommand(
                OverlayCommandKind.Text, text, fontFamily ?? "Segoe UI", bold: false, filled: true,
                position.X, position.Y, 4096f, 4096f, size, 0f, color, tracking));
        }

        public bool SupportsSprites => true;

        public void DrawSprite(in SpriteDrawCall call)
        {
            if (!call.Texture.IsValid || call.Alpha <= 0f) return;
            Add(new OverlayCommand(
                OverlayCommandKind.Sprite, null, null, bold: false, filled: true,
                call.X, call.Y, call.Width, call.Height, 0f, 0f,
                new Vector4(call.Tint.R, call.Tint.G, call.Tint.B, call.Tint.A), sprite: call));
            Hash(call.Texture.Id);
            Hash(call.OriginX); Hash(call.OriginY); Hash(call.Rotation);
            Hash(call.ScaleX); Hash(call.ScaleY); Hash(call.Alpha);
            Hash(call.UvRect.X); Hash(call.UvRect.Y); Hash(call.UvRect.Z); Hash(call.UvRect.W);
            Hash(call.ClipRect.X); Hash(call.ClipRect.Y); Hash(call.ClipRect.Z); Hash(call.ClipRect.W);
            Hash(call.Shader.Id); Hash((int)call.Blend); Hash(call.SmoothSampling ? 1 : 0);
            if (call.PointSampling) Hash(0x504F494E); // Only hashed when set, so other frames hash as before.
        }

        /// <summary>Every command added after this is limited to <paramref name="clip"/> until the next call or frame.</summary>
        public void SetClip(Vector4 clip) =>
            _clip = clip.Z > 0f && clip.W > 0f ? clip : default;

        /// <summary>Every command added after this blends in linear light, or not, until the next call or frame.</summary>
        public void SetBlendLinear(bool linear) => _linear = linear;

        /// <summary>True when any recorded command blends in linear light.</summary>
        public bool HasLinearCommands { get; private set; }

        public void DrawTextCentered(
            string text,
            float centerX,
            float y,
            float width,
            float size,
            Vector4 color,
            string fontFamily = "Segoe UI",
            bool bold = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            Add(new OverlayCommand(
                OverlayCommandKind.TextCentered, text, fontFamily, bold, filled: true,
                centerX, y, width, size + 8f, size, 0f, color));
        }

        public void DrawLine(Vector2 from, Vector2 to, Vector4 color, float width = 1.5f)
        {
            Add(new OverlayCommand(
                OverlayCommandKind.Line, null, null, bold: false, filled: false,
                from.X, from.Y, to.X, to.Y, 0f, width, color));
        }

        public void DrawRect(
            float x,
            float y,
            float width,
            float height,
            Vector4 color,
            float strokeWidth = 1.5f,
            bool filled = false)
        {
            Add(new OverlayCommand(
                OverlayCommandKind.Rect, null, null, bold: false, filled,
                x, y, width, height, 0f, strokeWidth, color));
        }

        public void DrawRect(
            Vector2 position,
            Vector2 size,
            Vector4 color,
            float strokeWidth = 1.5f,
            bool filled = false)
            => DrawRect(position.X, position.Y, size.X, size.Y, color, strokeWidth, filled);

        /// <summary>
        /// A run of filled rectangles as one command (PGSL DrawRectanglesFromList): the rectangles
        /// are copied beside the list, so thousands cost one command rather than thousands.
        /// </summary>
        public void DrawFilledRects(ReadOnlySpan<GuiRectangle> rectangles)
        {
            if (rectangles.IsEmpty) return;
            int first = _rectangles.Count;
            _rectangles.AddRange(rectangles);
            Add(new OverlayCommand(
                OverlayCommandKind.Rectangles, null, null, bold: false, filled: true,
                first, rectangles.Length, 0f, 0f, 0f, 0f, Vector4.One));
            // Two values a step rather than one byte: thousands of rectangles keep the digest cheap.
            foreach (GuiRectangle rectangle in rectangles)
            {
                HashPair(rectangle.X, rectangle.Y);
                HashPair(rectangle.Width, rectangle.Height);
                HashPair(rectangle.Color.X, rectangle.Color.Y);
                HashPair(rectangle.Color.Z, rectangle.Color.W);
            }
        }

        /// <summary>The rectangles of a <see cref="OverlayCommandKind.Rectangles"/> command; empty for any other.</summary>
        public ReadOnlySpan<GuiRectangle> RectanglesOf(in OverlayCommand command) =>
            command.Kind == OverlayCommandKind.Rectangles
                ? System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_rectangles).Slice((int)command.A, (int)command.B)
                : ReadOnlySpan<GuiRectangle>.Empty;

        private void Add(OverlayCommand command)
        {
            if (_clip != default || command.Tracking != 0f || _linear)
            {
                command = new OverlayCommand(command.Kind, command.Text, command.FontFamily, command.Bold, command.Filled,
                    command.A, command.B, command.C, command.D, command.Size, command.Stroke, command.Color,
                    command.Tracking, _clip, command.Sprite, _linear);
                Hash(command.Tracking);
                Hash(_clip.X);
                Hash(_clip.Y);
                Hash(_clip.Z);
                Hash(_clip.W);
                if (_linear)
                {
                    Hash(0x4C494E); // Only hashed when set, so a frame without it hashes as before.
                    HasLinearCommands = true;
                }
            }
            _commands.Add(command);
            Hash((int)command.Kind);
            Hash(command.Text);
            Hash(command.FontFamily);
            Hash(command.Bold ? 1 : 0);
            Hash(command.Filled ? 1 : 0);
            Hash(command.A);
            Hash(command.B);
            Hash(command.C);
            Hash(command.D);
            Hash(command.Size);
            Hash(command.Stroke);
            Hash(command.Color.X);
            Hash(command.Color.Y);
            Hash(command.Color.Z);
            Hash(command.Color.W);
        }

        private void Hash(string value)
        {
            if (value == null)
            {
                Hash(-1);
                return;
            }

            Hash(value.Length);
            for (int i = 0; i < value.Length; i++)
                Hash(value[i]);
        }

        private void Hash(float value) => Hash(BitConverter.SingleToInt32Bits(value));

        private void HashPair(float first, float second)
        {
            unchecked
            {
                _hash ^= ((ulong)(uint)BitConverter.SingleToInt32Bits(first) << 32) | (uint)BitConverter.SingleToInt32Bits(second);
                _hash *= Fnv1aPrime;
            }
        }

        private void Hash(int value)
        {
            unchecked
            {
                for (int shift = 0; shift < 32; shift += 8)
                {
                    _hash ^= (byte)(value >> shift);
                    _hash *= Fnv1aPrime;
                }
            }
        }
    }
}
