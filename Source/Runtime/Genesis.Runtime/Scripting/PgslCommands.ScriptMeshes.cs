using System;
using System.Drawing;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// Meshes a script builds (see ScriptMeshes): a voxel chunk is one mesh and one draw, not
// thousands of instances.
public static partial class PgslCommands
{
    [PgslCommand("MeshCreate", "MeshCreate() -> id", "A new empty mesh for the script to build (a voxel chunk, a procedural shape)", "Meshes")]
    public static double MeshCreate() => ScriptMeshes.Create();

    [PgslCommand("MeshClear", "MeshClear(mesh)", "Empty a mesh to build it again", "Meshes")]
    public static void MeshClear(double mesh) => ScriptMeshes.Clear((int)mesh);

    [PgslCommand("MeshDestroy", "MeshDestroy(mesh)", "Free a mesh", "Meshes")]
    public static void MeshDestroy(double mesh) => ScriptMeshes.Destroy((int)mesh);

    [PgslCommand("MeshAddVertex", "MeshAddVertex(mesh, x, y, z, nx, ny, nz, u, v, r, g, b, a) -> index",
        "Add a vertex (position, normal, texture coordinate, colour 0-255 and alpha 0-1); -1 when the mesh is full (65535)", "Meshes")]
    public static double MeshAddVertex(double mesh, double x, double y, double z, double nx, double ny, double nz,
        double u, double v, double r, double g, double b, double a) =>
        Finite3(x, y, z) && Finite3(nx, ny, nz) && double.IsFinite(u) && double.IsFinite(v)
            ? ScriptMeshes.AddVertex((int)mesh, new Vector3((float)x, (float)y, (float)z), new Vector3((float)nx, (float)ny, (float)nz),
                new Vector2((float)u, (float)v), Colour(r, g, b, a))
            : -1;

    [PgslCommand("MeshAddTriangle", "MeshAddTriangle(mesh, a, b, c) -> bool", "Add a triangle of three vertex indices (counter-clockwise from its front)", "Meshes")]
    public static bool MeshAddTriangle(double mesh, double a, double b, double c) =>
        ScriptMeshes.AddTriangle((int)mesh, (int)a, (int)b, (int)c);

    [PgslCommand("MeshAddCube", "MeshAddCube(mesh, x, y, z, size, faces, r, g, b, u0, v0, u1, v1) -> faces added",
        "Add a cube's faces centred on x, y, z: faces is a sum of 1 +X, 2 -X, 4 +Y, 8 -Y, 16 +Z, 32 -Z (63 all; a voxel adds only faces touching air); u0, v0 to u1, v1 is its tile of the texture atlas",
        "Meshes")]
    public static double MeshAddCube(double mesh, double x, double y, double z, double size, double faces,
        double r, double g, double b, double u0, double v0, double u1, double v1)
    {
        if (!Finite3(x, y, z) || !(size > 0) || !double.IsFinite(size)) return 0;
        Vector4 uv = Finite3(u0, v0, u1) && double.IsFinite(v1) && (u1 != u0 || v1 != v0)
            ? new Vector4((float)u0, (float)v0, (float)u1, (float)v1)
            : new Vector4(0, 0, 1, 1);
        return ScriptMeshes.AddCube((int)mesh, new Vector3((float)x, (float)y, (float)z), (float)size,
            (int)faces & ScriptMeshes.AllFaces, Colour(r, g, b, 1), uv);
    }

    [PgslCommand("MeshVertexCount", "MeshVertexCount(mesh) -> number", "Vertices in a mesh", "Meshes")]
    public static double MeshVertexCount(double mesh) => ScriptMeshes.VertexCount((int)mesh);

    [PgslCommand("MeshTriangleCount", "MeshTriangleCount(mesh) -> number", "Triangles in a mesh", "Meshes")]
    public static double MeshTriangleCount(double mesh) => ScriptMeshes.TriangleCount((int)mesh);

    [PgslCommand("DrawMesh3D", "DrawMesh3D(mesh, x, y, z, image)",
        "Draw a script's mesh in a Draw event of a 3D room at a position, textured by an Image (empty for none)", "Meshes")]
    public static void DrawMesh3D(double mesh, double x, double y, double z, string image) =>
        DrawMesh3DTransform(mesh, x, y, z, 1, 1, 1, 0, image);

    [PgslCommand("DrawMesh3DTransform", "DrawMesh3DTransform(mesh, x, y, z, sx, sy, sz, yaw, image)",
        "Draw a script's mesh scaled and turned about the vertical axis (degrees)", "Meshes")]
    public static void DrawMesh3DTransform(double mesh, double x, double y, double z, double sx, double sy, double sz,
        double yaw, string image)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !Finite3(x, y, z) || !Finite3(sx, sy, sz) || !double.IsFinite(yaw)) return;
        Matrix4x4 world = Matrix4x4.CreateScale((float)sx, (float)sy, (float)sz)
            * Matrix4x4.CreateRotationY((float)(yaw * Math.PI / 180))
            * Matrix4x4.CreateTranslation((float)x, (float)y, (float)z);
        PgslContext ctx = GetContext();
        surface.QueueScriptMesh3D((int)mesh, world, image ?? string.Empty, ctx?.ImageBlend ?? Color.White, (float)(ctx?.DrawAlpha ?? 1));
    }

    [PgslCommand("InstanceSetMeshCollider", "InstanceSetMeshCollider(id, mesh) -> bool",
        "Give an instance a fixed collider of a script's mesh (rebuilt by calling again after the mesh changes)", "Meshes")]
    public static bool InstanceSetMeshCollider(double id, double mesh)
    {
        var world = ActiveGameContext?.World;
        if (world == null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        var entity = world.GetEntity((int)id);
        if (!world.IsAlive(entity) || !ScriptMeshes.TryGetGeometry((int)mesh, out Vector3[] positions, out int[] indices)) return false;
        if (world.Has<RigidBodyComponent>(entity))
        {
            ref RigidBodyComponent existing = ref world.GetRef<RigidBodyComponent>(entity);
            if (existing.RegistrationId != 0) PhysicsWorld?.UnregisterEntity(world, entity, ref existing);
        }
        if (!world.Has<Transform3DComponent>(entity))
        {
            var transform = Transform3DComponent.Default;
            if (world.Has<Genesis.Runtime.ECS.Components.TransformComponent>(entity))
            {
                var flat = world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
                transform.Position = new Vector3(flat.X, flat.Y, flat.Z);
            }
            world.Set(entity, transform);
        }
        world.Set(entity, new MeshColliderComponent { Vertices = positions, Indices = indices, Scale = Vector3.One });
        RigidBodyComponent body = RigidBodyComponent.StaticBox(Vector3.One);
        body.Shape = CollisionShape.Mesh;
        world.Set(entity, body);
        if (PhysicsWorld is { } physics)
            physics.RegisterEntity(world, entity, ref world.GetRef<RigidBodyComponent>(entity), ref world.GetRef<Transform3DComponent>(entity));
        return true;
    }

    private static Vector4 Colour(double r, double g, double b, double a) => new(
        (float)Math.Clamp(double.IsFinite(r) ? r / 255.0 : 1, 0, 1),
        (float)Math.Clamp(double.IsFinite(g) ? g / 255.0 : 1, 0, 1),
        (float)Math.Clamp(double.IsFinite(b) ? b / 255.0 : 1, 0, 1),
        (float)Math.Clamp(double.IsFinite(a) ? a : 1, 0, 1));
}
