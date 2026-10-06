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
        Vector4 uv = Tile(u0, v0, u1, v1);
        return ScriptMeshes.AddCube((int)mesh, new Vector3((float)x, (float)y, (float)z), (float)size,
            (int)faces & ScriptMeshes.AllFaces, Colour(r, g, b, 1), uv);
    }

    [PgslCommand("MeshAddCubeTiles", "MeshAddCubeTiles(mesh, x, y, z, size, faces, r, g, b, topU0, topV0, topU1, topV1, sideU0, sideV0, sideU1, sideV1, bottomU0, bottomV0, bottomU1, bottomV1) -> faces added",
        "MeshAddCube with a tile of its own for the top (+Y), the four sides and the bottom (-Y): a grass block, a log, a crate",
        "Meshes")]
    public static double MeshAddCubeTiles(double mesh, double x, double y, double z, double size, double faces,
        double r, double g, double b,
        double topU0, double topV0, double topU1, double topV1,
        double sideU0, double sideV0, double sideU1, double sideV1,
        double bottomU0, double bottomV0, double bottomU1, double bottomV1)
    {
        if (!Finite3(x, y, z) || !(size > 0) || !double.IsFinite(size)) return 0;
        return ScriptMeshes.AddCube((int)mesh, new Vector3((float)x, (float)y, (float)z), (float)size,
            (int)faces & ScriptMeshes.AllFaces, Colour(r, g, b, 1),
            Tile(topU0, topV0, topU1, topV1), Tile(sideU0, sideV0, sideU1, sideV1), Tile(bottomU0, bottomV0, bottomU1, bottomV1));
    }

    [PgslCommand("MeshAddQuad", "MeshAddQuad(mesh, x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3, nx, ny, nz, u0, v0, u1, v1, r, g, b, a) -> index",
        "Add a four-cornered face in one call: corners in order around it (turning like MeshAddTriangle), one normal, the texture rectangle u0, v0 (first corner) to u1, v1 (third), colour 0-255 and alpha 0-1. Returns the first corner's index, -1 when the mesh is full",
        "Meshes")]
    public static double MeshAddQuad(double mesh,
        double x0, double y0, double z0, double x1, double y1, double z1,
        double x2, double y2, double z2, double x3, double y3, double z3,
        double nx, double ny, double nz, double u0, double v0, double u1, double v1,
        double r, double g, double b, double a)
    {
        if (!Finite3(x0, y0, z0) || !Finite3(x1, y1, z1) || !Finite3(x2, y2, z2) || !Finite3(x3, y3, z3) || !Finite3(nx, ny, nz))
            return -1;
        return ScriptMeshes.AddQuad((int)mesh,
            new Vector3((float)x0, (float)y0, (float)z0), new Vector3((float)x1, (float)y1, (float)z1),
            new Vector3((float)x2, (float)y2, (float)z2), new Vector3((float)x3, (float)y3, (float)z3),
            new Vector3((float)nx, (float)ny, (float)nz), Tile(u0, v0, u1, v1), Colour(r, g, b, a));
    }

    [PgslCommand("MeshAddQuadColors", "MeshAddQuadColors(mesh, x0, y0, z0, x1, y1, z1, x2, y2, z2, x3, y3, z3, nx, ny, nz, u0, v0, u1, v1, r0, g0, b0, a0, r1, g1, b1, a1, r2, g2, b2, a2, r3, g3, b3, a3, flip) -> index",
        "MeshAddQuad with a colour (0-255) and alpha (0-1) for each corner, for baked light, shading or gradients; flip 1 splits the quad along the other diagonal. Returns the first corner's index, -1 when the mesh is full",
        "Meshes")]
    public static double MeshAddQuadColors(double mesh,
        double x0, double y0, double z0, double x1, double y1, double z1,
        double x2, double y2, double z2, double x3, double y3, double z3,
        double nx, double ny, double nz, double u0, double v0, double u1, double v1,
        double r0, double g0, double b0, double a0, double r1, double g1, double b1, double a1,
        double r2, double g2, double b2, double a2, double r3, double g3, double b3, double a3, double flip)
    {
        if (!Finite3(x0, y0, z0) || !Finite3(x1, y1, z1) || !Finite3(x2, y2, z2) || !Finite3(x3, y3, z3) || !Finite3(nx, ny, nz))
            return -1;
        return ScriptMeshes.AddQuad((int)mesh,
            new Vector3((float)x0, (float)y0, (float)z0), new Vector3((float)x1, (float)y1, (float)z1),
            new Vector3((float)x2, (float)y2, (float)z2), new Vector3((float)x3, (float)y3, (float)z3),
            new Vector3((float)nx, (float)ny, (float)nz), Tile(u0, v0, u1, v1),
            Colour(r0, g0, b0, a0), Colour(r1, g1, b1, a1), Colour(r2, g2, b2, a2), Colour(r3, g3, b3, a3),
            flip: double.IsFinite(flip) && flip >= 0.5);
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
        double yaw, string image) =>
        DrawMeshShader3D(mesh, string.Empty, x, y, z, sx, sy, sz, yaw, image);

    [PgslCommand("DrawMeshShader3D", "DrawMeshShader3D(mesh, shader, x, y, z, sx, sy, sz, yaw, image)",
        "Draw a script's mesh through a mesh Shader resource (empty for the engine's own shading), with this instance's ShaderSetParameter / ShaderSetVector values",
        "Meshes")]
    public static void DrawMeshShader3D(double mesh, string shader, double x, double y, double z, double sx, double sy, double sz,
        double yaw, string image)
    {
        IPgslDrawSurface surface = Draw;
        if (surface is null || !Finite3(x, y, z) || !Finite3(sx, sy, sz) || !double.IsFinite(yaw)) return;
        Matrix4x4 world = Matrix4x4.CreateScale((float)sx, (float)sy, (float)sz)
            * Matrix4x4.CreateRotationY((float)(yaw * Math.PI / 180))
            * Matrix4x4.CreateTranslation((float)x, (float)y, (float)z);
        PgslContext ctx = GetContext();
        ScriptMeshDrawOptions options = ctx?.MeshDrawOptions ?? default;
        if (!string.IsNullOrWhiteSpace(shader) && InstanceDrawAssets(ctx) is { } assets)
        {
            options.ShaderParameters = assets.ShaderParameters;
            options.ShaderResources = assets.ShaderResources;
        }
        surface.QueueScriptMesh3D((int)mesh, world, image ?? string.Empty, shader ?? string.Empty,
            ctx?.ImageBlend ?? Color.White, (float)(ctx?.DrawAlpha ?? 1), options);
    }

    [PgslCommand("DrawMeshSetShadows", "DrawMeshSetShadows(cast, receive)",
        "Whether this instance's later script-mesh draws cast shadows and are shadowed (both on by default)", "Meshes")]
    public static void DrawMeshSetShadows(bool cast, bool receive) => EditMeshOptions((ref ScriptMeshDrawOptions options) =>
    {
        options.NoCastShadow = !cast;
        options.NoReceiveShadow = !receive;
    });

    [PgslCommand("DrawMeshSetGlow", "DrawMeshSetGlow(amount)",
        "Add the mesh's own colours over its lighting in later script-mesh draws: 0 none (default), 1 fully self-lit (lamps, lava, screens)", "Meshes")]
    public static void DrawMeshSetGlow(double amount) => EditMeshOptions((ref ScriptMeshDrawOptions options) =>
        options.Glow = double.IsFinite(amount) ? (float)Math.Clamp(amount, 0, 16) : 0f);

    [PgslCommand("DrawMeshSetFog", "DrawMeshSetFog(enabled)", "Whether later script-mesh draws are fogged (on by default)", "Meshes")]
    public static void DrawMeshSetFog(bool enabled) => EditMeshOptions((ref ScriptMeshDrawOptions options) => options.NoFog = !enabled);

    [PgslCommand("DrawMeshSetCull", "DrawMeshSetCull(enabled)",
        "Whether later script-mesh draws hide faces seen from behind (on by default); off draws both sides (leaves, crossed plants)", "Meshes")]
    public static void DrawMeshSetCull(bool enabled) => EditMeshOptions((ref ScriptMeshDrawOptions options) => options.TwoSided = !enabled);

    [PgslCommand("DrawMeshSetTransparent", "DrawMeshSetTransparent(enabled)",
        "Blend later script-mesh draws as see-through even at full alpha (glass, water); off by default, when only alpha below 1 blends", "Meshes")]
    public static void DrawMeshSetTransparent(bool enabled) => EditMeshOptions((ref ScriptMeshDrawOptions options) => options.Transparent = enabled);

    [PgslCommand("DrawMeshResetState", "DrawMeshResetState()", "Put the script-mesh draw options back to their defaults", "Meshes")]
    public static void DrawMeshResetState() => EditMeshOptions((ref ScriptMeshDrawOptions options) => options = default);

    private delegate void MeshOptionsEdit(ref ScriptMeshDrawOptions options);

    private static void EditMeshOptions(MeshOptionsEdit edit)
    {
        PgslContext ctx = GetContext();
        if (ctx == null) return;
        ScriptMeshDrawOptions options = ctx.MeshDrawOptions;
        edit(ref options);
        ctx.MeshDrawOptions = options;
    }

    private static Genesis.Runtime.Rendering.ObjectDrawAssetEntry InstanceDrawAssets(PgslContext ctx)
    {
        var world = World;
        if (world is null || ctx == null || ctx.InstanceId < 1) return null;
        var entity = world.GetEntity(ctx.InstanceId);
        return world.IsAlive(entity) && Genesis.Runtime.Rendering.ObjectDrawAssetRegistry.TryGet(entity, out var assets) ? assets : null;
    }

    // All four 0 (or not numbers) means the whole texture. A single point (u0 = u1, v0 = v1) is kept:
    // one texel of an atlas, such as a far-off block's colour.
    private static Vector4 Tile(double u0, double v0, double u1, double v1) =>
        Finite3(u0, v0, u1) && double.IsFinite(v1) && (u0 != 0 || v0 != 0 || u1 != 0 || v1 != 0)
            ? new Vector4((float)u0, (float)v0, (float)u1, (float)v1)
            : new Vector4(0, 0, 1, 1);

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
