#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace Genesis.Runtime.Modeling;

public readonly record struct WeightedClip(string Clip, float Weight, float Time, bool Loop);

public abstract class AnimationBlendTree
{
    public abstract IReadOnlyList<WeightedClip> Evaluate(IReadOnlyDictionary<string, object> parameters, float time);
    protected static float Number(IReadOnlyDictionary<string, object> parameters, string key) =>
        parameters.TryGetValue(key, out object? value)
        && float.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float number) && float.IsFinite(number) ? number : 0f;
}

public sealed class ClipNode(string clip, bool loop = true) : AnimationBlendTree
{
    public override IReadOnlyList<WeightedClip> Evaluate(IReadOnlyDictionary<string, object> parameters, float time) =>
        [new(clip, 1f, time, loop)];
}

public sealed class BlendTree1D(string parameter) : AnimationBlendTree
{
    private readonly SortedList<float, string> _clips = new();
    public void AddClip(string clip, float threshold)
    {
        if (!float.IsFinite(threshold) || string.IsNullOrWhiteSpace(clip))
            throw new ArgumentException("A blend clip needs a name and a finite threshold.");
        _clips[threshold] = clip;
    }
    public override IReadOnlyList<WeightedClip> Evaluate(IReadOnlyDictionary<string, object> parameters, float time)
    {
        if (_clips.Count == 0) return [];
        float value = Number(parameters, parameter);
        if (value <= _clips.Keys[0]) return [new(_clips.Values[0], 1, time, true)];
        for (int i = 1; i < _clips.Count; i++)
        {
            if (value > _clips.Keys[i]) continue;
            float weight = (value - _clips.Keys[i - 1]) / (_clips.Keys[i] - _clips.Keys[i - 1]);
            return [new(_clips.Values[i - 1], 1 - weight, time, true), new(_clips.Values[i], weight, time, true)];
        }
        return [new(_clips.Values[^1], 1, time, true)];
    }
}

public sealed class DirectBlend : AnimationBlendTree
{
    private readonly Dictionary<string, float> _clips = new(StringComparer.OrdinalIgnoreCase);
    public void SetWeight(string clip, float weight)
    {
        if (!float.IsFinite(weight) || weight < 0 || string.IsNullOrWhiteSpace(clip))
            throw new ArgumentException("A direct blend needs a clip and a nonnegative finite weight.");
        _clips[clip] = weight;
    }
    public override IReadOnlyList<WeightedClip> Evaluate(IReadOnlyDictionary<string, object> parameters, float time)
    {
        double total = _clips.Values.Sum(v => (double)v);
        return total <= 0 ? [] : _clips.Where(pair => pair.Value > 0)
            .Select(pair => new WeightedClip(pair.Key, (float)(pair.Value / total), time, true)).ToArray();
    }
}

/// <summary>Small explicit condition grammar; never evaluates C# or PGSL from condition strings.</summary>
public sealed class AnimationCondition
{
    private readonly string _parameter;
    private readonly string _operator;
    private readonly double _value;
    public AnimationCondition(string expression)
    {
        expression = expression.Trim();
        foreach (string op in new[] { ">=", "<=", "!=", "==", ">", "<" })
        {
            int index = expression.IndexOf(op, StringComparison.Ordinal);
            if (index < 0) continue;
            _parameter = expression[..index].Trim();
            _operator = op;
            string right = expression[(index + op.Length)..].Trim();
            if (bool.TryParse(right, out bool flag)) _value = flag ? 1 : 0;
            else if (!double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out _value)
                || !double.IsFinite(_value)) throw new ArgumentException("Invalid animation condition value.");
            if (_parameter.Length == 0) throw new ArgumentException("Missing animation parameter.");
            return;
        }
        bool negate = expression.StartsWith('!');
        _parameter = negate ? expression[1..].Trim() : expression;
        if (_parameter.Length == 0) throw new ArgumentException("Missing animation condition.");
        _operator = negate ? "==" : "!=";
        _value = 0;
    }
    public bool Matches(IReadOnlyDictionary<string, object> parameters, bool finished)
    {
        double value = 0;
        if (string.Equals(_parameter, "AnimationFinished", StringComparison.OrdinalIgnoreCase)) value = finished ? 1 : 0;
        else if (parameters.TryGetValue(_parameter, out object? raw))
        {
            if (raw is bool flag) value = flag ? 1 : 0;
            else if (!double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
        }
        else return false;
        return _operator switch
        {
            ">=" => value >= _value, "<=" => value <= _value, ">" => value > _value,
            "<" => value < _value, "==" => value == _value, _ => value != _value,
        };
    }
}

