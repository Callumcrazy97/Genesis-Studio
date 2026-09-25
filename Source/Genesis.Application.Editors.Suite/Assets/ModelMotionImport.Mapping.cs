using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public static partial class ModelMotionImport
{
    private static void ValidateRig(GModelRig rig, string missingMessage)
    {
        if (!rig.IsValid) throw new InvalidDataException(missingMessage);
        for (int i = 0; i < rig.Bones.Count; i++)
        {
            var visited = new HashSet<int>();
            for (int bone = i; bone >= 0; bone = rig.Bones[bone].ParentIndex)
                if (bone >= rig.Bones.Count || !visited.Add(bone)) throw new InvalidDataException("The model contains an invalid skeleton hierarchy.");
            if (!Matrix4x4.Invert(rig.Bones[i].BindLocal, out var inverse) || !Finite(inverse))
                throw new InvalidDataException("The model contains a joint with an invalid rest transform.");
        }
    }

    private static bool Finite(Matrix4x4 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static string NormalizeName(string name)
    {
        // Old Genesis's case-insensitive namespace matching, also accepting armature paths.
        name = name.Trim(); int separator = Math.Max(name.LastIndexOf(':'), name.LastIndexOf('|'));
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private sealed class MotionMapping
    {
        private readonly GModelRig _source, _target;
        private readonly Matrix4x4[] _sourceBind;
        private readonly int[] _sourceForTarget;
        public int[] TargetForSource { get; }

        public MotionMapping(GModelRig source, GModelRig target)
        {
            ValidateRig(source, "The source has no skeleton."); ValidateRig(target, "The current model has no skeleton.");
            _source = source; _target = target; _sourceBind = source.Bones.Select(b => b.BindLocal).ToArray();
            TargetForSource = Enumerable.Repeat(-1, source.Bones.Count).ToArray();
            _sourceForTarget = Enumerable.Repeat(-1, target.Bones.Count).ToArray();
            for (int i = 0; i < source.Bones.Count; i++)
            {
                string name = source.Bones[i].Name;
                var exact = Enumerable.Range(0, target.Bones.Count).Where(j => !string.IsNullOrWhiteSpace(name) && string.Equals(name, target.Bones[j].Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                var matches = exact.Length > 0 ? exact : Enumerable.Range(0, target.Bones.Count)
                    .Where(j => !string.IsNullOrWhiteSpace(name) && string.Equals(NormalizeName(name), NormalizeName(target.Bones[j].Name), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1 || (matches.Length == 1 && _sourceForTarget[matches[0]] >= 0))
                    throw new InvalidDataException($"Joint name '{name}' is ambiguous. Give the matching joints unique names before importing.");
                if (matches.Length == 1) { TargetForSource[i] = matches[0]; _sourceForTarget[matches[0]] = i; }
            }
        }

        public int RequireMatch(IEnumerable<Matrix4x4[]> frames)
        {
            var animated = new HashSet<int>();
            foreach (var frame in frames)
            {
                if (frame.Length != _source.Bones.Count || frame.Any(m => !Finite(m))) throw new InvalidDataException("Animation frame does not match its skeleton.");
                for (int i = 0; i < frame.Length; i++)
                {
                    // Baking TRS can introduce tiny roundoff in otherwise stationary helper nodes.
                    Matrix4x4 delta = frame[i] - _sourceBind[i];
                    if (new Vector4(delta.M11, delta.M12, delta.M13, delta.M14).LengthSquared() > 1e-10f
                        || new Vector4(delta.M21, delta.M22, delta.M23, delta.M24).LengthSquared() > 1e-10f
                        || new Vector4(delta.M31, delta.M32, delta.M33, delta.M34).LengthSquared() > 1e-10f
                        || new Vector4(delta.M41, delta.M42, delta.M43, delta.M44).LengthSquared() > 1e-10f) animated.Add(i);
                }
            }
            if (animated.Count == 0) animated.UnionWith(Enumerable.Range(0, _source.Bones.Count));
            int matched = animated.Count(i => TargetForSource[i] >= 0);
            if (matched == 0 || matched / (float)animated.Count < .8f)
                throw new InvalidDataException($"Skeletons are incompatible: {matched}/{animated.Count} animated joints match. Use a model with matching joint names, or import its rig first.");
            return animated.Count - matched;
        }

        public Matrix4x4[] Frame(Matrix4x4[] sourceLocals)
        {
            var sourceWorld = GModelPrimitiveFactory.ComputeWorldTransforms(_source.Bones, sourceLocals);
            var worlds = new Matrix4x4[_target.Bones.Count]; var ready = new bool[worlds.Length];
            Matrix4x4 World(int i)
            {
                if (ready[i]) return worlds[i];
                int parent = _target.Bones[i].ParentIndex, source = _sourceForTarget[i];
                if (source < 0)
                    worlds[i] = _target.Bones[i].BindLocal * (parent >= 0 ? World(parent) : Matrix4x4.Identity);
                else
                {
                    // Imported node defaults may be the first animated pose (Mixamo DAE), not
                    // the skin's rest pose. Subtracting that pose independently in world space
                    // changes bone lengths. Keep authored local motion, as in Old Genesis.
                    int anchor = parent;
                    while (anchor >= 0 && _sourceForTarget[anchor] < 0) anchor = _target.Bones[anchor].ParentIndex;
                    var inverse = Matrix4x4.Identity;
                    if (anchor >= 0 && !Matrix4x4.Invert(sourceWorld[_sourceForTarget[anchor]], out inverse))
                        throw new InvalidDataException("An animation frame contains zero joint scale.");
                    worlds[i] = sourceWorld[source] * inverse * (anchor >= 0 ? World(anchor) : Matrix4x4.Identity);
                }
                ready[i] = true; return worlds[i];
            }
            var locals = new Matrix4x4[worlds.Length];
            for (int i = 0; i < locals.Length; i++)
            {
                var world = World(i); int parent = _target.Bones[i].ParentIndex; var inverse = Matrix4x4.Identity;
                if (parent >= 0 && !Matrix4x4.Invert(World(parent), out inverse)) throw new InvalidDataException("An animation frame contains zero joint scale.");
                int source = _sourceForTarget[i];
                // Avoid roundoff for the common case of matching parent/child hierarchies.
                locals[i] = source >= 0 && (parent < 0 ? _source.Bones[source].ParentIndex < 0
                    : _sourceForTarget[parent] >= 0 && _source.Bones[source].ParentIndex == _sourceForTarget[parent])
                    ? sourceLocals[source] : world * inverse;
                if (!Finite(locals[i])) throw new InvalidDataException("An animation frame contains an invalid transform.");
            }
            return locals;
        }
    }
}
