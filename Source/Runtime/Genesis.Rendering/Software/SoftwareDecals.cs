using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Genesis.Rendering.Abstractions;

namespace Genesis.Rendering.Software
{
    /// <summary>
    /// The decal pass (DecalShaders) on the CPU, for the Software renderer, which runs no shaders: the
    /// same constants and decal records, the same box test, surface normal from the neighbouring
    /// depths, angle and depth fades, and the three blends, into the scene's 8-bit colour.
    /// </summary>
    internal static class SoftwareDecals
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Constants
        {
            public Matrix4x4 ViewProjection;
            public Matrix4x4 InvViewProjection;
            public Vector4 CameraPos;
            public Vector4 Viewport;
            public Vector4 Params;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Decal
        {
            public Vector4 Center;
            public Vector4 AxisX;
            public Vector4 AxisY;
            public Vector4 AxisZ;
            public Vector4 Color;
            public Vector4 Params;
        }

        public static void Draw(
            byte[] bgra, int width, int height, float[] depth,
            byte[] constantBytes, byte[] decalBytes, int instanceCount,
            byte[] texPixels, int texWidth, int texHeight, GpuFormat texFormat)
        {
            if (bgra == null || depth == null || constantBytes == null || decalBytes == null || instanceCount <= 0
                || width <= 0 || height <= 0 || depth.Length < width * height || bgra.Length < width * height * 4)
                return;
            if (constantBytes.Length < Marshal.SizeOf<Constants>()) return;
            Constants c = MemoryMarshal.Read<Constants>(constantBytes);
            bool reversed = c.CameraPos.W > 0.5f;
            int first = (int)MathF.Round(c.Params.X);
            int mode = (int)MathF.Round(c.Params.Y);
            Vector3 eye = new(c.CameraPos.X, c.CameraPos.Y, c.CameraPos.Z);
            int stride = Marshal.SizeOf<Decal>();
            bool texture = texPixels != null && texWidth > 0 && texHeight > 0 && texPixels.Length >= texWidth * texHeight * 4
                && texFormat is not (GpuFormat.BC5UNorm or GpuFormat.BC7UNorm or GpuFormat.BC7UNormSrgb);
            bool bgrTexture = texFormat == GpuFormat.B8G8R8A8UNorm;

            Vector3 WorldAt(int x, int y, float d)
            {
                float u = (x + 0.5f) / width, v = (y + 0.5f) / height;
                Vector4 h = Vector4.Transform(new Vector4(u * 2f - 1f, 1f - v * 2f, d, 1f), c.InvViewProjection);
                return new Vector3(h.X, h.Y, h.Z) / h.W;
            }

            bool Sky(float d) => reversed ? d <= 0f : d >= 0.9999999f;

            for (int k = 0; k < instanceCount; k++)
            {
                int at = (first + k) * stride;
                if (at < 0 || at + stride > decalBytes.Length) break;
                Decal decal = MemoryMarshal.Read<Decal>(decalBytes.AsSpan(at));
                Vector3 centre = new(decal.Center.X, decal.Center.Y, decal.Center.Z);
                Vector3 ax = new(decal.AxisX.X, decal.AxisX.Y, decal.AxisX.Z);
                Vector3 ay = new(decal.AxisY.X, decal.AxisY.Y, decal.AxisY.Z);
                Vector3 az = new(decal.AxisZ.X, decal.AxisZ.Y, decal.AxisZ.Z);
                Vector3 ix = ax / MathF.Max(ax.LengthSquared(), 1e-8f);
                Vector3 iy = ay / MathF.Max(ay.LengthSquared(), 1e-8f);
                Vector3 iz = az / MathF.Max(az.LengthSquared(), 1e-8f);
                Vector3 facingAxis = az.LengthSquared() > 0f ? Vector3.Normalize(az) : Vector3.UnitY;

                // The screen rectangle round the box (the whole screen when it reaches behind the eye).
                float loX = 1f, loY = 1f, hiX = -1f, hiY = -1f;
                bool behind = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = centre + ax * ((i & 1) != 0 ? 0.5f : -0.5f) + ay * ((i & 2) != 0 ? 0.5f : -0.5f)
                        + az * ((i & 4) != 0 ? 0.5f : -0.5f);
                    Vector4 clip = Vector4.Transform(new Vector4(corner, 1f), c.ViewProjection);
                    if (clip.W <= 1e-4f) { behind = true; continue; }
                    float nx = clip.X / clip.W, ny = clip.Y / clip.W;
                    loX = MathF.Min(loX, nx); hiX = MathF.Max(hiX, nx);
                    loY = MathF.Min(loY, ny); hiY = MathF.Max(hiY, ny);
                }
                if (behind) { loX = loY = -1f; hiX = hiY = 1f; }
                int x0 = Math.Clamp((int)MathF.Floor((Math.Clamp(loX, -1f, 1f) * 0.5f + 0.5f) * width), 0, width - 1);
                int x1 = Math.Clamp((int)MathF.Ceiling((Math.Clamp(hiX, -1f, 1f) * 0.5f + 0.5f) * width), 0, width - 1);
                int y0 = Math.Clamp((int)MathF.Floor((0.5f - Math.Clamp(hiY, -1f, 1f) * 0.5f) * height), 0, height - 1);
                int y1 = Math.Clamp((int)MathF.Ceiling((0.5f - Math.Clamp(loY, -1f, 1f) * 0.5f) * height), 0, height - 1);
                if (hiX < -1f || loX > 1f || hiY < -1f || loY > 1f) continue;

                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int index = y * width + x;
                        float d = depth[index];
                        if (Sky(d)) continue;
                        Vector3 p = WorldAt(x, y, d);
                        Vector3 rel = p - centre;
                        float lx = Vector3.Dot(rel, ix), ly = Vector3.Dot(rel, iy), lz = Vector3.Dot(rel, iz);
                        if (MathF.Abs(lx) > 0.5f || MathF.Abs(ly) > 0.5f || MathF.Abs(lz) > 0.5f) continue;

                        // Surface normal from the neighbour nearer in depth on each axis.
                        int xl = Math.Max(x - 1, 0), xr = Math.Min(x + 1, width - 1);
                        int yu = Math.Max(y - 1, 0), yd = Math.Min(y + 1, height - 1);
                        float dl = depth[y * width + xl], dr = depth[y * width + xr];
                        float du = depth[yu * width + x], dd = depth[yd * width + x];
                        Vector3 across = MathF.Abs(dr - d) < MathF.Abs(dl - d) ? WorldAt(xr, y, dr) - p : p - WorldAt(xl, y, dl);
                        Vector3 down = MathF.Abs(dd - d) < MathF.Abs(du - d) ? WorldAt(x, yd, dd) - p : p - WorldAt(x, yu, du);
                        Vector3 n = Vector3.Cross(down, across);
                        n = n.LengthSquared() > 1e-24f ? Vector3.Normalize(n) : Vector3.Normalize(eye - p);
                        if (Vector3.Dot(n, eye - p) < 0f) n = -n;
                        float facing = Vector3.Dot(n, facingAxis);
                        float angleFade = Math.Clamp((facing - decal.Params.Y) / MathF.Max(decal.Params.X - decal.Params.Y, 1e-3f), 0f, 1f);
                        float depthFade = Math.Clamp((0.5f - MathF.Abs(lz)) * 6f, 0f, 1f);

                        float tr = 1f, tg = 1f, tb = 1f, ta = 1f;
                        if (texture)
                        {
                            int tx = Math.Clamp((int)((lx + 0.5f) * texWidth), 0, texWidth - 1);
                            int ty = Math.Clamp((int)((0.5f - ly) * texHeight), 0, texHeight - 1);
                            int t = (ty * texWidth + tx) * 4;
                            tr = texPixels[t + (bgrTexture ? 2 : 0)] / 255f;
                            tg = texPixels[t + 1] / 255f;
                            tb = texPixels[t + (bgrTexture ? 0 : 2)] / 255f;
                            ta = texPixels[t + 3] / 255f;
                        }
                        float alpha = Math.Clamp(ta * decal.Color.W * angleFade * depthFade, 0f, 1f);
                        if (alpha <= 0.002f) continue;
                        float r = tr * decal.Color.X, g = tg * decal.Color.Y, b = tb * decal.Color.Z;

                        int px = index * 4;
                        float db = bgra[px] / 255f, dg = bgra[px + 1] / 255f, dr8 = bgra[px + 2] / 255f;
                        if (mode == 0)
                        {
                            db *= 1f + (b - 1f) * alpha;
                            dg *= 1f + (g - 1f) * alpha;
                            dr8 *= 1f + (r - 1f) * alpha;
                        }
                        else if (mode == 1)
                        {
                            db += (b - db) * alpha;
                            dg += (g - dg) * alpha;
                            dr8 += (r - dr8) * alpha;
                        }
                        else
                        {
                            db += b * alpha;
                            dg += g * alpha;
                            dr8 += r * alpha;
                        }
                        bgra[px] = (byte)(Math.Clamp(db, 0f, 1f) * 255f + 0.5f);
                        bgra[px + 1] = (byte)(Math.Clamp(dg, 0f, 1f) * 255f + 0.5f);
                        bgra[px + 2] = (byte)(Math.Clamp(dr8, 0f, 1f) * 255f + 0.5f);
                    }
                }
            }
        }
    }
}