public sealed class AnimationStateMachine
{
    private readonly Dictionary<string, AnimationBlendTree> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string From, string To, AnimationCondition Condition, float Duration)> _transitions = [];
    private string? _previous;
    private float _previousTime, _elapsed, _duration;
    public string CurrentState { get; private set; } = "";
    public float Time { get; private set; }
    public bool Contains(string state) => _states.ContainsKey(state);
    public bool HasFinished(IReadOnlyDictionary<string, object> parameters, GModelAsset? asset) =>
        asset != null && _states.TryGetValue(CurrentState, out AnimationBlendTree? tree)
        && tree.Evaluate(parameters, Time) is { Count: > 0 } clips
        && clips.All(c => !c.Loop && c.Time >= AnimationPose.Duration(asset, c.Clip));
    public void Seek(float time) => Time = float.IsFinite(time) ? MathF.Max(0, time) : 0;
    public void AddState(string name, AnimationBlendTree tree)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("State name cannot be empty.");
        _states[name] = tree;
        if (CurrentState.Length == 0) CurrentState = name;
    }
    public void AddTransition(string from, string to, string condition, float seconds)
    {
        if (!_states.ContainsKey(to) || (from != "*" && !_states.ContainsKey(from)))
            throw new ArgumentException("Create transition states first; use * for any-state transitions.");
        if (!float.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        _transitions.Add((from, to, new AnimationCondition(condition), seconds));
    }
    public void Play(string state, float duration = 0)
    {
        if (!_states.ContainsKey(state)) throw new ArgumentException($"Unknown animation state '{state}'.");
        _previous = duration > 0 ? CurrentState : null;
        _previousTime = Time;
        _elapsed = 0;
        _duration = duration;
        CurrentState = state;
        Time = 0;
    }
    public void Advance(float dt, float speed, IReadOnlyDictionary<string, object> parameters, GModelAsset? asset)
    {
        Time += dt * speed;
        _previousTime += dt * speed;
        _elapsed += dt;
        if (_elapsed >= _duration) _previous = null;
        bool finished = HasFinished(parameters, asset);
        // Stable authored order, any-state transitions first. One transition per simulation tick.
        foreach (var transition in _transitions.OrderBy(t => t.From == "*" ? 0 : 1))
        {
            if (string.Equals(transition.To, CurrentState, StringComparison.OrdinalIgnoreCase)) continue;
            if (transition.From != "*" && !string.Equals(transition.From, CurrentState, StringComparison.OrdinalIgnoreCase)) continue;
            if (!transition.Condition.Matches(parameters, finished)) continue;
            Play(transition.To, transition.Duration);
            break;
        }
    }
    public IReadOnlyList<WeightedClip> Evaluate(IReadOnlyDictionary<string, object> parameters)
    {
        if (!_states.TryGetValue(CurrentState, out AnimationBlendTree? tree)) return [];
        var current = tree.Evaluate(parameters, Time);
        if (_previous == null || !_states.TryGetValue(_previous, out AnimationBlendTree? previous)) return current;
        float blend = _duration <= 0 ? 1 : Math.Clamp(_elapsed / _duration, 0, 1);
        return previous.Evaluate(parameters, _previousTime).Select(c => c with { Weight = c.Weight * (1 - blend) })
            .Concat(current.Select(c => c with { Weight = c.Weight * blend })).ToArray();
    }
}

public enum AnimationLayerMode { Override, Additive }

public sealed class AnimationLayer
{
    public AnimationStateMachine StateMachine { get; } = new();
    public HashSet<string> BoneMask { get; } = new(StringComparer.OrdinalIgnoreCase);
    public float Weight { get; set; } = 1;
    public AnimationLayerMode Mode { get; set; }
}

