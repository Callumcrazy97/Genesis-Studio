using System;
using System.Numerics;
using Genesis.Shared.ECS.Components;

namespace Genesis.Physics;

/// <summary>
/// Issue 7 (declarative physics authoring). Maps the three authoring fields described in the
/// terrain/rendering fix plan and Documentation/Physics_Editor_Design.md §9 —
/// <c>Physics</c> (a named physics asset, resolved via <see cref="PhysicsAssetCatalog"/>),
/// <c>RigidBody</c> (an explicit collision shape, optional — falls back to the asset's default
/// shape when blank), and <c>PhysicsType</c> (how the object participates in the simulation) —
/// onto a ready-to-attach <see cref="RigidBodyComponent"/>.
///
/// Rule from the plan: "<c>Physics</c> present ⇒ the object joins simulation; absent ⇒ it
/// doesn't. Clean on/off." <see cref="TryBuildRigidBody"/> enforces exactly that: it returns
/// <c>false</c> (and a default component) whenever the asset name is null/blank, and otherwise
/// always returns <c>true</c> with a populated component.
///
/// This class only builds the component — it does not decide *whether* to attach it to an
/// entity, and it does not implement the WaterBody/Trigger spawn-routing behavior (no solid
/// collider for WaterBody, suppressed collision response for Trigger/Sensor in the Bepu narrow
/// phase). Both of those are <c>PhysicsType</c>-routing concerns layered on top of this by the
/// caller (see the "PhysicsType routing" task in the terrain/rendering fix plan).
/// </summary>
public static class PhysicsDeclarativeBinding
{
    /// <summary>
    /// Resolves the named physics asset and maps it (plus the explicit shape/type strings) onto
    /// a <see cref="RigidBodyComponent"/>.
    /// </summary>
    /// <param name="physicsAssetName">
    /// The authored <c>Physics</c> field — a physics asset name resolved via
    /// <see cref="PhysicsAssetCatalog.LoadPresetByName"/> (built-in preset, user preset under
    /// <c>Physics/Presets</c>, or <c>&lt;name&gt;.physics.json</c>). Null/blank means "no
    /// physics" — the object stays out of the simulation entirely.
    /// </param>
    /// <param name="rigidBodyShape">
    /// The authored <c>RigidBody</c> field — an explicit shape name ("Box", "Sphere", "Capsule",
    /// "Cylinder", "Mesh", "ConvexHull"). Blank/unrecognized falls back to the resolved asset's
    /// own default shape.
    /// </param>
    /// <param name="physicsTypeName">
    /// The authored <c>PhysicsType</c> field ("Object", "Dynamic", "Static", "Character",
    /// "WaterBody", "Trigger", "Sensor"). Blank/unrecognized falls back to <see cref="PhysicsType.Object"/>.
    /// </param>
    /// <param name="projectPath">Project root, for resolving user-authored physics assets. May be null to restrict resolution to built-in presets.</param>
    /// <param name="sizeHint">
    /// Half-extents (box) / radius-bearing vector (sphere/capsule, matching <see cref="RigidBodyComponent.Size"/>
    /// conventions) describing the object's authored footprint — this binder does not infer size
    /// from meshes/sprites, that's the caller's job.
    /// </param>
    /// <param name="component">The mapped component, ready to attach to an entity. Default when this method returns <c>false</c>.</param>
    /// <param name="resolvedType">The parsed <see cref="PhysicsType"/>, returned even when this method returns <c>false</c> isn't meaningful (defaults to <see cref="PhysicsType.Object"/>) since there's nothing to route in that case.</param>
    /// <returns><c>false</c> when <paramref name="physicsAssetName"/> is null/blank (no physics); otherwise <c>true</c>.</returns>
    public static bool TryBuildRigidBody(
        string? physicsAssetName,
        string? rigidBodyShape,
        string? physicsTypeName,
        string? projectPath,
        Vector3 sizeHint,
        out RigidBodyComponent component,
        out PhysicsType resolvedType)
    {
        component = default;
        resolvedType = PhysicsType.Object;

        if (string.IsNullOrWhiteSpace(physicsAssetName))
            return false;

        // LoadPresetByName already checks built-in presets first, so this covers both the
        // built-in and user-asset cases and never throws (falls back to PhysicsScenePresets.Default()).
        PhysicsSceneConfig asset = PhysicsAssetCatalog.LoadPresetByName(projectPath ?? string.Empty, physicsAssetName);

        resolvedType = ParsePhysicsType(physicsTypeName);
        CollisionShape shape = ParseShape(rigidBodyShape, asset.Shape);
        PhysicsMotionType motion = ResolveMotion(resolvedType, asset.BodyType);

        bool isSensor = asset.IsSensor || resolvedType is PhysicsType.Trigger or PhysicsType.Sensor;
        bool lockRotation = asset.LockRotation || resolvedType == PhysicsType.Character;
        bool useGravity = motion != PhysicsMotionType.Static && asset.GravityScale != 0f;

        var flags = RigidBodyFlags.Collision;
        if (useGravity) flags |= RigidBodyFlags.UseGravity;
        if (lockRotation) flags |= RigidBodyFlags.LockRotation;
        if (isSensor) flags |= RigidBodyFlags.Sensor;

        int collisionLayer = Math.Clamp(asset.CollisionLayer, 0, 6);
        bool[][] layerMatrix = asset.CollisionLayerMatrix ?? PhysicsSceneConfig.CreateDefaultCollisionLayerMatrix();
        uint collisionMask = 0;
        bool[]? layerRow = collisionLayer < layerMatrix.Length ? layerMatrix[collisionLayer] : null;
        for (int layer = 0; layer < 7; layer++)
            if (layerRow is null || layer >= layerRow.Length || layerRow[layer]) collisionMask |= 1u << layer;

        component = new RigidBodyComponent
        {
            Shape             = (Genesis.Shared.ECS.Components.CollisionShape)shape,
            Motion            = (Genesis.Shared.ECS.Components.PhysicsMotionType)motion,
            Size              = sizeHint,
            Mass              = MathF.Max(0.001f, (float)asset.Density),
            Weight            = 0f, // falls back to PhysicsWorld.DefaultWeight; asset has no direct "Weight" analogue
            Friction          = (float)asset.Friction,
            Restitution       = (float)asset.Restitution,
            GravityScale      = asset.GravityScale,
            SpeculativeMargin = shape is CollisionShape.Capsule or CollisionShape.Cylinder ? 0.15f : 0.06f,
            Flags             = flags,
            RegistrationId    = 0,
            CollisionLayer    = (byte)collisionLayer,
            CollisionMask     = collisionMask,
        };

        return true;
    }

