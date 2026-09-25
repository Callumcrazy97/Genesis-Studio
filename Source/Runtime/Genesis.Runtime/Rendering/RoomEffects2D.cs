using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Genesis.Runtime.Particles;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Rendering;

/// <summary>
/// Opt-in, room-owned 2D illumination and bounded particle pool. PGSL supplies gameplay intent;
/// this class contains no game-specific rules. Darkness is a shadow-tested lightmap composited
/// as one sprite per viewport; soft source textures remain ordinary batched GPU sprites.
/// </summary>
public sealed class RoomEffects2D : IDisposable
{
    private static readonly ConditionalWeakTable<RoomAsset, RoomEffects2D> States = new();
    public static RoomEffects2D For(RoomAsset room) => room == null ? null : States.GetValue(room, _ => new RoomEffects2D());
    public const int ParticleBudget = 1024;
    public const int VisibleLightBudget = 12;
    private const int RayCount = 256;
    private readonly Dictionary<int, Light> _lights = new();
    private readonly Dictionary<int, RectangleF> _occluders = new();
    private readonly Dictionary<int, Contact> _contacts = new();
    private readonly Dictionary<int, Mask> _masks = new();
    private readonly Dictionary<string, ParticleConfig[]> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextureHandle> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Light> _visibleLights = new(32);
    private readonly Particle[] _particles = new Particle[ParticleBudget];
    private readonly Random _random = new(6731);
    private readonly float[] _rays = new float[RayCount];
    private int _count;
    private float _time;
    private float _ambient = 0.32f;
    private Vector3 _shadowColor = new(0.012f, 0.024f, 0.06f);
    private IRenderController _renderer;
    private TextureHandle _halo;
    public bool Enabled { get; private set; }
    public bool Paused { get; set; }
    public int ParticleCount => _count;
    public int LightCount => _lights.Count;
    public int OccluderCount => _occluders.Count;

    private struct Light
    {
        public Vector2 Position;
        public Vector3 Color;
        public float Radius, Intensity, Direction, HalfAngle;
        public bool Cone;
    }
    private readonly record struct Contact(float X, float Y, float Width, float Height, float Alpha);
    private sealed class Mask
    {
        public int Width, Height;
        public byte[] Pixels = Array.Empty<byte>();
        public float[] Illumination = Array.Empty<float>();
        public TextureHandle Texture;
    }
    private struct Particle
    {
        public Vector2 Position, Velocity, Source, Target;
        public float Age, Life, Phase, Rotation;
        public bool Flow;
        public ParticleConfig Config;
    }

