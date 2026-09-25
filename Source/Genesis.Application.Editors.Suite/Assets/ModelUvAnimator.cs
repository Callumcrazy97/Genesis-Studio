using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>
/// Animated-UV ("panner") material behaviour: scrolls a mesh's texture coordinates over time so a
/// static mesh reads as flowing — the technique that turns a modelled waterfall sheet into moving
/// water, and the same one used for conveyor belts, lava and scrolling skies.
///
/// <para>This is a <b>CPU-side</b> panner: it rewrites the vertex UVs each frame. That keeps it
/// entirely inside the editor/model layer with no change to the shared <c>DrawConstants</c>
/// constant buffer. A GPU panner (a UV offset uniform sampled in the pixel shader) is the
/// efficiency follow-up for scenes with many flowing surfaces.</para>
/// </summary>
public sealed class ModelUvAnimator
{
    private Vector2[] _baseUvs = [];

    /// <summary>UV units scrolled per second (V is typically negative so water falls downward).</summary>
    public Vector2 Speed { get; set; } = new(0f, -0.6f);

    public bool Enabled { get; set; }

    /// <summary>Captures the mesh's authored UVs; scrolling is always applied relative to these,
    /// so repeated frames don't accumulate drift or precision error.</summary>
    public void Capture(MeshVertex[] vertices)
    {
        _baseUvs = new Vector2[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            _baseUvs[i] = vertices[i].UV;
        }
    }

    public bool HasCapture => _baseUvs.Length > 0;

    /// <summary>
    /// Writes scrolled UVs for <paramref name="timeSeconds"/> into <paramref name="vertices"/>.
    /// Returns false when disabled or when the capture doesn't match the mesh.
    /// </summary>
    public bool Apply(MeshVertex[] vertices, float timeSeconds)
    {
        if (!Enabled || _baseUvs.Length != vertices.Length)
        {
            return false;
        }

        Vector2 offset = Speed * timeSeconds;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].UV = _baseUvs[i] + offset;
        }

        return true;
    }

    /// <summary>
    /// Builds a tiling water texture (horizontal foam bands over a blue gradient). Generated in
    /// code so a flowing surface can be demonstrated without shipping an image asset.
    /// </summary>
    public static byte[] CreateWaterTexture(int width, int height)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            float v = y / (float)height;
            // Several sharp foam bands so vertical scrolling is unmistakable.
            float band = MathF.Sin(v * MathF.Tau * 4f);
            float foam = MathF.Max(0f, band) * MathF.Max(0f, band);
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width;
                float ripple = 0.5f + 0.5f * MathF.Sin((u * 6f + v * 2f) * MathF.Tau);
                float r = 0.16f + foam * 0.72f + ripple * 0.05f;
                float g = 0.42f + foam * 0.50f + ripple * 0.07f;
                float b = 0.62f + foam * 0.34f + ripple * 0.08f;

                int i = (y * width + x) * 4;
                rgba[i + 0] = (byte)Math.Clamp(r * 255f, 0f, 255f);
                rgba[i + 1] = (byte)Math.Clamp(g * 255f, 0f, 255f);
                rgba[i + 2] = (byte)Math.Clamp(b * 255f, 0f, 255f);
                rgba[i + 3] = 255;
            }
        }

        return rgba;
    }
}