/// <summary>Per-entity graph. Only simulation advances clocks; rendering may evaluate it repeatedly.</summary>
public sealed class AnimationController
{
    private GModelAsset? _lastAsset;
    public bool ActiveStateHasFinished => Layers[ActiveLayer].StateMachine.HasFinished(Parameters, _lastAsset);
    public Guid Id { get; } = Guid.NewGuid();
    public Dictionary<string, object> Parameters { get; }
    public Dictionary<int, AnimationBlendTree> Trees { get; } = [];
    public SortedDictionary<int, AnimationLayer> Layers { get; } = new() { [0] = new() };
    public int ActiveLayer { get; set; }
    public bool RootMotionEnabled { get; set; }
    public Vector3 RootMotionDelta { get; private set; }
    public Quaternion RootMotionRotation { get; private set; } = Quaternion.Identity;
    public AnimationController(Dictionary<string, object>? parameters = null) =>
        Parameters = parameters ?? new(StringComparer.OrdinalIgnoreCase);
    public void ClearRootMotion() { RootMotionDelta = Vector3.Zero; RootMotionRotation = Quaternion.Identity; }
    public void Advance(float dt, float speed, GModelAsset? asset)
    {
        _lastAsset = asset;
        ClearRootMotion();
        if (!float.IsFinite(dt) || !float.IsFinite(speed) || dt <= 0) return;
        // Integrate the outgoing contributions before transitions reset their clocks. Sampling the
        // same weights on both ends prevents parameter changes from teleporting the root.
        if (RootMotionEnabled && asset?.Rig?.Bones is { Count: > 0 } bones)
        {
            int root = bones.FindIndex(b => b.ParentIndex < 0);
            if (root >= 0)
            {
                var layer = Layers[0];
                foreach (WeightedClip clip in layer.StateMachine.Evaluate(Parameters))
                {
                    if (clip.Weight <= 0) continue;
                    Matrix4x4 a = AnimationPose.SampleRoot(asset, clip, root);
                    Matrix4x4 b = AnimationPose.SampleRoot(asset, clip with { Time = clip.Time + dt * speed }, root);
                    RootMotionDelta += (b.Translation - a.Translation) * clip.Weight;
                    if (Matrix4x4.Decompose(a, out _, out Quaternion qa, out _)
                        && Matrix4x4.Decompose(b, out _, out Quaternion qb, out _))
                        RootMotionRotation = Quaternion.Normalize(RootMotionRotation
                            * Quaternion.Slerp(Quaternion.Identity, Quaternion.Inverse(qa) * qb, clip.Weight));
                }
            }
        }
        foreach (AnimationLayer layer in Layers.Values) layer.StateMachine.Advance(dt, speed, Parameters, asset);
    }
    public Matrix4x4[] Evaluate(GModelAsset asset)
    {
        if (asset.Rig?.Bones == null) return [];
        Matrix4x4[] result = asset.Rig.Bones.Select(b => b.BindLocal).ToArray();
        foreach (AnimationLayer layer in Layers.Values)
        {
            var clips = layer.StateMachine.Evaluate(Parameters).Where(c => c.Weight > 0).ToArray();
            if (clips.Length == 0) continue;
            Matrix4x4[] pose = AnimationPose.Sample(asset, clips[0]);
            float total = clips[0].Weight;
            foreach (WeightedClip clip in clips.Skip(1))
            {
                Matrix4x4[] next = AnimationPose.Sample(asset, clip);
                float blend = clip.Weight / (total + clip.Weight);
                for (int i = 0; i < pose.Length; i++) pose[i] = AnimationPose.Blend(pose[i], next[i], blend);
                total += clip.Weight;
            }
            float weight = float.IsFinite(layer.Weight) ? Math.Clamp(layer.Weight, 0, 1) : 0;
            for (int i = 0; i < result.Length; i++)
            {
                if (layer.BoneMask.Count > 0 && !layer.BoneMask.Contains(asset.Rig.Bones[i].Name)) continue;
                result[i] = layer.Mode == AnimationLayerMode.Override
                    ? AnimationPose.Blend(result[i], pose[i], weight)
                    : AnimationPose.Add(result[i], pose[i], asset.Rig.Bones[i].BindLocal, weight);
            }
        }
        if (RootMotionEnabled)
            for (int i = 0; i < result.Length; i++)
                if (asset.Rig.Bones[i].ParentIndex < 0) result[i] = asset.Rig.Bones[i].BindLocal;
        return result;
    }
}

