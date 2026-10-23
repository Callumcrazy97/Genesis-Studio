using System;
using System.Numerics;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scripting;

// Meshes placed once and drawn by the engine (StaticMeshLayer), and instance positions moved or read
// for a whole list of instances in one call.
public static partial class PgslCommands
{
    [PgslCommand("MeshStaticAdd", "MeshStaticAdd(mesh, shader, x, y, z, sx, sy, sz, yaw, image) -> id",
        "Place a script mesh in the 3D world once: the engine draws it every frame in every view it is inside (no Draw event call), with this instance's DrawMeshSet* options, colour and alpha as they are now; it stays until removed or the room ends. Rebuilding the mesh shows the new build. Returns its handle, 0 when the numbers are bad",
        "Meshes")]
    public static double MeshStaticAdd(double mesh, string shader, double x, double y, double z, double sx, double sy, double sz,
        double yaw, string image)
    {
        if (!double.IsFinite(mesh) || !ScriptMeshes.Exists((int)mesh) || !StaticWorld(x, y, z, sx, sy, sz, yaw, out Matrix4x4 world)) return 0;
        PgslContext ctx = GetContext();
        ScriptMeshDrawOptions options = ctx?.MeshDrawOptions ?? default;
        if (!string.IsNullOrWhiteSpace(shader) && InstanceDrawAssets(ctx) is { } assets)
        {
            options.ShaderParameters = assets.ShaderParameters;
            options.ShaderResources = assets.ShaderResources;
        }
        return StaticMeshLayer.Add((int)mesh, world, image ?? string.Empty, shader ?? string.Empty,
            ctx?.ImageBlend ?? System.Drawing.Color.White, (float)(ctx?.DrawAlpha ?? 1), options);
    }

    [PgslCommand("MeshStaticMove", "MeshStaticMove(id, x, y, z, sx, sy, sz, yaw) -> bool", "Move, scale or turn a placed mesh; false when it is not placed", "Meshes")]
    public static bool MeshStaticMove(double id, double x, double y, double z, double sx, double sy, double sz, double yaw) =>
        double.IsFinite(id) && StaticWorld(x, y, z, sx, sy, sz, yaw, out Matrix4x4 world) && StaticMeshLayer.Move((int)id, world);

    [PgslCommand("MeshStaticRemove", "MeshStaticRemove(id) -> bool", "Stop drawing a placed mesh (the mesh itself is kept)", "Meshes")]
    public static bool MeshStaticRemove(double id) => double.IsFinite(id) && StaticMeshLayer.Remove((int)id);

    [PgslCommand("MeshStaticClear", "MeshStaticClear()", "Remove every placed mesh", "Meshes")]
    public static void MeshStaticClear() => StaticMeshLayer.Clear();

    [PgslCommand("MeshStaticCount", "MeshStaticCount() -> number", "Meshes placed with MeshStaticAdd", "Meshes")]
    public static double MeshStaticCount() => StaticMeshLayer.Count;

    [PgslCommand("MeshStaticDrawnCount", "MeshStaticDrawnCount() -> number",
        "Placed meshes the last view drew (the rest were outside it)", "Meshes")]
    public static double MeshStaticDrawnCount() => StaticMeshLayer.LastDrawn;

    private static bool StaticWorld(double x, double y, double z, double sx, double sy, double sz, double yaw, out Matrix4x4 world)
    {
        world = Matrix4x4.Identity;
        if (!Finite3(x, y, z) || !Finite3(sx, sy, sz) || !double.IsFinite(yaw)) return false;
        world = Matrix4x4.CreateScale((float)sx, (float)sy, (float)sz)
            * Matrix4x4.CreateRotationY((float)(yaw * Math.PI / 180))
            * Matrix4x4.CreateTranslation((float)x, (float)y, (float)z);
        return true;
    }

    [PgslCommand("InstanceSetPositionsFromNumList", "InstanceSetPositionsFromNumList(instances, positions) -> number",
        "Move many instances in one call: instances is a number list of ids, positions a number list of x, y, z for each in turn (as InstanceSetX/Y/Z: the 3D pose and physics follow). Returns how many moved; ids that do not exist are skipped",
        "Instances")]
    public static double InstanceSetPositionsFromNumList(double instances, double positions)
    {
        if (NumRead(instances) is not { } ids || NumRead(positions) is not { } xyz || World is not { } world) return 0;
        PgslContext ctx = GetContext();
        int count = Math.Min(ids.Count, xyz.Count / 3), moved = 0;
        for (int i = 0; i < count; i++)
        {
            double x = xyz.Items[i * 3], y = xyz.Items[i * 3 + 1], z = xyz.Items[i * 3 + 2];
            if (!Finite3(x, y, z) || !TryTransform(ids.Items[i], out Entity entity, out EcsWorld _)) continue;
            PlaceInstance(world, entity, x, y, z, ctx);
            moved++;
        }
        return moved;
    }

