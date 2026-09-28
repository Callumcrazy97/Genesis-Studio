using System;
using System.Collections.Generic;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Particles;

public sealed partial class ParticleSimulation
{
    private const int PlanarTrailSamples = 8;
    private const int TrailFadeSubsegments = 4;
    private Vector4[] _planarTrails = Array.Empty<Vector4>();
    private readonly Dictionary<uint, int> _planarSerials = new();
    private uint _nextBirthSerial, _lastBirthSerial;
    private float _planarSeconds;

    // The shared ordinary-sprite renderer accepts 32768 commands per frame. A segment is a
    // real sprite command, so retain that bound for Software rather than allocating unbounded arrays.
    public int SpriteCapacity2D => Math.Max(1, Math.Min(32768, Capacity *
        (_config.IsPlanar2D && _config.RendererKind == ParticleRendererKind.Trail ? (PlanarTrailSamples - 1) * TrailFadeSubsegments
            : _config.IsPlanar2D && _config.RendererKind == ParticleRendererKind.Beam ? PlanarTrailSamples - 1 : 1)));

    private void RemoveParticle(int index)
    {
        _count--;
        if (index >= _count) return;
        _particles[index] = _particles[_count];
        if (_planarTrails.Length > 0)
            Array.Copy(_planarTrails, _count * PlanarTrailSamples, _planarTrails, index * PlanarTrailSamples, PlanarTrailSamples);
    }

    private void EnsurePlanarTrails()
    {
        if (!_config.IsPlanar2D || _config.RendererKind != ParticleRendererKind.Trail)
        {
            _planarTrails = Array.Empty<Vector4>();
            return;
        }
        int length = Capacity * PlanarTrailSamples;
        if (_planarTrails.Length == length) return;
        bool fresh = _planarTrails.Length == 0;
        Array.Resize(ref _planarTrails, length);
        if (fresh) for (int index = 0; index < _count; index++) InitialisePlanarTrail(index);
    }

    private void InitialisePlanarTrail(int index)
    {
        if (_planarTrails.Length == 0) return;
        ParticleState particle = _particles[index];
        Array.Fill(_planarTrails, new Vector4(particle.Position, particle.Age), index * PlanarTrailSamples, PlanarTrailSamples);
    }

    private void UpdatePlanarTrail(int index, ref ParticleState particle, float dt)
    {
        int offset = index * PlanarTrailSamples;
        particle.TrailClock += dt;
        if (particle.TrailClock >= Math.Max(.001, _config.TrailDuration / (PlanarTrailSamples - 1)))
        {
            Array.Copy(_planarTrails, offset, _planarTrails, offset + 1, PlanarTrailSamples - 1);
            particle.TrailClock = 0;
        }
        _planarTrails[offset] = new Vector4(particle.Position, particle.Age);
    }

    private Vector3 PlanarWorld(Vector3 position) => _config.SimulationSpace == ParticleSimulationSpace.Local
        ? Vector3.Transform(position, _planarTransform) : position;

