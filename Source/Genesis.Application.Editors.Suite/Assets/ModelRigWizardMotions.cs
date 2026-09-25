using System.Numerics;
using System.Security.Cryptography;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed record ModelWizardContact(string Chain, bool Planted, Vector3 Position, Vector3 Target, float LegLength);
public sealed record ModelWizardMotionSample(Matrix4x4[] Locals, Vector3 VirtualTravel, IReadOnlyList<ModelWizardContact> Contacts)
{
    public Matrix4x4 VirtualTransform { get; init; } = Matrix4x4.Identity;
}

public sealed class ModelWizardEditedClipException(string name)
    : InvalidOperationException($"'{name}' has been edited since generation. Create a new copy to keep those edits.");

/// <summary>Editor-only trajectory generation. The saved output uses standard dense animation frames.</summary>
public static class ModelRigWizardMotions
{
    public static GModelAsset Generate(GModelAsset source, IEnumerable<GModelMotionRecipe> recipes,
        bool copyEditedClips = false, CancellationToken token = default, IProgress<string>? progress = null)
    {
        ModelRigWizardWorkflow.ValidateLandmarks(source, true);
        if (source.RigWizard?.Bound != true || !source.Meshes.Any(m=>m.IsSkinned) || source.Meshes.Any(m=>!m.IsSkinned&&m.Vertices.Length>0))
            throw new InvalidOperationException("Bind and review the movement test first.");
        var result = ModelPoseWorkflow.Copy(source);
        foreach (var requested in recipes)
        {
            token.ThrowIfCancellationRequested(); Validate(requested);
            var recipe = ModelPoseWorkflow.Copy(requested);
            var previous = result.Animations.SingleOrDefault(c => c.WizardRecipeId == recipe.Id);
            var saved = result.RigWizard.Motions.SingleOrDefault(m => m.Id == recipe.Id);
            if (previous is not null && (saved is null || Hash(previous) != saved.LastGeneratedHash))
            {
                if (!copyEditedClips) throw new ModelWizardEditedClipException(previous.Name);
                recipe.Id = Guid.NewGuid().ToString("N"); previous = null;
            }
            string name = previous?.Name ?? recipe.Name.Trim();
            if (name.Length == 0) name = recipe.Kind.ToString();
            string stem = name; int suffix = 2;
            while (result.Animations.Any(c => c != previous && c.Name == name)) name = stem + " " + suffix++;
            recipe.Name = name;
            float duration = recipe.Duration / recipe.Tempo;
            bool loop = recipe.Kind is not (GModelMotionKind.Crouch or GModelMotionKind.Jump);
            int frames = Math.Max(2, (int)MathF.Ceiling(duration * recipe.Fps));
            if ((long)frames * result.Rig.Bones.Count > 2_000_000) throw new InvalidOperationException("Reduce clip duration or FPS.");
            var clip = new GModelAnimationClip { Name = name, Fps = recipe.Fps, Loop = loop, WizardRecipeId = recipe.Id };
            progress?.Report("Generating " + name + "…");
            var solver = new Solver(result, recipe);
            for (int f = 0; f < frames; f++)
            {
                token.ThrowIfCancellationRequested();
                float time = duration * f / (loop ? frames : frames - 1f);
                var sample = solver.Sample(time);
                if (sample.Contacts.Any(c => c.Planted && Vector3.Distance(c.Position, c.Target) > c.LegLength * .009f))
                    throw new InvalidOperationException($"{name}: a foot cannot reach its planted target. Review the limb bends or reduce stride, stance width or body sway.");
                clip.Frames.Add(new() { LocalBoneTransforms = sample.Locals });
            }
            if (previous is not null) result.Animations.Remove(previous);
            result.Animations.Add(clip);
            recipe.LastGeneratedHash = Hash(clip);
            result.RigWizard.Motions.RemoveAll(m => m.Id == recipe.Id);
            result.RigWizard.Motions.Add(recipe);
        }
        return result;
    }

