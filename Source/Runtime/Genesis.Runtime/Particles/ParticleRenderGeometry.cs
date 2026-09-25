using System;
using System.Numerics;
using Genesis.Rendering.Meshes;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

/// <summary>Creates the shared quad or UV-cropped flipbook frames used by editor and Player.</summary>
public static class ParticleRenderGeometry
{
    public static MeshHandle[] RegisterFrames(IRenderController renderer, ParticleConfig config)
    {
        if (renderer == null) return Array.Empty<MeshHandle>();
        int columns = config?.UseFlipbook == true ? Math.Clamp(config.FlipbookColumns, 1, 64) : 1;
        int rows = config?.UseFlipbook == true ? Math.Clamp(config.FlipbookRows, 1, 64) : 1;
        int count = Math.Min(256, columns * rows);
        MeshHandle[] frames = new MeshHandle[count];
        for (int frame = 0; frame < count; frame++)
        {
            (MeshVertex[] vertices, ushort[] indices) = MeshGeometry.BuildQuad(RenderColor.White);
            int column = frame % columns;
            int row = frame / columns;
            float u0 = column / (float)columns;
            float v0 = row / (float)rows;
            float u1 = (column + 1) / (float)columns;
            float v1 = (row + 1) / (float)rows;
            foreach (ref MeshVertex vertex in vertices.AsSpan())
            {
                vertex.UV = new Vector2(
                    vertex.UV.X <= 0.5f ? u0 : u1,
                    vertex.UV.Y <= 0.5f ? v0 : v1);
            }
            frames[frame] = renderer.RegisterMesh(vertices, indices);
        }
        return frames;
    }

    public static void ReleaseFrames(IRenderController renderer, MeshHandle[] frames)
    {
        if (renderer == null || frames == null) return;
        foreach (MeshHandle frame in frames)
            if (frame.IsValid) renderer.ReleaseMesh(frame);
    }

    /// <summary>Creates a soft procedural sprite when no Image resource is assigned.</summary>
    public static TextureHandle CreateDefaultTexture(IRenderController renderer, ParticleConfig config, int size = 64)
    {
        if (renderer == null) return TextureHandle.Invalid;
        int dimension = Math.Clamp(size, 16, 256);
        byte[] rgba = new byte[dimension * dimension * 4];
        bool smoky = config?.BlendMode == ParticleBlendMode.Alpha;
        for (int y = 0; y < dimension; y++)
        {
            float ny = (y + 0.5f) / dimension * 2f - 1f;
            for (int x = 0; x < dimension; x++)
            {
                float nx = (x + 0.5f) / dimension * 2f - 1f;
                float radius = MathF.Sqrt(nx * nx + ny * ny);
                float soft = Math.Clamp(1f - radius, 0f, 1f);
                soft = smoky ? soft * soft * (0.72f + Hash(x, y) * 0.28f) : soft * soft * soft;
                int index = (y * dimension + x) * 4;
                rgba[index] = 255;
                rgba[index + 1] = 255;
                rgba[index + 2] = 255;
                rgba[index + 3] = (byte)Math.Clamp((int)MathF.Round(soft * 255f), 0, 255);
            }
        }
        return renderer.CreateTexture(dimension, dimension, rgba);
    }

    private static float Hash(int x, int y)
    {
        uint value = unchecked((uint)(x * 374761393 + y * 668265263));
        value = (value ^ (value >> 13)) * 1274126177u;
        return (value & 1023) / 1023f;
    }
}
