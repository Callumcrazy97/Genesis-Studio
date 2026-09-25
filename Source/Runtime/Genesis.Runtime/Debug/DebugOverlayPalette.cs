using System.Numerics;

namespace Genesis.Runtime.Debugger;

/// <summary>
/// Genesis Dark palette for the F6 in-game debugger overlay — matches Studio
/// <c>EditorChrome</c> / ThemePalette.Dark defaults (#14161D canvas, #6C8CFF accent).
/// </summary>
internal static class DebugOverlayPalette
{
    public static readonly Vector4 Canvas = Rgba(0x14, 0x16, 0x1D, 0.96f);
    public static readonly Vector4 Surface = Rgba(0x1C, 0x1F, 0x28, 0.94f);
    public static readonly Vector4 Raised = Rgba(0x25, 0x29, 0x34, 0.92f);
    public static readonly Vector4 Hover = Rgba(0x2E, 0x33, 0x40, 0.95f);
    public static readonly Vector4 Border = Rgba(0x37, 0x3D, 0x4C, 0.90f);
    public static readonly Vector4 Text = Rgba(0xEE, 0xF1, 0xF8, 1f);
    public static readonly Vector4 Muted = Rgba(0x9D, 0xA6, 0xBB, 1f);
    public static readonly Vector4 Accent = Rgba(0x6C, 0x8C, 0xFF, 1f);
    public static readonly Vector4 AccentDim = Rgba(0x6C, 0x8C, 0xFF, 0.35f);
    public static readonly Vector4 AccentFill = Rgba(0x6C, 0x8C, 0xFF, 0.28f);
    public static readonly Vector4 Success = Rgba(0x5B, 0xCA, 0x9A, 1f);
    public static readonly Vector4 Warning = Rgba(0xF5, 0xB5, 0x51, 1f);
    public static readonly Vector4 Error = Rgba(0xF4, 0x62, 0x6F, 1f);
    public static readonly Vector4 Selected = Rgba(0x6C, 0x8C, 0xFF, 0.45f);
    public static readonly Vector4 UsageTrack = Rgba(0x37, 0x3D, 0x4C, 0.90f);
    public static readonly Vector4 UsageFill = Rgba(0x5B, 0xCA, 0x9A, 1f);

    private static Vector4 Rgba(byte r, byte g, byte b, float a) =>
        new(r / 255f, g / 255f, b / 255f, a);
}
