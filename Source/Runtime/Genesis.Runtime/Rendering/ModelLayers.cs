using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Genesis.Shared.Rendering;

namespace Genesis.Runtime.Rendering
{
    /// <summary>
    /// Models drawn into images of their own with cameras of their own (see MeshDrawCall.Layer):
    /// the first-person layer (an instance's arms and weapon, with their own field of view and
    /// near plane, drawn over the world and under the GUI, never inside a wall) and GUI models (a
    /// model drawn into a GUI rectangle, such as an inventory portrait). GUI requests are made
    /// while the GUI draws, which is after the frame's 3D, so each shows the next frame.
    /// </summary>
    public static class ModelLayers
    {
        public const int FirstPerson = 1;
        public const int FirstGui = 100;
        private const int MaxGuiModels = 64;

        /// <summary>The first-person layer's vertical field of view in degrees; 0 uses the camera's.</summary>
        public static float FirstPersonFieldOfView { get; set; }

        /// <summary>The first-person layer's near plane in world units.</summary>
        public static float FirstPersonNearPlane { get; set; } = 0.01f;

        private readonly record struct GuiModel(string Model, int Width, int Height, float Yaw, float Pitch, float Zoom, string Clip, float Time);

        private static readonly object Gate = new();
        private static List<GuiModel> _requested = new();
        private static List<GuiModel> _submitting = new();
        private static readonly RuntimeModelRenderSystem Models = new();
        private static MeshDrawCall[] _buffer = new MeshDrawCall[64];

        /// <summary>Asks for a model in a GUI rectangle; returns the layer whose image to draw.</summary>
        public static int RequestGuiModel(string model, int width, int height, float yaw, float pitch, float zoom,
            string clip = null, float time = 0f)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(model) || _requested.Count >= MaxGuiModels) return 0;
                _requested.Add(new GuiModel(model.Trim(), Math.Clamp(width, 1, 2048), Math.Clamp(height, 1, 2048),
                    float.IsFinite(yaw) ? yaw : 0f, float.IsFinite(pitch) ? pitch : 0f,
                    float.IsFinite(zoom) && zoom > 0f ? zoom : 1f, clip ?? string.Empty, float.IsFinite(time) ? time : 0f));
                return FirstGui + _requested.Count - 1;
            }
        }

        /// <summary>Forgets requests (a new game or room).</summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _requested.Clear();
                _submitting.Clear();
            }
        }

        /// <summary>
        /// Gives this frame's layers their cameras and submits the GUI models asked for last frame.
        /// The host calls it while the frame's 3D is being submitted.
        /// </summary>
        public static void Submit(IRenderController renderer, string projectPath, Camera camera, int viewWidth, int viewHeight)
        {
            if (renderer == null) return;
            if (camera != null && viewWidth > 0 && viewHeight > 0)
            {
                float fov = FirstPersonFieldOfView > 1f ? FirstPersonFieldOfView * MathF.PI / 180f : camera.FieldOfView;
                float near = Math.Clamp(FirstPersonNearPlane, 0.001f, 10f);
                Matrix4x4 projection = Conventions.CreatePerspective(Math.Clamp(fov, 0.1f, 3f), viewWidth / (float)viewHeight, near, 200f);
                renderer.SetModelLayerCamera(FirstPerson, camera.ViewMatrix, projection, viewWidth, viewHeight, receiveShadows: true);
            }

            lock (Gate)
            {
                (_submitting, _requested) = (_requested, _submitting);
                _requested.Clear();
            }
            for (int i = 0; i < _submitting.Count; i++)
                SubmitGuiModel(renderer, projectPath, FirstGui + i, _submitting[i]);
            _submitting.Clear();
        }

        private static void SubmitGuiModel(IRenderController renderer, string projectPath, int layer, GuiModel request)
        {
            GModelAsset asset = RuntimeModelAssetRegistry.Shared.Load(projectPath, request.Model);
            if (asset == null || !asset.HasRenderableMeshes) return;
            Vector3 min = asset.Bounds?.Min ?? new Vector3(-0.5f), max = asset.Bounds?.Max ?? new Vector3(0.5f);
            Vector3 pivot = asset.Pivot?.Position ?? Vector3.Zero;
            Vector3 centre = (min + max) * 0.5f - pivot;
            float radius = MathF.Max(0.01f, Vector3.Distance(min, max) * 0.5f);

            const float Fov = 30f * MathF.PI / 180f;
            float distance = radius / MathF.Sin(Fov * 0.5f) / request.Zoom;
            Matrix4x4 world = Matrix4x4.CreateTranslation(-centre)
                * Matrix4x4.CreateFromYawPitchRoll(request.Yaw * MathF.PI / 180f, request.Pitch * MathF.PI / 180f, 0f);
            // The model faces the viewer: the camera stands on its -Z side, looking along +Z.
            Matrix4x4 view = Conventions.CreateLookAt(new Vector3(0f, 0f, -distance), Vector3.Zero, Vector3.UnitY);
            float near = MathF.Max(0.01f, distance - radius * 1.5f);
            Matrix4x4 projection = Conventions.CreatePerspective(Fov, request.Width / (float)request.Height, near, distance + radius * 1.5f);

            var renderer3d = new ModelRendererComponent { ModelAsset = request.Model, ScaleX = 1, ScaleY = 1, ScaleZ = 1, CastShadows = false, ReceiveShadows = false };
            var draw = new Draw3DComponent { Visible = true, CastShadows = false, ReceiveShadows = false };
            RuntimeModelAnimationState animation = string.IsNullOrWhiteSpace(request.Clip)
                ? default
                : RuntimeModelAnimationState.From(new ModelAnimatorComponent
                {
                    ClipName = request.Clip, TimeSeconds = request.Time, Loop = true, Playing = true, PlaybackSpeed = 1f,
                }, preserveRootTransform: false);
            ModelRenderQueue queue = ModelRenderQueue.Rent();
            if (!Models.Enqueue(queue, projectPath, request.Model, null, world, draw, renderer3d, animation, renderer)) return;
            queue.Transform(call =>
            {
                call.Layer = layer;
                call.Flags |= MeshDrawFlags.NoShadow | MeshDrawFlags.NoReceiveShadow;
                return call;
            });
            if (_buffer.Length < queue.Count) _buffer = new MeshDrawCall[queue.Count * 2];
            int count = queue.CopyTo(_buffer, 0);
            renderer.SetModelLayerCamera(layer, view, projection, request.Width, request.Height, receiveShadows: false);
            renderer.DrawMeshBatch(_buffer.AsSpan(0, count));
        }
    }
}
