using System;
using System.Drawing;
using System.Numerics;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Modeling;
using Genesis.Runtime.Rendering;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// The concrete <see cref="IPgslDrawSurface"/> that makes PGSL drawing commands actually put
    /// pixels on screen (NEXT-033 — previously nothing implemented this interface, so every
    /// <c>DrawText</c>/<c>DrawRectangle</c>/<c>DrawCircle</c> silently no-opped).
    ///
    /// <para>Shapes route to <see cref="IRenderController"/>'s sprite-batch primitives
    /// (<c>DrawRect</c>/<c>DrawLine</c>). Text routes to <see cref="IHudCanvas"/>, which R4 backed
    /// with the shared Skia overlay rather than Direct2D. Both now end up on the same overlay —
    /// <c>IRenderController.DrawText</c> queues onto it too — but the HUD canvas stays the route
    /// here because it carries the surface size a script's layout is written against.</para>
    ///
    /// Circles are approximated: filled ones as horizontal scanline strips, outlines as line
    /// segments, since the renderer exposes no circle primitive.
    /// </summary>
    public sealed class PgslRenderDrawSurface : IPgslDrawSurface
    {
        private const int CircleSegments = 32;

        private readonly IRenderController _renderer;
        private readonly IHudCanvas _hud;
        private readonly int _width;
        private readonly int _height;
        private readonly IRenderCommandSink _commands;
        private readonly string _projectPath;
        private readonly bool _is3DActive;
        private readonly bool _isGui;
        private static readonly RuntimeModelRenderSystem Models = new();

        public PgslRenderDrawSurface(
            IRenderController renderer,
            IHudCanvas hud,
            int width,
            int height,
            IRenderCommandSink commands = null,
            string projectPath = null,
            bool is3DActive = false,
            bool isGui = false)
        {
            _renderer = renderer;
            _hud = hud;
            _width = width;
            _height = height;
            _commands = commands;
            _projectPath = projectPath;
            _is3DActive = is3DActive;
            _isGui = isGui;
        }

        public bool Is3DActive => _is3DActive;

        /// <summary>GUI sprites share the primitive layer, preserving authored panel/icon order.</summary>
        public int SpriteDepth => _isGui ? -10000 : 0;

        public void Clear(Color color) =>
            _renderer?.DrawRect(0f, 0f, _width, _height, ToRender(color), filled: true);

        public void DrawPoint(float x, float y, Color color) =>
            _renderer?.DrawRect(x, y, 1f, 1f, ToRender(color), filled: true);

        public void DrawLine(float x1, float y1, float x2, float y2, Color color, float thickness = 1f) =>
            _renderer?.DrawLine(x1, y1, x2, y2, ToRender(color), thickness);

        public void DrawRectangle(Color color, RectangleF rect) =>
            _renderer?.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, ToRender(color), filled: false);

        public void FillRectangle(Color color, RectangleF rect) =>
            _renderer?.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, ToRender(color), filled: true);

        public void FillCircle(Color color, float centerX, float centerY, float radius)
        {
            if (_renderer == null || radius <= 0f) return;

            // Horizontal scanline strips: for each row, half-width = sqrt(r² - dy²).
            RenderColor c = ToRender(color);
            int rows = Math.Max(1, (int)MathF.Ceiling(radius * 2f));
            for (int i = 0; i < rows; i++)
            {
                float dy = -radius + i + 0.5f;
                float half = MathF.Sqrt(MathF.Max(0f, radius * radius - dy * dy));
                if (half <= 0f) continue;
                _renderer.DrawRect(centerX - half, centerY + dy - 0.5f, half * 2f, 1f, c, filled: true);
            }
        }

        public void DrawCircle(Color color, float centerX, float centerY, float radius, float thickness = 1f)
        {
            if (_renderer == null || radius <= 0f) return;

            RenderColor c = ToRender(color);
            float step = MathF.Tau / CircleSegments;
            float px = centerX + radius, py = centerY;
            for (int i = 1; i <= CircleSegments; i++)
            {
                float a = step * i;
                float nx = centerX + MathF.Cos(a) * radius;
                float ny = centerY + MathF.Sin(a) * radius;
                _renderer.DrawLine(px, py, nx, ny, c, thickness);
                px = nx;
                py = ny;
            }
        }

        public void DrawText(string text, string font, float size, Color color, Rectangle bounds)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_hud is not null)
                _hud.Text(text, bounds.X, bounds.Y, size <= 0f ? 12f : size, ToVector(color));
            else
                _renderer?.DrawText(text, bounds.X, bounds.Y, size <= 0f ? 12f : size, ToRender(color));
        }

        public void DrawText3D(
            string text,
            float x, float y, float z,
            string font,
            float size,
            Color color,
            Matrix4x4 view,
            Matrix4x4 projection)
        {
            if (!_is3DActive || _renderer is null || string.IsNullOrEmpty(text)) return;
            Vector4 clip = Vector4.Transform(new Vector4(x, y, z, 1f), view * projection);
            if (clip.W <= 0.0001f) return;
            float inverseW = 1f / clip.W;
            float ndcX = clip.X * inverseW;
            float ndcY = clip.Y * inverseW;
            float ndcZ = clip.Z * inverseW;
            if (ndcZ < 0f || ndcZ > 1f || MathF.Abs(ndcX) > 1.1f || MathF.Abs(ndcY) > 1.1f) return;
            float screenX = (ndcX * 0.5f + 0.5f) * _width;
            float screenY = (-ndcY * 0.5f + 0.5f) * _height;
            _renderer.DrawText(text, screenX, screenY, MathF.Max(1f, size), ToRender(color));
        }

        public void DrawSprite(string spriteName, float x, float y, int frame,
            float xscale, float yscale, float angle, Color blend, float alpha)
        {
            if (_renderer == null) return;
            var queue = _commands ?? new FrameRenderQueue();
            ObjectDrawPass.QueueSprite2D(_projectPath ?? PgslCommands.ProjectPath, queue, _renderer,
                new ObjectDrawAssetEntry(), new TransformComponent { X = x, Y = y, ScaleX = xscale, ScaleY = yscale, ScaleZ = 1, Rotation = angle },
                new Draw2DComponent { Visible = true, Depth = SpriteDepth }, spriteName, frame, alpha, 0, 0, 1, ToRender(blend));
            if (_commands == null) ((FrameRenderQueue)queue).Flush(_renderer, includeMeshes: false);
        }

        public void QueueCube3D(float x, float y, float z, float sx, float sy, float sz, Color color, float alpha)
        {
            QueuePrimitive(BuiltinMeshKind.Cube, x, y, z, sx, sy, sz, color, alpha);
        }

        public void QueueSphere3D(float x, float y, float z, float radius, Color color, float alpha)
        {
            float diameter = MathF.Max(0.001f, MathF.Abs(radius) * 2f);
            QueuePrimitive(BuiltinMeshKind.Sphere, x, y, z, diameter, diameter, diameter, color, alpha);
        }

        public void QueueModel3D(
            string modelName, float x, float y, float z, float scale, Color color, float alpha)
            => QueueModelTransform3D(
                modelName,
                shaderName: string.Empty,
                x, y, z,
                scale, scale, scale,
                0f, 0f, 0f,
                color,
                alpha);

        public void QueueModelTransform3D(
            string modelName,
            string shaderName,
            float x, float y, float z,
            float sx, float sy, float sz,
            float xrot, float yrot, float zrot,
            Color color,
            float alpha)
        {
            if (!_is3DActive || _renderer == null || string.IsNullOrWhiteSpace(modelName)) return;

            Vector3 safeScale = new(
                MathF.Max(0.001f, MathF.Abs(sx)),
                MathF.Max(0.001f, MathF.Abs(sy)),
                MathF.Max(0.001f, MathF.Abs(sz)));
            const float DegreesToRadians = MathF.PI / 180f;
            Matrix4x4 world = Matrix4x4.CreateScale(safeScale)
                            * Matrix4x4.CreateFromYawPitchRoll(
                                yrot * DegreesToRadians,
                                xrot * DegreesToRadians,
                                zrot * DegreesToRadians)
                            * Matrix4x4.CreateTranslation(x, y, z);
            Draw3DComponent draw = new()
            {
                Visible = true,
                CastShadows = true,
                ReceiveShadows = true,
            };
            ModelRendererComponent model = new()
            {
                ModelAsset = modelName,
                ScaleX = 1f,
                ScaleY = 1f,
                ScaleZ = 1f,
                CastShadows = true,
                ReceiveShadows = true,
            };

            ModelRenderQueue queue = ModelRenderQueue.Rent();
            if (!Models.Enqueue(
                queue,
                _projectPath,
                modelName,
                materialOverride: null,
                world,
                draw,
                model,
                animation: default,
                _renderer)) return;

            // Resource-backed floors use the same material path as DrawFloor3D, but they are
            // submitted through the model queue. Mark them explicitly so the renderer binds its
            // checker texture and keeps the plane double-sided while cameras orbit around it.
            // Without this, the authored checker-floor mesh was valid but rendered as a missing
            // (or featureless) plane because it followed the ordinary model material path.
            bool floorAsset = modelName.Contains("floor", StringComparison.OrdinalIgnoreCase)
                || modelName.Contains("ground", StringComparison.OrdinalIgnoreCase);
            float a = Math.Clamp(alpha, 0f, 1f) * (color.A / 255f);
            RenderColor tint = ToRender(color);
            queue.Transform(call =>
            {
                call.Tint = new RenderColor(
                    call.Tint.R * tint.R,
                    call.Tint.G * tint.G,
                    call.Tint.B * tint.B,
                    a);
                call.Alpha = Math.Clamp(call.Alpha * a, 0f, 1f);
                if (call.Alpha < 0.999f)
                    call.Flags |= MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow;
                if (call.Alpha < 0.999f && (call.Flags & MeshDrawFlags.Emissive) != 0)
                    call.Flags |= MeshDrawFlags.Additive;
                if (floorAsset)
                    call.Flags |= MeshDrawFlags.IsFloor | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow;
                if (!string.IsNullOrWhiteSpace(shaderName))
                    ObjectDrawPass.TryApplyMeshShader(_renderer, _projectPath, shaderName, ref call);
                return call;
            });

            if (_commands is IMeshDrawList meshList)
            {
                var buffer = new MeshDrawCall[Math.Max(1, queue.Count)];
                int count = queue.CopyTo(buffer, 0);
                for (int i = 0; i < count; i++) meshList.Add(buffer[i]);
            }
            else queue.Draw(_renderer);
        }

        public void QueuePointLight3D(
            float x, float y, float z,
            float radius,
            float intensity,
            Color color,
            float falloff = 2f)
        {
            if (!_is3DActive || _renderer == null) return;
            _renderer.AddPointLight(
                new Vector3(x, y, z),
                new Vector3(color.R / 255f, color.G / 255f, color.B / 255f),
                MathF.Max(0.01f, MathF.Abs(radius)),
                MathF.Max(0f, intensity),
                Math.Clamp(falloff, 0.05f, 16f));
        }

        private void QueuePrimitive(
            BuiltinMeshKind kind,
            float x, float y, float z,
            float sx, float sy, float sz,
            Color color,
            float alpha)
        {
            if (!_is3DActive || _renderer == null) return;
            MeshHandle mesh = _renderer.GetBuiltinMesh(kind);
            if (!mesh.IsValid) return;

            float a = Math.Clamp(alpha, 0f, 1f) * (color.A / 255f);
            bool floorPrimitive = kind == BuiltinMeshKind.Cube
                && sx >= 4f
                && sz >= 4f
                && sy <= 0.1f;
            MeshDrawFlags flags = a < 0.999f
                ? MeshDrawFlags.Transparent | MeshDrawFlags.NoDepthWrite | MeshDrawFlags.NoShadow
                : MeshDrawFlags.None;
            if (floorPrimitive)
            {
                // DrawFloor3D is represented by a very thin cube in the PGSL surface. Treat it as
                // the shared ground plane, including the renderer's double-sided floor policy, so
                // backend front-face conventions cannot cull the whole PGSL test floor.
                flags |= MeshDrawFlags.IsFloor | MeshDrawFlags.NoCull | MeshDrawFlags.NoShadow;
            }

            MeshDrawCall call = new()
            {
                Mesh = mesh,
                World = Matrix4x4.CreateScale(
                            MathF.Max(0.001f, MathF.Abs(sx)),
                            MathF.Max(0.001f, MathF.Abs(sy)),
                            MathF.Max(0.001f, MathF.Abs(sz)))
                      * Matrix4x4.CreateTranslation(x, y, z),
                Tint = new RenderColor(color.R / 255f, color.G / 255f, color.B / 255f, a),
                Alpha = a,
                Flags = flags,
            };

            if (_commands != null) _commands.DrawMesh(call);
            else _renderer.DrawMesh(call);
        }

        private static RenderColor ToRender(Color c) =>
            new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        private static Vector4 ToVector(Color c) =>
            new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    }
}