    [PgslCommand("InstanceSetTransformsFromNumList", "InstanceSetTransformsFromNumList(instances, transforms) -> number",
        "Place, turn and size many instances in one call: transforms holds 9 numbers for each id in the instances number list (x, y, z, pitch, yaw, roll in degrees, sx, sy, sz), as InstanceSetX/Y/Z, InstanceSetRotation3D and InstanceSetScale3D (the 3D pose and physics follow). Returns how many changed; ids that do not exist, and scales of 0, are skipped",
        "Instances")]
    public static double InstanceSetTransformsFromNumList(double instances, double transforms)
    {
        if (NumRead(instances) is not { } ids || NumRead(transforms) is not { } values || World is not { } world) return 0;
        PgslContext ctx = GetContext();
        int count = Math.Min(ids.Count, values.Count / 9), changed = 0;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<double> v = values.Items.AsSpan(i * 9, 9);
            if (!Finite3(v[0], v[1], v[2]) || !Finite3(v[3], v[4], v[5]) || !Finite3(v[6], v[7], v[8])
                || Math.Abs(v[6]) < 1e-6 || Math.Abs(v[7]) < 1e-6 || Math.Abs(v[8]) < 1e-6
                || !TryTransform(ids.Items[i], out Entity entity, out EcsWorld _)) continue;
            ref Genesis.Runtime.ECS.Components.TransformComponent t = ref world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
            var ratio = new Vector3((float)v[6] / UnitScale(t.ScaleX), (float)v[7] / UnitScale(t.ScaleY), (float)v[8] / UnitScale(t.ScaleZ));
            t.X = (float)v[0]; t.Y = (float)v[1]; t.Z = (float)v[2];
            t.RotationX = (float)v[3]; t.RotationY = (float)v[4]; t.RotationZ = t.Rotation = (float)v[5];
            t.ScaleX = (float)v[6]; t.ScaleY = (float)v[7]; t.ScaleZ = (float)v[8];
            if (ctx != null && ctx.InstanceId == entity.Id)
            {
                ctx.X = v[0]; ctx.Y = v[1]; ctx.Z = v[2];
                ctx.ImageAngle = v[5]; ctx.ImageXScale = v[6]; ctx.ImageYScale = v[7];
            }
            // A sprite with a physics body follows as a move does; anything else takes the whole pose.
            if (world.Has<Genesis.Runtime.Scene.SpritePhysicsBindingComponent>(entity)) FollowMovedTransform(world, entity, ref t);
            else SynchronizeTransform(world, entity, Vector3.Abs(ratio));
            changed++;
        }
        return changed;
    }

    [PgslCommand("InstanceSetTintsFromNumList", "InstanceSetTintsFromNumList(instances, colours) -> number",
        "Tint many model instances in one call: colours holds r, g, b, a (0-1) for each id in the instances number list, as ModelSetTint. Returns how many changed; ids that are not model instances are skipped",
        "Instances")]
    public static double InstanceSetTintsFromNumList(double instances, double colours)
    {
        if (NumRead(instances) is not { } ids || NumRead(colours) is not { } rgba || World is not { } world) return 0;
        int count = Math.Min(ids.Count, rgba.Count / 4), changed = 0;
        for (int i = 0; i < count; i++)
        {
            double r = rgba.Items[i * 4], g = rgba.Items[i * 4 + 1], b = rgba.Items[i * 4 + 2], a = rgba.Items[i * 4 + 3];
            if (!Finite3(r, g, b) || !double.IsFinite(a) || !TryTransform(ids.Items[i], out Entity entity, out EcsWorld _)) continue;
            if (Genesis.Runtime.Modeling.ModelInstance.SetTint(world, entity, new Vector4((float)r, (float)g, (float)b, (float)a))) changed++;
        }
        return changed;
    }

    [PgslCommand("InstanceGetPositionsToNumList", "InstanceGetPositionsToNumList(instances, positions) -> number",
        "Read many instances' positions in one call: positions becomes x, y, z for each id in the instances number list (0, 0, 0 for one that does not exist). Returns how many exist",
        "Instances")]
    public static double InstanceGetPositionsToNumList(double instances, double positions)
    {
        if (NumRead(instances) is not { } ids || NumWrite(positions) is not { } xyz) return 0;
        if (!xyz.Resize(Math.Min(ids.Count * 3, MaxCollectionElements), MaxCollectionElements)) return 0;
        int count = xyz.Count / 3, found = 0;
        for (int i = 0; i < count; i++)
        {
            if (TryTransform(ids.Items[i], out Entity entity, out EcsWorld world))
            {
                ref Genesis.Runtime.ECS.Components.TransformComponent t = ref world.GetRef<Genesis.Runtime.ECS.Components.TransformComponent>(entity);
                xyz.Items[i * 3] = t.X; xyz.Items[i * 3 + 1] = t.Y; xyz.Items[i * 3 + 2] = t.Z;
                found++;
            }
            else xyz.Items[i * 3] = xyz.Items[i * 3 + 1] = xyz.Items[i * 3 + 2] = 0;
        }
        return found;
    }
}