    public static ModelWizardMotionSample Sample(GModelAsset source, GModelMotionRecipe recipe, float time)
    {
        Validate(recipe); return new Solver(source, recipe).Sample(time);
    }

    public static string Hash(GModelAnimationClip clip)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(clip.Fps); writer.Write(clip.Loop); writer.Write(clip.Frames.Count);
        foreach (var frame in clip.Frames) foreach (var m in frame.LocalBoneTransforms)
        {
            writer.Write(m.M11); writer.Write(m.M12); writer.Write(m.M13); writer.Write(m.M14);
            writer.Write(m.M21); writer.Write(m.M22); writer.Write(m.M23); writer.Write(m.M24);
            writer.Write(m.M31); writer.Write(m.M32); writer.Write(m.M33); writer.Write(m.M34);
            writer.Write(m.M41); writer.Write(m.M42); writer.Write(m.M43); writer.Write(m.M44);
        }
        foreach (var track in clip.Tracks)
        {
            writer.Write(track.BoneIndex);
            foreach (var key in track.Keys)
            {
                writer.Write(key.Frame);
                foreach (float v in new[] { key.Translation.X, key.Translation.Y, key.Translation.Z, key.Rotation.X,
                    key.Rotation.Y, key.Rotation.Z, key.Rotation.W, key.Scale.X, key.Scale.Y, key.Scale.Z }) writer.Write(v);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static void Validate(GModelMotionRecipe r)
    {
        if (!Enum.IsDefined(r.Kind) || string.IsNullOrWhiteSpace(r.Id)) throw new InvalidOperationException("Invalid motion recipe.");
        foreach (float v in new[] { r.Fps, r.Duration, r.Tempo, r.Stride, r.StepHeight, r.StanceWidth, r.BodySway, r.Intensity, r.CrouchDepth, r.JumpHeight })
            if (!float.IsFinite(v)) throw new InvalidOperationException("Motion controls must be finite.");
        if (r.Fps is < 1 or > 120 || r.Duration is < .25f or > 20 || r.Tempo is < .25f or > 4 || r.Stride is < 0 or > .8f
            || r.StepHeight is < 0 or > .4f || r.StanceWidth is < .7f or > 1.3f || r.BodySway is < 0 or > .1f
            || r.Intensity is < 0 or > 1 || r.CrouchDepth is < 0 or > .5f || r.JumpHeight is < 0 or > .6f)
            throw new InvalidOperationException("A motion control is outside its supported range.");
    }

    private sealed class Solver
    {
        private readonly GModelAsset _asset;
        private readonly GModelRigWizardSetup _setup;
        private readonly GModelMotionRecipe _r;
        private readonly Matrix4x4[] _rest;
        private readonly Dictionary<string, GModelRigLandmark> _roles;
        private readonly Vector3 _up, _forward, _right;
        private readonly float _legLength, _duration;

        public Solver(GModelAsset asset, GModelMotionRecipe recipe)
        {
            _asset = asset; _setup = asset.RigWizard ?? throw new InvalidOperationException("Fit or map a rig first."); _r = recipe;
            _roles = _setup.Joints.ToDictionary(j => j.Role);
            _rest = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, ModelPoseWorkflow.BindPose(asset));
            _up = Vector3.Normalize(_setup.Up); _right = Vector3.Normalize(Vector3.Cross(_up, _setup.Forward)); _forward = Vector3.Cross(_right, _up);
            _legLength = _setup.Chains.Where(c => c.Kind == GModelLimbKind.Leg).Select(Length).DefaultIfEmpty(1).Min();
            _duration = _r.Duration / _r.Tempo;
        }
        private Vector3[] Points(GModelRigChain c) => c.Roles.Select(r => _rest[_roles[r].BoneIndex].Translation).ToArray();
        private float Length(GModelRigChain c)
        {
            var p = Points(c); float sum = 0; for (int i = 1; i < p.Length; i++) sum += Vector3.Distance(p[i - 1], p[i]); return sum;
        }

        public ModelWizardMotionSample Sample(float time)
        {
            if(_r.Intensity==0)
            {
                var still=_setup.Chains.Where(c=>c.Kind==GModelLimbKind.Leg).Select(c=>
                {
                    var end=Points(c)[^1];return new ModelWizardContact(c.Name,true,end,end,Length(c));
                }).ToArray();
                return new(ModelPoseWorkflow.BindPose(_asset),Vector3.Zero,still);
            }
            float cycle = time / _duration;
            float wave = MathF.Sin(cycle * MathF.Tau);
            bool walking = _r.Kind is GModelMotionKind.Walk or GModelMotionKind.Run or GModelMotionKind.TurnLeft or GModelMotionKind.TurnRight;
            float duty = _r.Kind == GModelMotionKind.Run ? .42f : .65f;
            float stride = walking ? _r.Stride * _r.Intensity * _legLength : 0;
            float turn = _r.Kind == GModelMotionKind.TurnLeft ? -1 : _r.Kind == GModelMotionKind.TurnRight ? 1 : 0;
            // Squat a little to provide reach slack. Global travel is virtual and never enters root locals.
            float lower = walking ? (.025f + stride / Math.Max(_legLength, 1e-5f) * .18f) * _legLength : 0;
            float progress = Math.Clamp(cycle, 0, 1);
            if (_r.Kind == GModelMotionKind.Crouch) lower = _r.CrouchDepth * _r.Intensity * _legLength * MathF.Sin(progress * MathF.PI);
            if (_r.Kind == GModelMotionKind.Jump) lower = _r.CrouchDepth * _r.Intensity * _legLength * MathF.Pow(MathF.Sin(progress * MathF.Tau), 2) * .5f;
            float sway = _r.BodySway * _r.Intensity * _legLength * wave;
            var shift = -_up * lower + _right * sway;
            if (_r.Kind == GModelMotionKind.Idle) shift += _up * (_legLength * .004f * _r.Intensity * (MathF.Cos(cycle * MathF.Tau) - 1));
            var overrides = new Dictionary<int, Matrix4x4>();
            foreach (var role in new[] { "Pelvis", "Chest", "Neck", "Head" })
            {
                int index = _roles[role].BoneIndex;
                if (_asset.Rig.Bones[index].ParentIndex < 0) continue;
                var m = _rest[index]; m.Translation += shift; overrides[index] = m;
            }
            var contacts = new List<ModelWizardContact>();
            foreach (var chain in _setup.Chains)
            {
                var points = Points(chain); var posed = points.Select(p => p + shift).ToArray();
                // An imported root may itself be the hips; do not translate that hierarchy implicitly.
                int first = _roles[chain.Roles[0]].BoneIndex;
                int parent = _asset.Rig.Bones[first].ParentIndex;
                if (parent >= 0 && !overrides.ContainsKey(parent) && _asset.Rig.Bones[parent].ParentIndex < 0)
                    posed = (Vector3[])points.Clone();
                if (chain.Kind == GModelLimbKind.Leg)
                {
                    float phase = Fraction(cycle + Phase(chain, _setup.Body, _r.Kind));
                    bool planted = !walking || phase < duty;
                    float u = planted ? phase / duty : (phase - duty) / (1 - duty);
                    float z = walking ? (planted ? .5f - u : -.5f + u * u * (3 - 2 * u)) * stride : 0;
                    float lift = walking && !planted ? MathF.Pow(MathF.Sin(u * MathF.PI), 2) * _r.StepHeight * _r.Intensity * _legLength : 0;
                    var target = points[^1] + _forward * z + _up * lift;
                    float lateral = Vector3.Dot(points[^1] - _rest[_roles["Pelvis"].BoneIndex].Translation, _right);
                    target += _right * lateral * (_r.StanceWidth - 1);
                    if (turn != 0)
                    {
                        var center = _rest[_roles["Pelvis"].BoneIndex].Translation;
                        float angle = (planted ? .5f - u : -.5f + u * u * (3 - 2 * u)) * _r.Stride * _r.Intensity * turn;
                        target = center + Vector3.TransformNormal(points[^1] - center, Matrix4x4.CreateFromAxisAngle(_up, angle)) + _up * lift;
                    }
                    if (_r.Kind == GModelMotionKind.Jump && progress is > .25f and < .75f)
                    {
                        planted = false; target += _up * (_r.JumpHeight * _r.Intensity * _legLength * MathF.Sin((progress - .25f) * MathF.Tau));
                    }
                    posed = SolveChain(posed, target, chain.BendDirection);
                    contacts.Add(new(chain.Name, planted, posed[^1], target, Length(chain)));
                }
                else if (chain.Kind == GModelLimbKind.Arm)
                {
                    float length = Length(chain);
                    float actualSide = MathF.Sign(Vector3.Dot(points[^1] - points[0], _right));
                    float swing = wave * (walking ? .35f : .035f) * -chain.Side;
                    if (_r.Kind == GModelMotionKind.Crouch) swing += MathF.Sin(progress * MathF.PI) * .6f;
                    var relaxed = Vector3.Normalize(-_up + _right * actualSide * (.16f + chain.Pair * .08f) + _forward * swing);
                    var target = posed[0] + Vector3.Lerp(points[^1] - points[0], relaxed * (length * .96f), _r.Intensity);
                    posed = SolveChain(posed, target, chain.BendDirection);
                }
                else
                {
                    var axis=Safe(Vector3.Cross(Safe(points[^1]-points[0],-_forward),chain.BendDirection),_up);
                    for (int j = 1; j < posed.Length; j++)
                    {
                        float angle=MathF.Sin(cycle*MathF.Tau-j*.6f)*.12f*_r.Intensity;
                        posed[j]=posed[j-1]+Vector3.TransformNormal(points[j]-points[j-1],Matrix4x4.CreateFromAxisAngle(axis,angle));
                    }
                }
                for (int j = 0; j < points.Length; j++)
                {
                    int index = _roles[chain.Roles[j]].BoneIndex;
                    if (_asset.Rig.Bones[index].ParentIndex < 0) continue;
                    int a = _setup.IncomingSegmentBinding ? Math.Max(0, j - 1) : Math.Min(j, points.Length - 2);
                    int b = a + 1;
                    Quaternion rotation = FromTo(points[b] - points[a], posed[b] - posed[a]);
                    if (_setup.IncomingSegmentBinding && j == 0)
                    {
                        int connection = _asset.Rig.Bones[index].ParentIndex;
                        var parentWorld = overrides.TryGetValue(connection, out var movedParent) ? movedParent : _rest[connection];
                        rotation = FromTo(points[0] - _rest[connection].Translation, posed[0] - parentWorld.Translation);
                    }
                    var m = _rest[index]; m.Translation = Vector3.Zero;
                    m *= Matrix4x4.CreateFromQuaternion(rotation); m.Translation = posed[j]; overrides[index] = m;
                }
            }
            var worlds = new Matrix4x4[_rest.Length]; var locals = new Matrix4x4[_rest.Length]; var ready = new bool[_rest.Length];
            for (int i = 0; i < worlds.Length; i++) Resolve(i);
            void Resolve(int i)
            {
                if (ready[i]) return;
                int parent = _asset.Rig.Bones[i].ParentIndex;
                if (parent >= 0) Resolve(parent);
                locals[i] = _asset.Rig.Bones[i].BindLocal;
                worlds[i] = parent >= 0 ? locals[i] * worlds[parent] : locals[i];
                if (parent >= 0 && overrides.TryGetValue(i, out var desired))
                {
                    if (!Matrix4x4.Invert(worlds[parent], out var inverse)) throw new InvalidOperationException("Invalid rest scale.");
                    worlds[i] = desired; locals[i] = desired * inverse;
                }
                ready[i] = true;
            }
            var travel = _forward * (stride / duty * cycle);
            if (_r.Kind == GModelMotionKind.Jump)
                travel = _up * (_r.JumpHeight * _r.Intensity * _legLength * MathF.Pow(MathF.Sin(Math.Clamp((progress - .25f) * 2, 0, 1) * MathF.PI), 2));
            var virtualTransform = Matrix4x4.CreateTranslation(travel);
            if (turn != 0)
            {
                travel = Vector3.Zero; var center = _rest[_roles["Pelvis"].BoneIndex].Translation;
                virtualTransform = Matrix4x4.CreateTranslation(-center) * Matrix4x4.CreateFromAxisAngle(_up, _r.Stride * _r.Intensity * turn / duty * cycle) * Matrix4x4.CreateTranslation(center);
            }
            return new(locals, travel, contacts) { VirtualTransform = virtualTransform };
        }
    }

    public static float Phase(GModelRigChain chain, GModelBodyPlan body, GModelMotionKind kind)
    {
        int side = chain.Side < 0 ? 0 : 1;
        return body switch
        {
            GModelBodyPlan.Humanoid => side * .5f,
            GModelBodyPlan.Quadruped when kind != GModelMotionKind.Run => Fraction(chain.Pair * .25f + side * .5f),
            _ => ((chain.Pair + side) % 2) * .5f,
        };
    }

    /// <summary>FABRIK with a stable pole plane and exact segment projection on each forward pass.</summary>
    public static Vector3[] SolveChain(IReadOnlyList<Vector3> initial, Vector3 target, Vector3 bend)
    {
        var p = initial.ToArray(); var root = p[0]; var lengths = new float[p.Length - 1]; float total = 0;
        for (int i = 0; i < lengths.Length; i++) { lengths[i] = Vector3.Distance(p[i], p[i + 1]); total += lengths[i]; }
        var direction = Safe(target - root, Vector3.UnitY);
        var pole = Safe(bend - direction * Vector3.Dot(bend, direction), Vector3.Cross(direction, MathF.Abs(direction.X) < .8f ? Vector3.UnitX : Vector3.UnitZ));
        if (Vector3.Distance(root, target) >= total * .99999f)
        {
            for (int i = 1; i < p.Length; i++) p[i] = p[i - 1] + direction * lengths[i - 1];
            return p;
        }
        // Reinitialize on the chosen bend side, preventing straight rest poses flipping between solutions.
        for (int i = 1; i < p.Length - 1; i++) p[i] = Vector3.Lerp(root, target, i / (p.Length - 1f)) + pole * (total * .25f * MathF.Sin(i * MathF.PI / (p.Length - 1)));
        for (int iteration = 0; iteration < 96; iteration++)
        {
            p[^1] = target;
            for (int i = p.Length - 2; i >= 0; i--) p[i] = p[i + 1] + Safe(p[i] - p[i + 1], -direction) * lengths[i];
            p[0] = root;
            for (int i = 1; i < p.Length; i++) p[i] = p[i - 1] + Safe(p[i] - p[i - 1], direction) * lengths[i - 1];
            if (Vector3.DistanceSquared(p[^1], target) < total * total * 1e-10f) break;
        }
        return p;
    }
    private static Vector3 Safe(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Normalize(fallback);
    private static float Fraction(float value) => value - MathF.Floor(value);
    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Safe(a, Vector3.UnitY); b = Safe(b, a); float dot = Math.Clamp(Vector3.Dot(a, b), -1, 1);
        if (dot > .999999f) return Quaternion.Identity;
        if (dot < -.999999f) return Quaternion.CreateFromAxisAngle(Safe(Vector3.Cross(a, MathF.Abs(a.X) < .8f ? Vector3.UnitX : Vector3.UnitY), Vector3.UnitZ), MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(a, b), 1 + dot));
    }
}