    public void SetAmbient(float amount, float r, float g, float b)
    {
        if (!float.IsFinite(amount) || !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b)) return;
        Enabled = true;
        _ambient = Math.Clamp(amount, 0.05f, 1f);
        _shadowColor = Vector3.Clamp(new Vector3(r, g, b), Vector3.Zero, Vector3.One);
    }

    public void SetLight(int id, float x, float y, float radius, float intensity,
        float r, float g, float b, float direction = 0, float spread = 360)
    {
        if (!Finite(x, y, radius, intensity) || !Finite(r, g, b, direction) || !float.IsFinite(spread)) return;
        if (radius <= 0 || intensity <= 0) { _lights.Remove(id); return; }
        if (!_lights.ContainsKey(id) && _lights.Count >= 128) return;
        Enabled = true;
        _lights[id] = new Light
        {
            Position = new Vector2(x, y), Radius = Math.Clamp(radius, 1, 1800),
            Intensity = Math.Clamp(intensity, 0, 4), Color = Vector3.Clamp(new Vector3(r, g, b), Vector3.Zero, Vector3.One),
            // PGSL angles are 0=right, 90=up; our pixel-space angle grows downward.
            Direction = -direction * MathF.PI / 180f, HalfAngle = Math.Clamp(spread, 2, 360) * MathF.PI / 360f,
            Cone = spread < 359,
        };
    }
    public void RemoveLight(int id) => _lights.Remove(id);
    public void SetObstacle(int id, float x, float y, float width, float height)
    {
        if (!Finite(x, y, width, height)) return;
        if (width <= 0 || height <= 0) { _occluders.Remove(id); return; }
        if (_occluders.Count < 256 || _occluders.ContainsKey(id))
            _occluders[id] = new RectangleF(x, y, width, height);
    }
    public void SetContact(int id, float x, float y, float width, float height, float alpha)
    {
        if (!Finite(x, y, width, height) || !float.IsFinite(alpha)) return;
        if (alpha <= 0 || width <= 0 || height <= 0) { _contacts.Remove(id); return; }
        if (_contacts.Count < 256 || _contacts.ContainsKey(id))
            _contacts[id] = new Contact(x, y, width, height, Math.Clamp(alpha, 0, 1));
    }

    public bool LineClear(float x1, float y1, float x2, float y2)
    {
        if (!Finite(x1, y1, x2, y2)) return false;
        Vector2 from = new(x1, y1), delta = new(x2 - x1, y2 - y1);
        float distance = delta.Length();
        if (distance < 0.001f) return true;
        Vector2 direction = delta / distance;
        foreach (RectangleF box in _occluders.Values)
            if (RayRectangle(from, direction, box, distance) < distance - 0.05f) return false;
        return true;
    }

    /// <summary>First positive segment hit, including the case where the origin is inside a blocker.</summary>
    public static float RayRectangle(Vector2 origin, Vector2 direction, RectangleF box, float maximum)
    {
        float near = 0, far = maximum;
        if (!ClipRay(origin.X, direction.X, box.Left, box.Right, ref near, ref far)
            || !ClipRay(origin.Y, direction.Y, box.Top, box.Bottom, ref near, ref far)) return maximum;
        return near >= 0 && near <= maximum ? near : maximum;
    }
    private static bool ClipRay(float origin, float direction, float min, float max, ref float near, ref float far)
    {
        if (MathF.Abs(direction) < 0.000001f) return origin >= min && origin <= max;
        float a = (min - origin) / direction, b = (max - origin) / direction;
        if (a > b) (a, b) = (b, a);
        near = MathF.Max(near, a); far = MathF.Min(far, b);
        return far >= near;
    }
    private static bool Finite(float a, float b, float c, float d) =>
        float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c) && float.IsFinite(d);

    public void Emit(string project, string asset, float x, float y, int count, float direction,
        bool flow = false, float targetX = 0, float targetY = 0)
    {
        if (Paused || !Finite(x, y, targetX, targetY) || !float.IsFinite(direction) || count <= 0 || string.IsNullOrWhiteSpace(asset)) return;
        if (!_configs.TryGetValue(asset, out ParticleConfig[] configurations))
        {
            ParticleConfig effect = ParticleAssetLoader.Load(project, asset);
            configurations = ParticleAssetLoader.EnumerateEnabledEmitters(effect).Select(e => e.Config).ToArray();
            _configs[asset] = configurations;
        }
        Enabled = true;
        foreach (ParticleConfig config in configurations)
        {
            int emit = Math.Min(Math.Clamp(count, 0, 64), ParticleBudget - _count);
            for (int i = 0; i < emit; i++)
            {
                float angle = -(direction + ((Next() * 2 - 1) * (float)config.SpreadDegrees)) * MathF.PI / 180f;
                float speed = (float)config.Speed * (1 + ((Next() * 2 - 1) * (float)config.SpeedVariance));
                Vector2 jitter = new((Next() * 2 - 1) * (float)config.EmitRadius, (Next() * 2 - 1) * (float)config.EmitRadius);
                Vector2 position = new Vector2(x, y) + jitter;
                _particles[_count++] = new Particle
                {
                    Position = position, Source = position, Target = new Vector2(targetX, targetY),
                    Velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed,
                    Life = Math.Clamp((float)config.Lifetime * (1 + ((Next() * 2 - 1) * (float)config.LifetimeVariance)), 0.03f, 12f),
                    Phase = Next() * MathF.Tau, Rotation = (Next() * 2 - 1) * (float)config.RotationVariance * 180,
                    Flow = flow, Config = config,
                };
            }
        }
    }
    private float Next() => (float)_random.NextDouble();

    public void Advance(float delta)
    {
        if (!Enabled || Paused || !float.IsFinite(delta)) return;
        delta = Math.Clamp(delta, 0, 0.05f); _time += delta;
        for (int i = 0; i < _count;)
        {
            ref Particle particle = ref _particles[i];
            particle.Age += delta;
            if (particle.Age >= particle.Life)
            {
                _particles[i] = _particles[--_count]; _particles[_count] = default; continue;
            }
            float t = particle.Age / particle.Life;
            ParticleConfig config = particle.Config;
            if (particle.Flow)
            {
                Vector2 d = particle.Target - particle.Source;
                float length = d.Length();
                Vector2 normal = length > 0.001f ? new Vector2(-d.Y, d.X) / length : Vector2.Zero;
                float curve = MathF.Sin(particle.Phase + t * MathF.Tau * 2) * (1 - t) * MathF.Min(12, length * .13f);
                particle.Position = Vector2.Lerp(particle.Source, particle.Target, t) + normal * curve;
            }
            else
            {
                particle.Velocity += new Vector2((float)config.GravityX, -(float)config.Gravity) * delta;
                particle.Velocity *= MathF.Max(0, 1 - (float)config.Drag * delta);
                particle.Position += (particle.Velocity + new Vector2((float)config.WindX, 0)) * delta;
            }
            particle.Rotation += (float)config.RotationSpeed * delta;
            i++;
        }
    }

    public void Render(IRenderController renderer, IRenderCommandSink commands, string project,
        int viewport, float offsetX, float offsetY, float zoom, int width, int height)
    {
        if (!Enabled || renderer == null || commands == null || width <= 0 || height <= 0 || zoom <= 0) return;
        if (!ReferenceEquals(_renderer, renderer)) { ReleaseGraphics(); _renderer = renderer; }
        if (!_halo.IsValid) _halo = renderer.CreateTexture(96, 96, CreateHalo(96));
        foreach (Contact shadow in _contacts.Values)
            Submit(commands, _halo, shadow.X * zoom + offsetX, shadow.Y * zoom + offsetY,
                shadow.Width * zoom, shadow.Height * zoom, new RenderColor(.005f, .012f, .025f), shadow.Alpha, 4);

        RenderMask(renderer, commands, viewport, offsetX, offsetY, zoom, width, height);
        // Glow lies over the lightmap, never over GUI. Broad source stamps have smooth opacity.
        foreach (Light light in _visibleLights)
        {
            if (light.Cone) continue;
            float size = MathF.Min(light.Radius * .65f, 72) * zoom;
            Submit(commands, _halo, light.Position.X * zoom + offsetX, light.Position.Y * zoom + offsetY,
                size, size, new RenderColor(light.Color.X, light.Color.Y, light.Color.Z),
                MathF.Min(.28f, light.Intensity * .13f), -8020);
        }
        for (int i = 0; i < _count; i++)
        {
            ref Particle particle = ref _particles[i];
            ParticleConfig config = particle.Config;
            float t = particle.Age / particle.Life;
            float sizeT = config.UseCustomSizeCurve ? (config.SizeOverLifetime?.Evaluate(t) ?? t) : ParticleCurveMath.Evaluate(config.SizeCurve, t);
            float size = (float)(config.StartSize + (config.EndSize - config.StartSize) * sizeT);
            float sx = particle.Position.X * zoom + offsetX, sy = particle.Position.Y * zoom + offsetY;
            float w = size * (float)config.SizeXScale * zoom, h = size * (float)config.SizeYScale * zoom;
            if (sx < -w || sx > width + w || sy < -h || sy > height + h) continue;
            ParticleColor start = config.StartColor, end = config.EndColor;
            float u = t;
            if (config.MidColor != null)
            {
                float mid = Math.Clamp((float)config.ColorMidpoint, .001f, .999f);
                if (t <= mid) { end = config.MidColor; u = t / mid; }
                else { start = config.MidColor; u = (t - mid) / (1 - mid); }
            }
            float alphaT = config.UseCustomAlphaCurve ? (config.AlphaOverLifetime?.Evaluate(t) ?? t) : ParticleCurveMath.Evaluate(config.AlphaCurve, t);
            float alpha = (config.StartColor.A + (config.EndColor.A - config.StartColor.A) * alphaT) * MathF.Min(1, t * 15);
            RenderColor color = new(start.R + (end.R - start.R) * u, start.G + (end.G - start.G) * u, start.B + (end.B - start.B) * u);
            TextureHandle texture = ParticleTexture(project, config.TexturePath);
            Submit(commands, texture, sx, sy, w, h, color, Math.Clamp(alpha, 0, 1), -8040, particle.Rotation);
        }
    }

    private void RenderMask(IRenderController renderer, IRenderCommandSink commands, int viewport,
        float offsetX, float offsetY, float zoom, int width, int height)
    {
        // Bound CPU work and upload size independently of 4K/ultrawide windows.
        int mw = Math.Clamp(width / 4, 160, 320), mh = Math.Clamp((int)MathF.Round(mw * (height / (float)width)), 90, 240);
        if (!_masks.TryGetValue(viewport, out Mask mask)) _masks[viewport] = mask = new Mask();
        if (mask.Width != mw || mask.Height != mh)
        {
            if (mask.Texture.IsValid) renderer.ReleaseTexture(mask.Texture);
            mask.Width = mw; mask.Height = mh; mask.Pixels = new byte[mw * mh * 4]; mask.Illumination = new float[mw * mh]; mask.Texture = default;
        }
        Array.Fill(mask.Illumination, _ambient);
        float originX = -offsetX / zoom, originY = -offsetY / zoom;
        float unitsX = width / (zoom * mw), unitsY = height / (zoom * mh);
        Vector2 centre = new(originX + width / zoom * .5f, originY + height / zoom * .5f);
        _visibleLights.Clear();
        foreach (Light light in _lights.Values)
            if (light.Position.X + light.Radius >= originX && light.Position.X - light.Radius <= originX + width / zoom
                && light.Position.Y + light.Radius >= originY && light.Position.Y - light.Radius <= originY + height / zoom)
                _visibleLights.Add(light);
        _visibleLights.Sort((a, b) => LightPriority(a, centre).CompareTo(LightPriority(b, centre)));
        if (_visibleLights.Count > VisibleLightBudget) _visibleLights.RemoveRange(VisibleLightBudget, _visibleLights.Count - VisibleLightBudget);
        foreach (Light light in _visibleLights)
        {
            for (int ray = 0; ray < RayCount; ray++)
            {
                float angle = ray * MathF.Tau / RayCount;
                Vector2 direction = new(MathF.Cos(angle), MathF.Sin(angle));
                float distance = light.Radius;
                foreach (RectangleF obstacle in _occluders.Values)
                    distance = MathF.Min(distance, RayRectangle(light.Position, direction, obstacle, light.Radius));
                _rays[ray] = distance;
            }
            int minX = Math.Clamp((int)((light.Position.X - light.Radius - originX) / unitsX), 0, mw - 1);
            int maxX = Math.Clamp((int)((light.Position.X + light.Radius - originX) / unitsX) + 1, 0, mw - 1);
            int minY = Math.Clamp((int)((light.Position.Y - light.Radius - originY) / unitsY), 0, mh - 1);
            int maxY = Math.Clamp((int)((light.Position.Y + light.Radius - originY) / unitsY) + 1, 0, mh - 1);
            float radiusSquared = light.Radius * light.Radius;
            float dirX = MathF.Cos(light.Direction), dirY = MathF.Sin(light.Direction);
            float outer = MathF.Cos(light.HalfAngle), inner = MathF.Cos(light.HalfAngle * .70f);
            float flicker = light.Cone ? 1 : .97f + .03f * MathF.Sin(_time * 13 + light.Position.X);
            for (int py = minY; py <= maxY; py++)
            for (int px = minX; px <= maxX; px++)
            {
                float dx = originX + (px + .5f) * unitsX - light.Position.X;
                float dy = originY + (py + .5f) * unitsY - light.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > radiusSquared) continue;
                float distance = MathF.Sqrt(distanceSquared), cone = 1;
                if (light.Cone && distance > 1)
                {
                    float dot = (dx * dirX + dy * dirY) / distance;
                    cone = Smooth((dot - outer) / MathF.Max(.001f, inner - outer));
                    if (cone <= 0) continue;
                }
                float angle = MathF.Atan2(dy, dx);
                if (angle < 0) angle += MathF.Tau;
                float rayPosition = angle * (RayCount / MathF.Tau);
                int rayIndex = (int)rayPosition % RayCount;
                float rayDistance = MathF.Min(_rays[rayIndex], _rays[(rayIndex + 1) % RayCount]);
                float shadow = Smooth((rayDistance - distance) / 3.5f);
                float falloff = 1 - distanceSquared / radiusSquared;
                float value = falloff * falloff * light.Intensity * flicker * cone * shadow;
                int pixel = py * mw + px;
                mask.Illumination[pixel] = MathF.Min(1, mask.Illumination[pixel] + value);
            }
        }
        byte r = (byte)(_shadowColor.X * 255), g = (byte)(_shadowColor.Y * 255), b = (byte)(_shadowColor.Z * 255);
        for (int pixel = 0; pixel < mask.Illumination.Length; pixel++)
        {
            int p = pixel * 4; mask.Pixels[p] = r; mask.Pixels[p + 1] = g; mask.Pixels[p + 2] = b;
            mask.Pixels[p + 3] = (byte)Math.Clamp((1 - mask.Illumination[pixel]) * 242, 0, 255);
        }
        if (!mask.Texture.IsValid) mask.Texture = renderer.CreateTexture(mw, mh, mask.Pixels);
        else renderer.UpdateTexture(mask.Texture, mw, mh, mask.Pixels);
        Submit(commands, mask.Texture, width * .5f, height * .5f, width, height, RenderColor.White, 1, -8000);
    }
    private static float LightPriority(Light light, Vector2 centre) =>
        light.Cone ? -1000000 : Vector2.DistanceSquared(light.Position, centre) / MathF.Max(1, light.Intensity);
    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    private TextureHandle ParticleTexture(string project, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return _halo;
        if (_textures.TryGetValue(path, out TextureHandle texture)) return texture.IsValid ? texture : _halo;
        string resolved = Path.IsPathRooted(path) ? path : Path.Combine(project, path.Replace('/', Path.DirectorySeparatorChar));
        texture = File.Exists(resolved) ? _renderer.LoadTexture(resolved) : default;
        _textures[path] = texture;
        return texture.IsValid ? texture : _halo;
    }
    private static void Submit(IRenderCommandSink commands, TextureHandle texture, float cx, float cy,
        float width, float height, RenderColor tint, float alpha, int depth, float rotation = 0)
    {
        if (!texture.IsValid || width <= 0 || height <= 0 || alpha <= 0) return;
        commands.DrawSprite(new SpriteDrawCall
        {
            Texture = texture, X = cx, Y = cy, Width = width, Height = height,
            OriginX = width * .5f, OriginY = height * .5f, ScaleX = 1, ScaleY = 1,
            Tint = tint, Alpha = alpha, Depth = depth, Rotation = rotation, SmoothSampling = true,
        });
    }
    private static byte[] CreateHalo(int size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
        {
            float dx = (x + .5f) / size * 2 - 1, dy = (y + .5f) / size * 2 - 1;
            float a = MathF.Max(0, 1 - dx * dx - dy * dy); a *= a;
            int i = (y * size + x) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 255; pixels[i + 3] = (byte)(a * 255);
        }
        return pixels;
    }
    private void ReleaseGraphics()
    {
        if (_renderer != null)
        {
            if (_halo.IsValid) _renderer.ReleaseTexture(_halo);
            foreach (Mask mask in _masks.Values) if (mask.Texture.IsValid) _renderer.ReleaseTexture(mask.Texture);
            foreach (TextureHandle texture in _textures.Values) if (texture.IsValid) _renderer.ReleaseTexture(texture);
        }
        _halo = default; _masks.Clear(); _textures.Clear();
    }
    public void Dispose()
    {
        ReleaseGraphics(); _renderer = null; _count = 0; Array.Clear(_particles);
        _lights.Clear(); _occluders.Clear(); _contacts.Clear(); _configs.Clear(); Enabled = false; Paused = false; _time = 0;
    }
}
