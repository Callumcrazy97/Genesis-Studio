using System.Numerics;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// One filled rectangle of a batch (PGSL DrawRectanglesFromList): position and size in pixels,
    /// colour as straight-alpha red, green, blue and alpha from 0 to 1. A batch travels to the GUI
    /// overlay as one command and is drawn exactly as the same rectangles drawn one at a time.
    /// </summary>
    public readonly struct GuiRectangle
    {
        public readonly float X, Y, Width, Height;
        public readonly Vector4 Color;

        public GuiRectangle(float x, float y, float width, float height, Vector4 color)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Color = color;
        }
    }

    /// <summary>
    /// One part of an image frame drawn into a rectangle (PGSL DrawSpritePartsFromList): u0, v0 to
    /// u1, v1 are fractions of the frame, x, y, width and height are pixels.
    /// </summary>
    public readonly struct GuiSpritePart
    {
        public readonly int Frame;
        public readonly float U0, V0, U1, V1;
        public readonly float X, Y, Width, Height;
        public readonly float Alpha;

        public GuiSpritePart(int frame, float u0, float v0, float u1, float v1,
            float x, float y, float width, float height, float alpha)
        {
            Frame = frame;
            U0 = u0; V0 = v0; U1 = u1; V1 = v1;
            X = x; Y = y; Width = width; Height = height;
            Alpha = alpha;
        }
    }
}
