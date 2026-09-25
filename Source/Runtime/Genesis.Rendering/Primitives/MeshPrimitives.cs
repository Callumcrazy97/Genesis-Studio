using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Rendering.Primitives
{
    // Static mesh geometry for built-in shapes.
    // All meshes use the MeshVertex layout: Position(12) + Color(16) + Normal(12) = 40 bytes.
    // Ported from EngineTest MeshData.cs (SharpDX) — geometry and indices are identical.
    internal static class MeshPrimitives
    {
        // ── Cube ─────────────────────────────────────────────────────────────────

        // 8-vertex cube centred at origin, half-extent 1. Normal = normalised position.
        // Shared vertices means normals are interpolated across faces — fine for basic lighting.
        public static MeshVertex[] CreateCube(Vector4 color)
        {
            Vector3[] positions =
            {
                new Vector3(-1f,  1f, -1f), // 0
                new Vector3( 1f,  1f, -1f), // 1
                new Vector3( 1f, -1f, -1f), // 2
                new Vector3(-1f, -1f, -1f), // 3
                new Vector3(-1f,  1f,  1f), // 4
                new Vector3( 1f,  1f,  1f), // 5
                new Vector3( 1f, -1f,  1f), // 6
                new Vector3(-1f, -1f,  1f), // 7
            };
            var verts = new MeshVertex[8];
            for (int i = 0; i < 8; i++)
                verts[i] = new MeshVertex { Position = positions[i], Color = color, Normal = Vector3.Normalize(positions[i]) };
            return verts;
        }

        public static readonly ushort[] CubeIndices =
        {
            // Front (-Z)
            0, 1, 2,  0, 2, 3,
            // Back  (+Z)
            5, 4, 7,  5, 7, 6,
            // Left  (-X)
            4, 0, 3,  4, 3, 7,
            // Right (+X)
            1, 5, 6,  1, 6, 2,
            // Top   (+Y)
            4, 5, 1,  4, 1, 0,
            // Bottom(-Y)
            3, 2, 6,  3, 6, 7,
        };

        // ── Floor quad ───────────────────────────────────────────────────────────

        // Flat unit-extent quad at Y=0, normal pointing up. Scale via world matrix.
        public static MeshVertex[] CreateFloorQuad(float halfExtent = 1f)
        {
            var gray = new Vector4(0.32f, 0.34f, 0.38f, 1f);
            return new[]
            {
                new MeshVertex { Position = new Vector3(-halfExtent, 0f, -halfExtent), Color = gray, Normal = Vector3.UnitY },
                new MeshVertex { Position = new Vector3( halfExtent, 0f, -halfExtent), Color = gray, Normal = Vector3.UnitY },
                new MeshVertex { Position = new Vector3( halfExtent, 0f,  halfExtent), Color = gray, Normal = Vector3.UnitY },
                new MeshVertex { Position = new Vector3(-halfExtent, 0f,  halfExtent), Color = gray, Normal = Vector3.UnitY },
            };
        }

        // Winding for +Y normal when viewed from above (CW in DX LH).
        public static readonly ushort[] QuadIndices = { 0, 2, 1,  0, 3, 2 };

        // ── Sun billboard quad ────────────────────────────────────────────────────

        // Unit quad in the XY plane, facing +Z. Transform with CreateBillboard before submit.
        public static MeshVertex[] CreateSunQuad(float halfExtent = 1f)
        {
            var sun = new Vector4(1f, 0.92f, 0.55f, 1f);
            return new[]
            {
                new MeshVertex { Position = new Vector3(-halfExtent, -halfExtent, 0f), Color = sun, Normal = Vector3.UnitZ },
                new MeshVertex { Position = new Vector3( halfExtent, -halfExtent, 0f), Color = sun, Normal = Vector3.UnitZ },
                new MeshVertex { Position = new Vector3( halfExtent,  halfExtent, 0f), Color = sun, Normal = Vector3.UnitZ },
                new MeshVertex { Position = new Vector3(-halfExtent,  halfExtent, 0f), Color = sun, Normal = Vector3.UnitZ },
            };
        }
        // Reuses QuadIndices above (same winding)
    }
}
