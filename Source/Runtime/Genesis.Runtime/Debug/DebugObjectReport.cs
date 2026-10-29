using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Genesis.Rendering.D3dMath;
using Genesis.Runtime.Culling;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scene;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Spatial;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Rendering;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Debugger;

/// <summary>
/// What the debug screen says about one instance: what it is (its Object, model, materials and
/// shaders, body), where it is, the level of detail it is drawn at, and whether it is drawn this
/// frame and if not why: hidden, deactivated, outside the camera's view, behind an occluder, too
/// small on screen, past the far plane, in a hidden model layer, outside a 2D room's views. The
/// reasons are worked out with the same tests the draw uses (Visibility, ModelLodView), from the
/// frame's camera.
/// </summary>
public sealed class DebugObjectReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public int Instance { get; private set; }
    public string Name { get; private set; } = "";
    /// <summary>True when the instance is drawn in this frame.</summary>
    public bool Drawn { get; private set; }
    /// <summary>Why it is not drawn (empty when it is), in a few words.</summary>
    public string Reason { get; private set; } = "";
    /// <summary>The level of detail it is drawn at; -1 when it has no model or is not drawn.</summary>
    public int LodLevel { get; private set; } = -1;
    /// <summary>The report's lines: a label and what it says.</summary>
    public IReadOnlyList<(string Label, string Value)> Lines => _lines;

    private readonly List<(string, string)> _lines = new();

    private void Add(string label, string value) => _lines.Add((label, value ?? ""));

    private static string N(float value) => value.ToString("0.##", Invariant);
    private static string V(Vector3 value) => $"({N(value.X)}, {N(value.Y)}, {N(value.Z)})";

    /// <summary>The report as text, one "Label: value" line each (Engine.Debug.Inspect.Report).</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        foreach ((string label, string value) in _lines) text.Append(label).Append(": ").Append(value).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The report on an instance of the scene, seen through <paramref name="projection"/>; null when it is not alive.</summary>
    internal static DebugObjectReport For(RuntimeScene scene, ScriptHostSystem scripts, int instanceId, in DebugProjection projection)
    {
        EcsWorld world = scene?.World;
        if (world == null || instanceId <= 0) return null;
        Entity entity = world.GetEntity(instanceId);
        if (entity.IsNull || !world.IsAlive(entity)) return null;
        var report = new DebugObjectReport { Instance = instanceId };
        report.Build(world, entity, scripts, projection, scene);
        return report;
    }

    /// <summary>The instance's name as the screen lists it: its Object, or its script, or its image.</summary>
    internal static string NameOf(EcsWorld world, Entity entity, ScriptHostSystem scripts)
    {
        if (ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets))
        {
            if (!string.IsNullOrWhiteSpace(assets.InstanceName)) return assets.InstanceName;
            if (!string.IsNullOrWhiteSpace(assets.Prefab)) return ResourceNames.FormatName(assets.Prefab);
        }
        if (scripts?.FindBehaviorForEntity(entity) is PgslBehavior pgsl && !string.IsNullOrWhiteSpace(pgsl.ScriptName)) return pgsl.ScriptName;
        if (world.Has<ModelRendererComponent>(entity) && world.GetRef<ModelRendererComponent>(entity).ModelAsset is { Length: > 0 } model)
            return ResourceNames.FormatName(model);
        return assets != null && !string.IsNullOrWhiteSpace(assets.Image) ? ResourceNames.FormatName(assets.Image) : $"Instance {entity.Id}";
    }

    private void Build(EcsWorld world, Entity entity, ScriptHostSystem scripts, in DebugProjection projection, RuntimeScene scene)
    {
        string project = PgslCommands.ProjectPath;
        bool hasAssets = ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets);
        Name = NameOf(world, entity, scripts);
        Add("Instance", $"{Name} #{entity.Id}");
        string objectType = hasAssets && !string.IsNullOrWhiteSpace(assets.Prefab) ? ResourceNames.FormatName(assets.Prefab) : "(none: made by the engine or a script)";
        Add("Object", objectType);
        if (scripts?.FindBehaviorForEntity(entity) is PgslBehavior behavior) Add("Script", behavior.ScriptName ?? "PGSL");

        bool has3D = world.Has<TransformComponent>(entity) || world.Has<Transform3DComponent>(entity);
        Vector3 position = Vector3.Zero;
        if (world.Has<TransformComponent>(entity))
        {
            ref TransformComponent transform = ref world.GetRef<TransformComponent>(entity);
            position = new Vector3(transform.X, transform.Y, transform.Z);
            Add("Position", V(position));
            Add("Rotation / scale", $"{V(new Vector3(transform.RotationX, transform.RotationY, transform.RotationZ == 0 ? transform.Rotation : transform.RotationZ))} / {V(new Vector3(transform.ScaleX, transform.ScaleY, transform.ScaleZ))}");
        }
        else if (world.Has<Transform3DComponent>(entity))
        {
            position = world.GetRef<Transform3DComponent>(entity).Position;
            Add("Position", V(position));
        }
        if (!has3D) Add("Position", "(none: not placed in the room)");

        if (world.Has<RigidBodyComponent>(entity))
        {
            ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
            string kind = body.IsSensor ? "trigger" : body.Motion.ToString().ToLowerInvariant();
            Add("Body", $"{body.Shape} {kind}, size {V(body.Size)}, layer {(char)('A' + Math.Min(body.CollisionLayer, (byte)25))}"
                + (body.Collision ? "" : ", collision off") + (body.Dormant ? ", dormant (far from the camera)" : ""));
        }
        else if (world.Has<PhysicsComponent>(entity))
            Add("Body", world.GetRef<PhysicsComponent>(entity).Solid ? "2D solid" : "2D motion (not solid)");
        else Add("Body", "none");

        if (world.Has<EntityLifecycleComponent>(entity) && !world.GetRef<EntityLifecycleComponent>(entity).Enabled)
        {
            NotDrawn("deactivated (InstanceDeactivate)");
            return;
        }

        if (projection.Is2D) Build2D(world, entity, hasAssets ? assets : null, project, projection);
        else Build3D(world, entity, hasAssets ? assets : null, project, projection, scene);
    }

    private void NotDrawn(string reason)
    {
        Drawn = false;
        Reason = reason;
        Add("Drawn", "no: " + reason);
    }

    private void IsDrawn(string detail)
    {
        Drawn = true;
        Reason = "";
        Add("Drawn", string.IsNullOrEmpty(detail) ? "yes" : "yes, " + detail);
    }

    private void Build2D(EcsWorld world, Entity entity, ObjectDrawAssetEntry assets, string project, in DebugProjection projection)
    {
        if (assets?.Image is { Length: > 0 } image) Add("Image", image);
        if (assets?.Shader is { Length: > 0 } shader) Add("Shader", shader);
        if (!world.Has<Draw2DComponent>(entity)) { NotDrawn("it has nothing to draw in 2D"); return; }
        ref Draw2DComponent draw = ref world.GetRef<Draw2DComponent>(entity);
        Add("Depth", N(draw.Depth));
        if (!draw.Visible) { NotDrawn("invisible (visible = false)"); return; }
        if (assets == null) { NotDrawn("no image or Object drawing registered"); return; }
        if (assets.Is3D == true) { NotDrawn("a 3D Object in a 2D room"); return; }
        if (string.IsNullOrWhiteSpace(assets.Image)) { NotDrawn("no image"); return; }
        if (assets.SpriteAlpha <= 0f) { NotDrawn("fully see-through (image alpha 0)"); return; }
        RectangleF box = SpriteCollisionBounds.ForEntity(world, entity, project);
        Add("Hitbox", $"{N(box.Left)}, {N(box.Top)} to {N(box.Right)}, {N(box.Bottom)}");
        for (int index = 0; index < projection.Views2D.Count; index++)
        {
            RoomRenderSubsystem.View2D view = projection.Views2D[index];
            Vector2 a = DebugProjection.Map(view, box.Left, box.Top), b = DebugProjection.Map(view, box.Right, box.Bottom);
            RectangleF onScreen = RectangleF.FromLTRB(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
            if (onScreen.IntersectsWith(view.Port)) { IsDrawn(projection.Views2D.Count > 1 ? $"in view {index}" : ""); return; }
        }
        NotDrawn("outside the view (off screen)");
    }

    private void Build3D(EcsWorld world, Entity entity, ObjectDrawAssetEntry assets, string project, in DebugProjection projection, RuntimeScene scene)
    {
        if (!world.Has<Draw3DComponent>(entity))
        {
            if (world.Has<MeshDrawComponent>(entity) || world.Has<LodMeshComponent>(entity)) BuildEngineMesh(world, entity, projection);
            else NotDrawn("it has nothing to draw in 3D");
            return;
        }
        ref Draw3DComponent draw = ref world.GetRef<Draw3DComponent>(entity);
        Add("Shadows", $"{(draw.CastShadows ? "casts" : "casts none")}, {(draw.ReceiveShadows ? "receives" : "receives none")}");
        if (!draw.Visible) { NotDrawn("invisible (visible = false)"); return; }
        if (assets?.Is3D == false) { NotDrawn("a 2D Object in a 3D room"); return; }
        if (assets?.Shader is { Length: > 0 } objectShader) Add("Object shader", objectShader + (string.IsNullOrEmpty(assets.ShaderVariant) ? "" : " (" + assets.ShaderVariant + ")"));
        if (draw.Procedural) { IsDrawn("a script's own meshes (procedural)"); return; }
        if (!world.Has<ModelRendererComponent>(entity) || string.IsNullOrWhiteSpace(world.GetRef<ModelRendererComponent>(entity).ModelAsset))
        {
            Add("Model", assets?.Image is { Length: > 0 } cube ? $"none: an image cube ({cube})" : "none");
            IsDrawn("");
            return;
        }

        ref ModelRendererComponent model = ref world.GetRef<ModelRendererComponent>(entity);
        Add("Model", model.ModelAsset + (string.IsNullOrWhiteSpace(model.MaterialOverride) ? "" : $" (material image {model.MaterialOverride})"));
        GModelAsset asset = ObjectDrawPass.Models.LoadAsset(project, model.ModelAsset);
        if (asset == null || !asset.HasRenderableMeshes) { NotDrawn("its model was not found or has no meshes"); return; }
        if (asset.ImportRequired) { NotDrawn("its model needs importing"); return; }
        int[] levels = asset.Meshes.Where(mesh => mesh != null).Select(mesh => mesh.Lod).Distinct().OrderBy(level => level).ToArray();
        Add("Meshes", $"{asset.Meshes.Count}, authored levels of detail {string.Join(", ", levels)}");
        var materials = asset.Materials ?? new List<GModelMaterial>();
        Add("Materials", materials.Count == 0 ? "none (default grey)" : string.Join("; ", materials.Take(6).Select(material =>
            material.Name + (string.IsNullOrWhiteSpace(material.Shader) ? "" : " [shader " + material.Shader + "]")
            + (string.IsNullOrWhiteSpace(material.AlbedoTexture) ? "" : " (" + material.AlbedoTexture + ")")))
            + (materials.Count > 6 ? $"; and {materials.Count - 6} more" : ""));
        if (ModelLayers.IsOverlay(model.ModelLayer))
        {
            Add("Model layer", model.ModelLayer.ToString(Invariant));
            if (!ModelLayers.IsVisible(model.ModelLayer)) { NotDrawn($"model layer {model.ModelLayer} is hidden"); return; }
        }

        // The draw's own bounds and tests: the model's box through its matrix, the camera's frustum.
        Matrix4x4 matrix = RuntimeModelRenderSystem.TransformMatrix(world.GetRef<TransformComponent>(entity), model);
        bool bounded = ObjectDrawPass.Models.TryGetBounds(project, model.ModelAsset, out Vector3 min, out Vector3 max, includePivot: true);
        Vector3 centre = bounded ? Vector3.Transform((min + max) * 0.5f, matrix) : matrix.Translation;
        float radius = bounded ? Vector3.Distance(min, max) * 0.5f * MatrixScaleHelper.MaxScale(matrix) : 1.2f;
        float distance = Vector3.Distance(projection.Eye, centre);
        Add("Bounds", $"centre {V(centre)}, radius {N(radius)}, {N(distance)} m from the camera");
        if (!projection.Has3D) { NotDrawn("no 3D camera"); return; }

        Camera3D camera = scene.Camera3D;
        float projectionScale = MathF.Abs(camera.ProjectionMatrix.M22);
        float screenSize = radius * projectionScale / MathF.Max(distance, MathF.Max(radius, 0.01f));
        bool sizeLod = model.LodPolicy == 0 && ModelLodView.Enabled && camera.ProjectionMatrix.M44 == 0f && projectionScale > 1e-6f;
        int viewLevel = sizeLod ? ModelLodView.LevelForSize(screenSize) : 0;
        string policy = model.LodPolicy > 0 ? $"pinned to level {model.LodPolicy}" : model.LodPolicy < 0 ? "always full detail" : "by size on screen";
        Add("Screen size", $"{N(screenSize * 100f)}% of the screen's height (detail {policy})");

        if (RenderAutoState.FrustumCulling && !ObjectDrawPass.IsPosedByAnimation(world, entity) && !LightProbes.KeepsForCapture(centre, radius))
        {
            switch (Visibility.Test(new Frustum(projection.ViewProjection), projection.ViewProjection, centre, radius, RenderAutoState.OcclusionCulling))
            {
                case Visibility.Verdict.OutsideView:
                    NotDrawn(distance - radius > camera.FarPlane ? $"past the camera's far plane ({N(camera.FarPlane)} m)" : "outside the camera's view (frustum culled)");
                    return;
                case Visibility.Verdict.Hidden:
                    NotDrawn("behind an occluder (occlusion culled)");
                    return;
            }
        }
        if (bounded && sizeLod && viewLevel < 0)
        {
            NotDrawn($"too small on screen (under {N(ModelLodView.CullSize * 100f)}% of its height; Engine.Rendering.Detail.CullSize)");
            return;
        }
        int active = RuntimeModelRenderSystem.ResolveLodLevel(asset, Math.Max(Math.Max(0, viewLevel), Math.Max(0, model.LodPolicy)));
        bool authored = levels.Contains(active) && levels.Length > 1;
        LodLevel = authored ? active : Math.Max(0, viewLevel);
        Add("Level of detail", authored ? $"{active} (authored)" : viewLevel > 0 ? $"{viewLevel} (simplified automatically)" : "0 (full detail)");
        IsDrawn($"level of detail {LodLevel}");
    }

    // An engine mesh (a script's or a system's), drawn through the visibility system's verdict.
    private void BuildEngineMesh(EcsWorld world, Entity entity, in DebugProjection projection)
    {
        Add("Model", world.Has<LodMeshComponent>(entity) ? "engine mesh with levels of detail" : "engine mesh");
        if (world.Has<VisibilityComponent>(entity))
        {
            VisibilityResult result = world.GetRef<VisibilityComponent>(entity).LastResult;
            switch (result)
            {
                case VisibilityResult.FrustumCulled: NotDrawn("outside the camera's view (frustum culled)"); return;
                case VisibilityResult.DistanceCulled: NotDrawn("past its draw distance (distance culled)"); return;
                case VisibilityResult.SubPixelCulled: NotDrawn("smaller than a pixel (sub-pixel culled)"); return;
                case VisibilityResult.Occluded: NotDrawn("behind an occluder (occlusion culled)"); return;
                case VisibilityResult.Disabled: NotDrawn("its visibility is turned off"); return;
            }
        }
        IsDrawn("");
    }
}
