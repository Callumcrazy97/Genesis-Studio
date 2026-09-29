using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>One texture geometry and facing convention for Terrain, Room and gameplay.</summary>
public static class TerrainTextureGeometry
{
    public static bool IsExtruded(string mode) => mode is "Extruded2D" or "2";

    public static Matrix4x4 Facing(string mode, Matrix4x4 placement, Vector3 cameraEye)
    {
        float angle = mode is "Diagonal2D" or "1" ? MathF.PI / 4 : 0;
        if (mode is "Billboard2D" or "0" && Matrix4x4.Invert(placement, out Matrix4x4 inverse))
        {
            Vector3 localEye = Vector3.Transform(cameraEye, inverse);
            angle = MathF.Atan2(localEye.X, localEye.Z);
        }
        return Matrix4x4.CreateRotationY(angle);
    }

    public static (MeshVertex[] Vertices, ushort[] Indices) Plane(int width, int height)
    {
        float halfWidth = Math.Max(1, width) / (float)Math.Max(1, height) * .5f;
        MeshVertex V(float x, float y, float u, float v) => new()
        { Position = new(x, y, 0), Normal = Vector3.UnitZ, UV = new(u, v), Color = Vector4.One };
        return ([V(-halfWidth, 0, 0, 1), V(halfWidth, 0, 1, 1), V(halfWidth, 1, 1, 0), V(-halfWidth, 1, 0, 0)], [0, 1, 2, 0, 2, 3]);
    }

    /// <summary>Extrudes opaque image cells, including their silhouette and internal holes.
    /// Large images use at most 48 cells per axis to fit the renderer's 16-bit mesh budget.</summary>
    public static (MeshVertex[] Vertices, ushort[] Indices) Extruded(int width, int height, byte[] albedo)
    {
        if (width < 1 || height < 1 || albedo.Length != (long)width * height * 4)
            throw new ArgumentException("Texture geometry requires complete RGBA image pixels.");
        float reduction = Math.Max(1, Math.Max(width, height) / 48f);
        int columns = Math.Max(1, (int)MathF.Ceiling(width / reduction));
        int rows = Math.Max(1, (int)MathF.Ceiling(height / reduction));
        bool[,] cells = new bool[columns, rows];
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            int left = x * width / columns, right = (x + 1) * width / columns;
            int top = y * height / rows, bottom = (y + 1) * height / rows;
            for (int py = top; py < bottom && !cells[x, y]; py++)
            for (int px = left; px < right; px++)
                if (albedo[(py * width + px) * 4 + 3] > 16) { cells[x, y] = true; break; }
        }
        List<MeshVertex> vertices = []; List<ushort> indices = [];
        float aspect = width / (float)height;
        bool Solid(int x, int y) => x >= 0 && x < columns && y >= 0 && y < rows && cells[x, y];
        void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            ushort first = checked((ushort)vertices.Count);
            vertices.Add(new() { Position = a, Normal = normal, UV = ua, Color = Vector4.One });
            vertices.Add(new() { Position = b, Normal = normal, UV = ub, Color = Vector4.One });
            vertices.Add(new() { Position = c, Normal = normal, UV = uc, Color = Vector4.One });
            vertices.Add(new() { Position = d, Normal = normal, UV = ud, Color = Vector4.One });
            indices.AddRange([first, (ushort)(first + 1), (ushort)(first + 2), first, (ushort)(first + 2), (ushort)(first + 3)]);
        }
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            if (!cells[x, y]) continue;
            float u0 = x / (float)columns, u1 = (x + 1f) / columns, v0 = y / (float)rows, v1 = (y + 1f) / rows;
            float x0 = (u0 - .5f) * aspect, x1 = (u1 - .5f) * aspect, y0 = 1 - v1, y1 = 1 - v0;
            const float front = .06f, back = -.06f;
            Vector2 uv = new((u0 + u1) * .5f, (v0 + v1) * .5f);
            Face(new(x0, y0, front), new(x1, y0, front), new(x1, y1, front), new(x0, y1, front), Vector3.UnitZ,
                new(u0, v1), new(u1, v1), new(u1, v0), new(u0, v0));
            Face(new(x1, y0, back), new(x0, y0, back), new(x0, y1, back), new(x1, y1, back), -Vector3.UnitZ,
                new(u1, v1), new(u0, v1), new(u0, v0), new(u1, v0));
            if (!Solid(x - 1, y)) Face(new(x0, y0, back), new(x0, y0, front), new(x0, y1, front), new(x0, y1, back), -Vector3.UnitX, uv, uv, uv, uv);
            if (!Solid(x + 1, y)) Face(new(x1, y0, front), new(x1, y0, back), new(x1, y1, back), new(x1, y1, front), Vector3.UnitX, uv, uv, uv, uv);
            if (!Solid(x, y - 1)) Face(new(x0, y1, front), new(x1, y1, front), new(x1, y1, back), new(x0, y1, back), Vector3.UnitY, uv, uv, uv, uv);
            if (!Solid(x, y + 1)) Face(new(x0, y0, back), new(x1, y0, back), new(x1, y0, front), new(x0, y0, front), -Vector3.UnitY, uv, uv, uv, uv);
        }
        return (vertices.ToArray(), indices.ToArray());
    }
}