public static class AnimationPose
{
    public static float Duration(GModelAsset asset, string name)
    {
        var clip = asset.Animations?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return clip?.Frames is { Count: > 0 } ? (clip.Frames.Count - 1) / MathF.Max(1, clip.Fps) : float.PositiveInfinity;
    }
    public static Matrix4x4[] Sample(GModelAsset asset, WeightedClip sample)
    {
        var bones = asset.Rig.Bones;
        Matrix4x4[] result = bones.Select(b => b.BindLocal).ToArray();
        var clip = asset.Animations?.FirstOrDefault(c => string.Equals(c.Name, sample.Clip, StringComparison.OrdinalIgnoreCase));
        if (clip?.Frames is not { Count: > 0 }) return result;
        float frame = sample.Time * MathF.Max(1, clip.Fps);
        frame = sample.Loop ? ((frame % clip.Frames.Count) + clip.Frames.Count) % clip.Frames.Count
            : Math.Clamp(frame, 0, clip.Frames.Count - 1);
        int first = (int)MathF.Floor(frame);
        int second = sample.Loop ? (first + 1) % clip.Frames.Count : Math.Min(first + 1, clip.Frames.Count - 1);
        var a = clip.Frames[first].LocalBoneTransforms;
        var b = clip.Frames[second].LocalBoneTransforms;
        for (int i = 0; i < result.Length; i++)
            result[i] = Blend(a != null && i < a.Length ? a[i] : result[i],
                b != null && i < b.Length ? b[i] : result[i], frame - first);
        return result;
    }
    public static Matrix4x4 SampleRoot(GModelAsset asset, WeightedClip sample, int root)
    {
        var clip = asset.Animations?.FirstOrDefault(c => string.Equals(c.Name, sample.Clip, StringComparison.OrdinalIgnoreCase));
        if (!sample.Loop || clip?.Frames is not { Count: > 1 }) return Sample(asset, sample)[root];
        float duration = clip.Frames.Count / MathF.Max(1, clip.Fps);
        float cycles = MathF.Floor(sample.Time / duration);
        var start = Sample(asset, sample with { Time = 0, Loop = false })[root];
        var end = Sample(asset, sample with { Time = duration, Loop = false })[root];
        var local = Sample(asset, sample with { Time = sample.Time - cycles * duration, Loop = false })[root];
        // Translation is unwrapped across loops, including negative playback and multi-loop steps.
        local.Translation += (end.Translation - start.Translation) * cycles;
        if (Matrix4x4.Decompose(start, out _, out var firstRotation, out _)
            && Matrix4x4.Decompose(end, out _, out var lastRotation, out _)
            && Matrix4x4.Decompose(local, out var scale, out var localRotation, out var position))
        {
            Quaternion change = Quaternion.Normalize(Quaternion.Inverse(firstRotation) * lastRotation);
            if (change.W < 0) change = new Quaternion(-change.X, -change.Y, -change.Z, -change.W);
            Vector3 axis = new(change.X, change.Y, change.Z);
            Quaternion accumulated = axis.LengthSquared() < 1e-10f ? Quaternion.Identity
                : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), 2 * MathF.Acos(Math.Clamp(change.W, -1, 1)) * cycles);
            local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(
                firstRotation * accumulated * Quaternion.Inverse(firstRotation) * localRotation)) * Matrix4x4.CreateTranslation(position);
        }
        return local;
    }
    public static Matrix4x4 Blend(Matrix4x4 a, Matrix4x4 b, float weight)
    {
        if (!Matrix4x4.Decompose(a, out var sa, out var ra, out var pa)
            || !Matrix4x4.Decompose(b, out var sb, out var rb, out var pb)) return weight < .5f ? a : b;
        return Matrix4x4.CreateScale(Vector3.Lerp(sa, sb, weight))
            * Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(ra, rb, weight))
            * Matrix4x4.CreateTranslation(Vector3.Lerp(pa, pb, weight));
    }
    public static Matrix4x4 Add(Matrix4x4 basis, Matrix4x4 pose, Matrix4x4 bind, float weight)
    {
        if (!Matrix4x4.Decompose(basis, out var s, out var r, out var p)
            || !Matrix4x4.Decompose(pose, out var ps, out var pr, out var pp)
            || !Matrix4x4.Decompose(bind, out var bs, out var br, out var bp)) return basis;
        return Matrix4x4.CreateScale(s + (ps - bs) * weight)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r * Quaternion.Slerp(Quaternion.Identity,
                Quaternion.Inverse(br) * pr, weight))) * Matrix4x4.CreateTranslation(p + (pp - bp) * weight);
    }
}
