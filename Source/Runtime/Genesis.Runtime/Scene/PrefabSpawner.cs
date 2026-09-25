using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Genesis.Runtime.ECS;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Scripting;
using Genesis.Shared.ECS;
using Genesis.Shared.ECS.Components;
using Genesis.Shared.Interfaces;
using EcsWorld = Genesis.Runtime.ECS.World;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// Builds a live ECS entity from a component-based prefab definition
    /// (<c>*.object.json</c>) as described in the Architectural Evaluation. Replaces
    /// the old PGSL ScriptComponent dispatch: the prefab's <c>ScriptComponent.ScriptClass</c>
    /// names a compiled <see cref="EntityBehavior"/>, which is bound through
    /// <see cref="ScriptHostSystem"/>.
    ///
    /// Supported component "type" values: TransformComponent, SpriteComponent,
    /// PhysicsComponent, ModelRendererComponent, MaterialComponent, ShaderComponent,
    /// ParticleComponent, AudioComponent, PointLightComponent, ModelAnimatorComponent, ModelMorphComponent,
    /// NavMeshAgentComponent, CrowdAgentComponent,
    /// Draw2DComponent, Draw3DComponent, ScriptComponent, WildlifeComponent. Legacy
    /// ModelComponent JSON is upgraded in memory.
    /// </summary>
    public static class PrefabSpawner
    {
        public static Entity Spawn(
            EcsWorld world,
            JObject prefab,
            ScriptHostSystem scriptHost = null,
            float originX = 0f,
            float originY = 0f)
        {
            Entity e = world.CreateEntity();
            world.Set(e, new EntityLifecycleComponent { NeedsStart = true, Enabled = true });

            // Default transform so every entity is positionable even if the prefab omits one.
            world.Set(e, new TransformComponent { X = originX, Y = originY, ScaleX = 1f, ScaleY = 1f, ScaleZ = 1f });

            var components = prefab["components"] as JArray;
            if (components == null) return e;

            string rootSprite = (string)prefab["sprite"];
            string rootModel = (string)prefab["model"];
            string spriteName = null;
            string modelName = null;
            string materialPath = null;
            string shaderPath = (string)prefab["shader"];
            float spriteAlpha = 1f;
            float modelScaleX = 1f, modelScaleY = 1f, modelScaleZ = 1f;
            bool hasDraw2D = false;
            bool hasDraw3D = false;
            FaceCullingOverride culling = MeshRasterDefaults.ParseCulling(
                (string)prefab["culling"], FaceCullingOverride.Default);
            FrontFaceWindingOverride winding = MeshRasterDefaults.ParseWinding(
                (string)prefab["windingOrder"], FrontFaceWindingOverride.Default);

            foreach (var token in components)
            {
                if (token is not JObject comp) continue;
                string type = (string)comp["type"];
                JObject props = comp["props"] as JObject ?? new JObject();

                // Honour the component's enabled flag (set in the Object Editor).
                JToken enabledTok = comp["enabled"];
                if (enabledTok != null && enabledTok.Type == JTokenType.Boolean && !(bool)enabledTok)
                    continue;

                switch (type)
                {
                    case "TransformComponent":
                        ApplyTransform(world, e, props, originX, originY);
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "SpriteComponent":
                        if (props["Sprite"] != null && !string.IsNullOrWhiteSpace((string)props["Sprite"]))
                            spriteName = ((string)props["Sprite"]).Trim();
                        spriteAlpha = F(props, "Alpha", 1f);
                        world.Set(e, new SpriteComponent
                        {
                            ImageSpeed = F(props, "ImageSpeed", 0f),
                            AnimationTagIndex = -1,
                            AnimationLoopOverride = -1,
                            Alpha = spriteAlpha,
                            Depth = (int)F(props, "Depth", 0f),
                        });
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "PhysicsComponent":
                        world.Set(e, new PhysicsComponent
                        {
                            Gravity = F(props, "Gravity", 0f),
                            GravityDirection = F(props, "GravityDirection", 270f),
                            Friction = F(props, "Friction", 0f),
                            Solid = B(props, "Solid", true),
                        });
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "MaterialComponent":
                        materialPath = ((string)(props["Asset"] ?? props["Material"]))?.Trim();
                        break;
                    case "ShaderComponent":
                        shaderPath = ((string)(props["Asset"] ?? props["Shader"]))?.Trim();
                        break;
                    case "ParticleComponent":
                        world.Set(e, new ParticleComponent
                        {
                            Asset = ((string)(props["Asset"] ?? props["Particle"]))?.Trim(),
                            ParticleTypeId = -1,
                            EmitRate = F(props, "EmitRate", 0f),
                            RateScale = F(props, "RateScale", 1f),
                            FollowEntity = B(props, "FollowEntity", true),
                            Emitting = B(props, "Emitting", true),
                        });
                        break;
                    case "AudioComponent":
                        world.Set(e, new AudioComponent
                        {
                            Asset = ((string)(props["Asset"] ?? props["Audio"]))?.Trim(),
                            SoundIndex = -1,
                            Volume = F(props, "Volume", 1f),
                            Pitch = F(props, "Pitch", 1f),
                            AutoPlay = B(props, "AutoPlay", false),
                            Spatial = B(props, "Spatial", false),
                            Looping = B(props, "Loop", false),
                            Playing = false,
                        });
                        break;
                    case "PointLightComponent":
                        var (cr, cg, cb) = Vec3(props, "Color", 1f, 0.72f, 0.35f);
                        var (sr, sg, sb) = Vec3(props, "SecondaryColor", 1f, 0.25f, 0.08f);
                        var (tr, tg, tb) = Vec3(props, "TertiaryColor", 0.35f, 0.55f, 1f);
                        var (ox, oy, oz) = Vec3(props, "Offset", 0f, 0f, 0f);
                        _ = Enum.TryParse(
                            (string)props["Action"] ?? "Steady",
                            ignoreCase: true,
                            out LightEmitterAction lightAction);
                        world.Set(e, new PointLightComponent
                        {
                            Color = new System.Numerics.Vector3(cr, cg, cb),
                            SecondaryColor = new System.Numerics.Vector3(sr, sg, sb),
                            TertiaryColor = new System.Numerics.Vector3(tr, tg, tb),
                            ColorCount = Math.Clamp((int)F(props, "ColorCount", 1f), 1, 3),
                            Offset = new System.Numerics.Vector3(ox, oy, oz),
                            Radius = MathF.Max(0.01f, F(props, "Radius", 8f)),
                            Intensity = MathF.Max(0f, F(props, "Intensity", 2f)),
                            Falloff = Math.Clamp(F(props, "Falloff", 2f), 0.05f, 16f),
                            Action = lightAction,
                            ActionSpeed = MathF.Max(0f, F(props, "ActionSpeed", 1f)),
                            ActionAmount = Math.Clamp(F(props, "ActionAmount", 0.25f), 0f, 1f),
                            Phase = F(props, "Phase", 0f),
                            Enabled = B(props, "Enabled", true),
                        });
                        break;
                    case "ModelComponent":
                    case "ModelRendererComponent":
                        if (props["Model"] != null && !string.IsNullOrWhiteSpace((string)props["Model"]))
                            modelName = ((string)props["Model"]).Trim();
                        if (props["ModelAsset"] != null && !string.IsNullOrWhiteSpace((string)props["ModelAsset"]))
                            modelName = ((string)props["ModelAsset"]).Trim();
                        if (props["Material"] != null && !string.IsNullOrWhiteSpace((string)props["Material"]))
                            materialPath = ((string)props["Material"]).Trim();
                        if (props["MaterialOverride"] != null && !string.IsNullOrWhiteSpace((string)props["MaterialOverride"]))
                            materialPath = ((string)props["MaterialOverride"]).Trim();
                        modelScaleX = F(props, "ScaleX", 1f);
                        modelScaleY = F(props, "ScaleY", 1f);
                        modelScaleZ = F(props, "ScaleZ", 1f);
                        world.Set(e, new ModelRendererComponent
                        {
                            ModelAsset = modelName,
                            MaterialOverride = materialPath,
                            ScaleX = modelScaleX, ScaleY = modelScaleY, ScaleZ = modelScaleZ,
                            CastShadows = B(props, "CastShadows", true),
                            ReceiveShadows = B(props, "ReceiveShadows", true),
                            KeepPreviousTransform = B(props, "KeepPreviousTransform", false),
                            LodPolicy = (int)F(props, "LodPolicy", 0f),
                            Culling = culling,
                            WindingOrder = winding,
                        });
                        ComponentLifecycle.OnAttach(world, e, "ModelRendererComponent");
                        break;
                    case "ModelAnimatorComponent":
                    case "AnimatorComponent":
                        world.Set(e, new ModelAnimatorComponent
                        {
                            ClipName = (string)(props["ClipName"] ?? props["Clip"]) ?? "",
                            PreviousClipName = (string)props["PreviousClipName"] ?? "",
                            ClipFps = F(props, "ClipFps", F(props, "FPS", 60f)),
                            TimeSeconds = F(props, "TimeSeconds", 0f),
                            PreviousTimeSeconds = F(props, "PreviousTimeSeconds", 0f),
                            PlaybackSpeed = F(props, "PlaybackSpeed", 1f),
                            BlendTime = F(props, "BlendTime", 0f),
                            BlendDuration = F(props, "BlendDuration", 0f),
                            BlendElapsed = F(props, "BlendElapsed", 0f),
                            Playing = B(props, "Playing", true),
                            Loop = B(props, "Loop", true),
                        });
                        ComponentLifecycle.OnAttach(world, e, "ModelAnimatorComponent");
                        break;
                    case "ModelMorphComponent":
                        world.Set(e, new ModelMorphComponent
                        {
                            Enabled = B(props, "Enabled", true),
                            Weights = NamedWeights(props["Weights"] ?? props["InitialWeights"]),
                        });
                        ComponentLifecycle.OnAttach(world, e, "ModelMorphComponent");
                        break;
                    case "ThirdPersonCameraComponent":
                        world.Set(e, new Cameras.ThirdPersonCameraComponent
                        {
                            Rig = new Cameras.ThirdPersonCamera
                            {
                                Distance = F(props, "Distance", 7.5f), Pitch = F(props, "Pitch", 15f), Yaw = F(props, "Yaw", 0f),
                                ShoulderOffset = new System.Numerics.Vector3(F(props, "ShoulderX", 1.2f), F(props, "Height", 2.4f), F(props, "ShoulderZ", 0)),
                                CollisionEnabled = B(props, "CollisionEnabled", true), MouseLook = B(props, "MouseLook", true),
                            },
                        });
                        break;
                    case "NavMeshAgentComponent":
                        world.Set(e, new Navigation.NavMeshAgentComponent
                        {
                            Agent = new Navigation.NavMeshAgent { Speed = F(props, "Speed", 3.8f), StoppingDistance = F(props, "StoppingDistance", .1f) },
                        });
                        break;
                    case "CrowdAgentComponent":
                        world.Set(e, new Navigation.CrowdAgentComponent
                        {
                            Enabled = B(props, "Enabled", true),
                            Radius = F(props, "Radius", .35f),
                            NeighborDistance = F(props, "NeighborDistance", 3f),
                            AvoidanceStrength = F(props, "AvoidanceStrength", .75f),
                            MaxNeighbors = (int)F(props, "MaxNeighbors", 12f),
                            NearUpdateHz = F(props, "NearUpdateHz", 30f),
                            FarUpdateHz = F(props, "FarUpdateHz", 6f),
                            FarDistance = F(props, "FarDistance", 35f),
                            AvoidanceDistance = F(props, "AvoidanceDistance", 250f),
                        });
                        break;
                    case "Draw2DComponent":
                        hasDraw2D = true;
                        string draw2dImage = (string)props["Image"];
                        if (!string.IsNullOrWhiteSpace(draw2dImage)) spriteName = draw2dImage.Trim();
                        world.Set(e, new Draw2DComponent
                        {
                            Visible = B(props, "Visible", true),
                            Depth = F(props, "Depth", 0f),
                            BlendMode = string.Equals((string)props["BlendMode"], "Additive", StringComparison.OrdinalIgnoreCase)
                                ? (byte)1 : (byte)0,
                        });
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "Draw3DComponent":
                        hasDraw3D = true;
                        string draw3dModel = (string)props["Model"];
                        if (!string.IsNullOrWhiteSpace(draw3dModel)) modelName = draw3dModel.Trim();
                        if (props["Material"] != null && !string.IsNullOrWhiteSpace((string)props["Material"]))
                            materialPath = ((string)props["Material"]).Trim();
                        bool procedural = string.IsNullOrWhiteSpace(draw3dModel) && string.IsNullOrWhiteSpace(modelName);
                        world.Set(e, new Draw3DComponent
                        {
                            Visible = B(props, "Visible", true),
                            CastShadows = B(props, "CastShadows", true),
                            ReceiveShadows = B(props, "ReceiveShadows", true),
                            Procedural = procedural,
                        });
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "ScriptComponent":
                        string scriptClass = (string)props["ScriptClass"];
                        scriptHost?.Attach(world, e, scriptClass, ToDict(props));
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                    case "WildlifeComponent":
                        WildlifePreset preset = Enum.TryParse((string)props["Preset"], true, out WildlifePreset authoredPreset)
                            ? authoredPreset
                            : WildlifePreset.Grazer;
                        ref TransformComponent wildlifeTransform = ref world.GetRef<TransformComponent>(e);
                        world.Set(e, new WildlifeComponent
                        {
                            Preset = preset,
                            Home = new System.Numerics.Vector3(wildlifeTransform.X, wildlifeTransform.Y, wildlifeTransform.Z),
                            HomeRadius = F(props, "HomeRadius", 35f),
                            Hunger = F(props, "Hunger", 0.15f),
                            Thirst = F(props, "Thirst", 0.1f),
                            Energy = F(props, "Energy", 1f),
                            HungerRate = F(props, "HungerRate", 0.006f),
                            ThirstRate = F(props, "ThirstRate", 0.009f),
                            AwarenessRadius = F(props, "AwarenessRadius", 60f),
                            Seed = (int)F(props, "Seed", 1337f),
                        });
                        world.Set(e, new AgentComponent
                        {
                            Target = new System.Numerics.Vector3(wildlifeTransform.X, wildlifeTransform.Y, wildlifeTransform.Z),
                            MaxSpeed = F(props, "MaxSpeed", 0f),
                            Acceleration = F(props, "Acceleration", 0f),
                            ArrivalRadius = F(props, "ArrivalRadius", 1.2f),
                            DecisionTimer = 0f,
                        });
                        ComponentLifecycle.OnAttach(world, e, type);
                        break;
                }
            }

            if (string.IsNullOrEmpty(spriteName) && !string.IsNullOrWhiteSpace(rootSprite))
                spriteName = rootSprite.Trim();
            if (string.IsNullOrEmpty(modelName) && !string.IsNullOrWhiteSpace(rootModel))
                modelName = rootModel.Trim();

            bool is3D = string.Equals((string)prefab["dimension"], "ThreeD", StringComparison.OrdinalIgnoreCase);
            bool procedural3D = hasDraw3D && world.Has<Draw3DComponent>(e) && world.GetRef<Draw3DComponent>(e).Procedural;
            ObjectDrawPass.ApplyDrawComponents(
                world, e, spriteName, modelName, spriteAlpha,
                new System.Numerics.Vector3(modelScaleX, modelScaleY, modelScaleZ),
                is3D, hasDraw2D, hasDraw3D, procedural3D, materialPath);

            if (prefab["visible"] is JValue visible && visible.Type == JTokenType.Boolean)
            {
                if (world.Has<Draw2DComponent>(e)) world.GetRef<Draw2DComponent>(e).Visible = (bool)visible;
                if (world.Has<Draw3DComponent>(e)) world.GetRef<Draw3DComponent>(e).Visible = (bool)visible;
            }
            if (prefab["solid"] is JValue solid && solid.Type == JTokenType.Boolean && world.Has<PhysicsComponent>(e))
                world.GetRef<PhysicsComponent>(e).Solid = (bool)solid;
            if ((bool?)prefab["persistent"] == true) world.Set(e, new PersistentObjectComponent());

            if (world.Has<ModelRendererComponent>(e))
            {
                ref ModelRendererComponent rendererComponent = ref world.GetRef<ModelRendererComponent>(e);
                rendererComponent.Culling = culling;
                rendererComponent.WindingOrder = winding;
            }

            if (ObjectDrawAssetRegistry.TryGet(e, out ObjectDrawAssetEntry drawAssets))
            {
                drawAssets.Culling = culling;
                drawAssets.WindingOrder = winding;
                drawAssets.Shader = shaderPath?.Trim();
                if (prefab["shaderVariant"] is JValue variantToken
                    && variantToken.Type == JTokenType.String)
                {
                    drawAssets.ShaderVariant = variantToken.Value<string>()?.Trim();
                }
                if (prefab["shaderParameters"] is JObject authoredParameters)
                {
                    foreach (JProperty property in authoredParameters.Properties())
                    {
                        if (property.Value is JArray array)
                            drawAssets.ShaderParameters[property.Name] = array.Values<float>().ToArray();
                        else if (property.Value.Type is JTokenType.Float or JTokenType.Integer)
                            drawAssets.ShaderParameters[property.Name] = new[] { property.Value.Value<float>() };
                    }
                }
                if (prefab["shaderResources"] is JObject authoredResources)
                {
                    foreach (JProperty property in authoredResources.Properties())
                    {
                        if (property.Value.Type == JTokenType.String)
                            drawAssets.ShaderResources[property.Name] = property.Value.Value<string>() ?? string.Empty;
                    }
                }
            }

            return e;
        }

        private static void ApplyTransform(EcsWorld world, Entity e, JObject props, float ox, float oy)
        {
            var (sx, sy, sz) = Vec3(props, "Scale", 1f, 1f, 1f);
            var (px, py, pz) = Vec3(props, "Position", ox, oy, 0f);
            var (rx, ry, rz) = Vec3(props, "Rotation", 0f, 0f, 0f);
            world.Set(e, new TransformComponent
            {
                X = px, Y = py, Z = pz,
                ScaleX = sx, ScaleY = sy, ScaleZ = sz,
                Rotation = rz,
                RotationX = rx, RotationY = ry, RotationZ = rz,
            });
        }

        // ----- tolerant primitive parsing ("1.0, 2.0, 3.0" strings or arrays) -----

        private static (float, float, float) Vec3(JObject props, string key, float dx, float dy, float dz)
        {
            JToken t = props[key];
            if (t == null) return (dx, dy, dz);

            if (t.Type == JTokenType.String)
            {
                string[] parts = ((string)t).Split(',');
                float x = Parse(parts, 0, dx);
                float y = Parse(parts, 1, dy);
                float z = Parse(parts, 2, dz);
                return (x, y, z);
            }
            if (t is JArray arr)
            {
                float x = arr.Count > 0 ? (float)arr[0] : dx;
                float y = arr.Count > 1 ? (float)arr[1] : dy;
                float z = arr.Count > 2 ? (float)arr[2] : dz;
                return (x, y, z);
            }
            return (dx, dy, dz);
        }

        private static float Parse(string[] parts, int i, float def)
            => i < parts.Length &&
               float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
               ? v : def;

        private static float F(JObject props, string key, float def)
        {
            JToken t = props[key];
            if (t == null) return def;
            if (t.Type == JTokenType.String)
                return float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;
            try { return (float)t; } catch { return def; }
        }

        private static bool B(JObject props, string key, bool def)
        {
            JToken t = props[key];
            if (t == null) return def;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            return bool.TryParse((string)t, out bool v) ? v : def;
        }

        private static Dictionary<string, float> NamedWeights(JToken token)
        {
            Dictionary<string, float> result = new(StringComparer.OrdinalIgnoreCase);
            if (token is JObject values)
            {
                foreach (JProperty property in values.Properties())
                {
                    float value = property.Value.Type is JTokenType.Float or JTokenType.Integer
                        ? property.Value.Value<float>()
                        : float.TryParse(property.Value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 0f;
                    if (!string.IsNullOrWhiteSpace(property.Name) && float.IsFinite(value)) result[property.Name] = value;
                }
                return result;
            }
            string text = token?.ToString() ?? string.Empty;
            foreach (string assignment in text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int equals = assignment.IndexOf('=');
                if (equals <= 0 || !float.TryParse(assignment[(equals + 1)..].Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value)) continue;
                string name = assignment[..equals].Trim();
                if (!string.IsNullOrWhiteSpace(name)) result[name] = value;
            }
            return result;
        }

        private static Dictionary<string, string> ToDict(JObject props)
        {
            var d = new Dictionary<string, string>();
            foreach (var p in props.Properties())
                d[p.Name] = p.Value != null && p.Value.Type == JTokenType.String ? (string)p.Value : p.Value?.ToString();
            return d;
        }
    }
}

