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
            Vector4 color)
        {
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
        private ulong _hash = Fnv1aOffset;

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

        private void Add(in OverlayCommand command)
        {
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
