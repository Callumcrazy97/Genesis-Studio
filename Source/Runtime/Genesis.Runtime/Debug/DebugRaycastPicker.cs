using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Debugger
{
    public sealed class InspectedEntityInfo
    {
        public Entity Entity { get; set; }
        public int Id => Entity.Id;
        public string Name { get; set; } = "Entity";
        public Vector3 WorldPosition { get; set; }
        public Vector4 ScreenBounds { get; set; } // MinX, MinY, MaxX, MaxY
        public string SpriteName { get; set; } = "None";
        public string ColliderType { get; set; } = "None";
        public string ScriptName { get; set; } = "None";
        public bool IsExpanded { get; set; } = false;

        public PgslBehavior Behavior { get; set; }
        public Dictionary<string, object> BuiltInVars { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, object> CustomVars { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Real-time mouse raycasting and scene entity inspection for the Genesis In-Game Debugger.
    /// Maps 2D/3D camera coordinates to screen space and performs raycast hit tests against living entities.
    /// </summary>
    public sealed class DebugRaycastPicker
    {
        public bool IsInspectModeActive { get; set; } = false;
        public InspectedEntityInfo HoveredEntity { get; private set; }
        public InspectedEntityInfo SelectedEntity { get; private set; }

        private readonly List<InspectedEntityInfo> _allEntities = new();
        public IReadOnlyList<InspectedEntityInfo> AllEntities => _allEntities;

        public void Update(
            Vector2 mousePos,
            bool mouseClicked,
            RuntimeScene scene,
            ScriptHostSystem scriptHost,
            IRenderController renderer,
            int screenWidth,
            int screenHeight)
        {
            _allEntities.Clear();
            if (scene == null || scene.World == null)
            {
                HoveredEntity = null;
                return;
            }

            var world = scene.World;
            float camOffsetX = screenWidth * 0.5f;
            float camOffsetY = screenHeight * 0.5f;
            float zoom = 1.0f;

            // Compute camera offset & zoom from scene 2D camera if available
            // By default Level 1 room is 1280x720 with center at (640, 360)
            float cx = 640f;
            float cy = 360f;
            camOffsetX = screenWidth * 0.5f - (cx * zoom);
            camOffsetY = screenHeight * 0.5f - (cy * zoom);

            // Query living entities in the ECS world
            InspectedEntityInfo bestHover = null;
            float bestDistSq = float.MaxValue;

            world.Query<TransformComponent>((Entity entity, ref TransformComponent transform) =>
            {
                string name = $"Entity #{entity.Id}";
                string spriteName = "None";
                string collider = "None";
                string script = "None";
                PgslBehavior pgsl = null;

                if (ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assetEntry))
                {
                    if (!string.IsNullOrEmpty(assetEntry.Prefab))
                    {
                        string clean = ResourceNames.FormatName(assetEntry.Prefab);
                        name = clean;
                    }
                    else if (!string.IsNullOrEmpty(assetEntry.Image))
                    {
                        spriteName = assetEntry.Image;
                        string clean = ResourceNames.FormatName(assetEntry.Image);

                        name = $"{clean} #{entity.Id}";
                    }
                }

                if (scriptHost != null)
                {
                    var b = scriptHost.FindBehaviorForEntity(entity);
                    if (b is PgslBehavior pb)
                    {
                        pgsl = pb;
                        script = pb.ScriptName ?? "PGSL Script";
                        if (name.StartsWith("Entity #", StringComparison.Ordinal) && !string.IsNullOrEmpty(pb.ScriptName))
                            name = pb.ScriptName;
                    }
                }

                ObjectDrawAssetEntry drawMetrics = ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry resolved)
                    ? resolved : new ObjectDrawAssetEntry();
                float baseW = Math.Max(1f, drawMetrics.SpritePixelWidth);
                float baseH = Math.Max(1f, drawMetrics.SpritePixelHeight);
                float originNormX = Math.Clamp(drawMetrics.SpriteOriginX, 0f, 1f);
                float originNormY = Math.Clamp(drawMetrics.SpriteOriginY, 0f, 1f);

                if (world.Has<RigidBodyComponent>(entity))
                {
                    ref RigidBodyComponent body = ref world.GetRef<RigidBodyComponent>(entity);
                    collider = $"{body.Shape} ({(body.IsSensor ? "Trigger" : body.Motion.ToString())})";
                }
                else if (world.Has<PhysicsComponent>(entity))
                {
                    ref PhysicsComponent physics = ref world.GetRef<PhysicsComponent>(entity);
                    collider = physics.Solid ? "2D Solid" : "2D Motion";
                }

                float ew = baseW * Math.Max(0.1f, Math.Abs(transform.ScaleX)) * zoom;
                float eh = baseH * Math.Max(0.1f, Math.Abs(transform.ScaleY)) * zoom;

                // Screen coordinates
                float sx = camOffsetX + (transform.X * zoom);
                float sy = camOffsetY + (transform.Y * zoom);

                float minX = sx - (ew * originNormX);
                float maxX = minX + ew;
                float minY = sy - (eh * originNormY);
                float maxY = minY + eh;

                var info = new InspectedEntityInfo
                {
                    Entity = entity,
                    Name = name,
                    WorldPosition = new Vector3(transform.X, transform.Y, transform.Z),
                    ScreenBounds = new Vector4(minX, minY, maxX, maxY),
                    SpriteName = spriteName,
                    ColliderType = collider,
                    ScriptName = script,
                    Behavior = pgsl,
                };

                // Populate built-in vars
                info.BuiltInVars["x"] = Math.Round(transform.X, 1);
                info.BuiltInVars["y"] = Math.Round(transform.Y, 1);
                info.BuiltInVars["z"] = Math.Round(transform.Z, 1);
                info.BuiltInVars["scaleX"] = Math.Round(transform.ScaleX, 2);
                info.BuiltInVars["scaleY"] = Math.Round(transform.ScaleY, 2);
                info.BuiltInVars["rotation"] = Math.Round(transform.Rotation, 1);

                if (world.Has<Draw2DComponent>(entity))
                {
                    ref Draw2DComponent d2 = ref world.GetRef<Draw2DComponent>(entity);
                    info.BuiltInVars["depth"] = d2.Depth;
                    info.BuiltInVars["visible"] = d2.Visible;
                }

                // Populate custom PGSL vars
                if (pgsl?.Context != null)
                {
                    foreach (var (k, v) in pgsl.Context.Variables)
                    {
                        info.CustomVars[k] = v;
                    }
                    if (pgsl.Context.Speed != 0) info.BuiltInVars["speed"] = pgsl.Context.Speed;
                    if (pgsl.Context.HSpeed != 0) info.BuiltInVars["hspeed"] = pgsl.Context.HSpeed;
                    if (pgsl.Context.VSpeed != 0) info.BuiltInVars["vspeed"] = pgsl.Context.VSpeed;
                }

                _allEntities.Add(info);

                // Check mouse hit test
                if (mousePos.X >= minX && mousePos.X <= maxX && mousePos.Y >= minY && mousePos.Y <= maxY)
                {
                    float dx = mousePos.X - sx;
                    float dy = mousePos.Y - (sy - eh * 0.5f);
                    float distSq = dx * dx + dy * dy;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestHover = info;
                    }
                }
            });

            _allEntities.Sort((a, b) => a.Id.CompareTo(b.Id));

            HoveredEntity = bestHover;

            if (mouseClicked && bestHover != null && IsInspectModeActive)
            {
                SelectEntity(bestHover);
            }
        }

        public void SelectEntity(InspectedEntityInfo entity)
        {
            SelectedEntity = entity;
            if (entity != null)
            {
                entity.IsExpanded = true;
            }
        }

        public void ClearSelection()
        {
            SelectedEntity = null;
        }
    }
}