    private int FillPlanarSegments(Span<SpriteDrawCall> buffer, float centerX, float centerY, float zoom, TextureHandle texture)
    {
        if (_config.RendererKind == ParticleRendererKind.Ribbon)
        {
            _planarSerials.Clear();
            for (int index = 0; index < _count; index++) _planarSerials[_particles[index].Serial] = index;
        }
        float widthScale = (float)_config.SizeXScale * new Vector2(_planarTransform.M11, _planarTransform.M12).Length();
        int written = 0;
        for (int index = 0; index < _count && written < buffer.Length; index++)
        {
            ParticleState particle = _particles[index];
            float time = Math.Clamp(particle.Age / MathF.Max(.05f, particle.Life), 0, 1);
            float sizeTime = _config.UseCustomSizeCurve ? _config.SizeOverLifetime?.Evaluate(time) ?? time
                : ParticleCurveMath.Evaluate(_config.SizeCurve, time);
            float width = Lerp((float)_config.StartSize, (float)_config.EndSize, sizeTime) * (float)_config.TrailWidth * widthScale;
            if (_config.RendererKind == ParticleRendererKind.Ribbon)
            {
                if (!_planarSerials.TryGetValue(particle.PreviousSerial, out int previous)
                    || _particles[previous].Age >= _particles[previous].Life) continue;
                Vector3 start = PlanarWorld(particle.Position), end = PlanarWorld(_particles[previous].Position);
                if (Vector3.Distance(start, end) > _config.RibbonMaxSegmentLength) continue;
                AddPlanarSegment(buffer, ref written, start, end, width, PlanarColor(particle, particle.Age), texture, centerX, centerY, zoom, 0, 1);
                continue;
            }
            for (int segment = 0; segment < PlanarTrailSamples - 1 && written < buffer.Length; segment++)
            {
                float a = segment / (float)(PlanarTrailSamples - 1), b = (segment + 1) / (float)(PlanarTrailSamples - 1);
                Vector3 start, end;
                RenderColor colour;
                if (_config.RendererKind == ParticleRendererKind.Trail)
                {
                    Vector4 head = _planarTrails[index * PlanarTrailSamples + segment];
                    Vector4 tail = _planarTrails[index * PlanarTrailSamples + segment + 1];
                    Vector3 headPosition = PlanarWorld(new Vector3(head.X, head.Y, head.Z));
                    Vector3 tailPosition = PlanarWorld(new Vector3(tail.X, tail.Y, tail.Z));
                    for (int subsegment = 0; subsegment < TrailFadeSubsegments && written < buffer.Length; subsegment++)
                    {
                        float startFraction = subsegment / (float)TrailFadeSubsegments;
                        float endFraction = (subsegment + 1) / (float)TrailFadeSubsegments;
                        start = Vector3.Lerp(headPosition, tailPosition, startFraction);
                        end = Vector3.Lerp(headPosition, tailPosition, endFraction);
                        float age = Lerp(head.W, tail.W, (startFraction + endFraction) * .5f);
                        colour = PlanarColor(particle, age);
                        colour = new RenderColor(colour.R, colour.G, colour.B,
                            colour.A * Math.Clamp(1 - (particle.Age - age) / MathF.Max(.001f, (float)_config.TrailDuration), 0, 1));
                        AddPlanarSegment(buffer, ref written, start, end, width, colour, texture, centerX, centerY, zoom,
                            a + (b - a) * startFraction, a + (b - a) * endFraction);
                    }
                    continue;
                }
                else
                {
                    Vector3 origin = PlanarWorld(particle.Position);
                    Vector3 target = Vector3.Transform(new Vector3((float)_config.BeamEndX, (float)_config.BeamEndY, 0), _planarTransform);
                    start = BeamPoint(origin, target, a); end = BeamPoint(origin, target, b);
                    colour = PlanarColor(particle, particle.Age);
                }
                AddPlanarSegment(buffer, ref written, start, end, width, colour, texture, centerX, centerY, zoom, a, b);
            }
        }
        return written;
    }

    private Vector3 BeamPoint(Vector3 start, Vector3 end, float fraction)
    {
        float envelope = MathF.Sin(fraction * MathF.PI) * (float)_config.BeamNoise;
        return Vector3.Lerp(start, end, fraction) + new Vector3(MathF.Sin(fraction * 31 + _planarSeconds * 13),
            -MathF.Cos(fraction * 19 - _planarSeconds * 9), 0) * envelope;
    }

    private RenderColor PlanarColor(in ParticleState particle, float age) => ApplyColorJitter(EvaluateGradient(
        Math.Clamp(age / MathF.Max(.05f, particle.Life), 0, 1), _config.StartColor ?? new ParticleColor(),
        _config.MidColor, _config.EndColor ?? new ParticleColor(), (float)_config.ColorMidpoint, _config.AlphaCurve), particle.ColorJitter);

    private void AddPlanarSegment(Span<SpriteDrawCall> buffer, ref int written, Vector3 start, Vector3 end, float width,
        RenderColor colour, TextureHandle texture, float centerX, float centerY, float zoom, float v0, float v1)
    {
        Vector2 delta = new(end.X - start.X, end.Y - start.Y);
        float length = delta.Length();
        if (length < .0001f || width <= 0 || colour.A <= .001f) return;
        buffer[written++] = new SpriteDrawCall
        {
            Texture = texture, X = centerX + start.X * zoom, Y = centerY + start.Y * zoom,
            Width = width * zoom, Height = length * zoom, OriginX = width * zoom * .5f,
            Rotation = MathF.Atan2(delta.Y, delta.X) * (180f / MathF.PI) - 90,
            Alpha = colour.A, Tint = new RenderColor(colour.R, colour.G, colour.B, 1),
            SmoothSampling = true, UvRect = new Vector4(0, v0, 1, v1),
            Blend = _config.BlendMode switch
            {
                ParticleBlendMode.Additive => BlendMode.Additive, ParticleBlendMode.Multiply => BlendMode.Multiply, _ => BlendMode.Alpha,
            },
        };
    }
}