    private static PhysicsMotionType ResolveMotion(PhysicsType physicsType, PhysicsBodyKind assetBodyType)
    {
        return physicsType switch
        {
            PhysicsType.Dynamic => PhysicsMotionType.Dynamic,
            PhysicsType.Static => PhysicsMotionType.Static,
            PhysicsType.Character => PhysicsMotionType.Dynamic,
            // WaterBody/Trigger/Sensor still get a motion type here in case the caller chooses to
            // attach a collider for them (e.g. a solid-walled trigger volume); the no-collider
            // WaterBody and contact-suppressed Trigger/Sensor behaviors are routing concerns.
            _ => assetBodyType switch
            {
                PhysicsBodyKind.Static => PhysicsMotionType.Static,
                PhysicsBodyKind.Kinematic => PhysicsMotionType.Kinematic,
                PhysicsBodyKind.Ragdoll => PhysicsMotionType.Dynamic,
                _ => PhysicsMotionType.Dynamic,
            },
        };
    }

    private static CollisionShape ParseShape(string? rigidBodyShape, PhysicsBodyShape assetDefault)
    {
        if (!string.IsNullOrWhiteSpace(rigidBodyShape))
        {
            switch (rigidBodyShape.Trim().ToLowerInvariant())
            {
                case "box": return CollisionShape.Box;
                case "sphere": return CollisionShape.Sphere;
                case "capsule": return CollisionShape.Capsule;
                case "cylinder": return CollisionShape.Cylinder;
                case "mesh": return CollisionShape.Mesh;
                case "convexhull":
                case "convex hull":
                case "hull": return CollisionShape.ConvexHull;
            }
        }

        return assetDefault switch
        {
            PhysicsBodyShape.Box => CollisionShape.Box,
            PhysicsBodyShape.Sphere => CollisionShape.Sphere,
            PhysicsBodyShape.Capsule => CollisionShape.Capsule,
            PhysicsBodyShape.Cylinder => CollisionShape.Cylinder,
            PhysicsBodyShape.Mesh => CollisionShape.Mesh,
            _ => CollisionShape.Box,
        };
    }

    private static PhysicsType ParsePhysicsType(string? physicsTypeName)
    {
        if (string.IsNullOrWhiteSpace(physicsTypeName))
            return PhysicsType.Object;

        return physicsTypeName.Trim().ToLowerInvariant() switch
        {
            "object" => PhysicsType.Object,
            "dynamic" => PhysicsType.Dynamic,
            "static" => PhysicsType.Static,
            "character" => PhysicsType.Character,
            "waterbody" or "water body" or "water" => PhysicsType.WaterBody,
            "trigger" => PhysicsType.Trigger,
            "sensor" => PhysicsType.Sensor,
            _ => PhysicsType.Object,
        };
    }
}
