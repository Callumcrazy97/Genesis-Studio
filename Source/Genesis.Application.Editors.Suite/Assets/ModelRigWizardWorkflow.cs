using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public static partial class ModelRigWizardWorkflow
{
    public static GModelRigWizardSetup Suggest(GModelAsset source)
    {
        if (source.RigWizard is not null)
        {
            var saved=ModelPoseWorkflow.Copy(source.RigWizard);
            saved.Bound &= source.Rig.IsValid && source.Meshes.Any(m=>m.IsSkinned) && source.Meshes.Where(m=>m.Vertices.Length+m.SkinnedVertices.Length>0).All(m=>m.IsSkinned);
            if(saved.Bound)saved.ReuseExistingRig=true;
            return saved;
        }
        var setup = new GModelRigWizardSetup
        {
            ReuseExistingRig = source.Rig.IsValid,
            Body = source.Bounds.Size.Y > Math.Max(source.Bounds.Size.X, source.Bounds.Size.Z) ? GModelBodyPlan.Humanoid : GModelBodyPlan.Quadruped,
            GroundHeight = source.Bounds.Min.Y,
            SymmetryOffset = source.Bounds.Center.X
        };
        if (source.Rig.IsValid)
        {
            var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(source.Rig.Bones, ModelPoseWorkflow.BindPose(source));
            int hips = source.Rig.Bones.FindIndex(b => Clean(b.Name).EndsWith("hips", StringComparison.Ordinal));
            int head = source.Rig.Bones.FindIndex(b => Clean(b.Name).EndsWith("head", StringComparison.Ordinal));
            int left = source.Rig.Bones.FindIndex(b => Clean(b.Name).EndsWith("leftarm", StringComparison.Ordinal));
            int right = source.Rig.Bones.FindIndex(b => Clean(b.Name).EndsWith("rightarm", StringComparison.Ordinal));
            if (hips >= 0 && head >= 0)
            {
                setup.Body = GModelBodyPlan.Humanoid;
                setup.Up = Dominant(worlds[head].Translation - worlds[hips].Translation);
                if (left >= 0 && right >= 0) setup.Forward = Dominant(Vector3.Cross(setup.Up, worlds[right].Translation - worlds[left].Translation));
                if (Math.Abs(Vector3.Dot(setup.Up, setup.Forward)) > .1f) setup.Forward = Math.Abs(setup.Up.Z) < .5f ? Vector3.UnitZ : Vector3.UnitY;
                var points = source.Meshes.SelectMany(ModelRigWizardSpace.Positions).ToArray();
                var across = Vector3.Cross(setup.Up, setup.Forward);
                if (points.Length > 0) { setup.GroundHeight = points.Min(p => Vector3.Dot(p, setup.Up)); setup.SymmetryOffset = (points.Min(p => Vector3.Dot(p, across)) + points.Max(p => Vector3.Dot(p, across))) * .5f; }
            }
        }
        return setup;
        static Vector3 Dominant(Vector3 vector)
        {
            var magnitude = Vector3.Abs(vector); int axis = magnitude.X > magnitude.Y ? (magnitude.X > magnitude.Z ? 0 : 2) : (magnitude.Y > magnitude.Z ? 1 : 2);
            var result = Vector3.Zero; result[axis] = vector[axis] >= 0 ? 1 : -1; return result;
        }
    }

    public static GModelAsset Detect(GModelAsset source, GModelRigWizardSetup options, CancellationToken token = default, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested(); ValidateOptions(options);
        var result = ModelPoseWorkflow.Copy(source); var setup = ModelPoseWorkflow.Copy(options); result.RigWizard = setup; setup.Bound = false;
        var previous = setup.Joints.ToDictionary(j => j.Role); var previousChains = setup.Chains.ToDictionary(c => c.Name); var space = new ModelRigWizardSpace(source, setup);
        BuildProfile(setup, space);
        foreach (var chain in setup.Chains) if (previousChains.TryGetValue(chain.Name, out var savedChain)) chain.BendDirection = savedChain.BendDirection;
        foreach (var joint in setup.Joints)
            if (previous.TryGetValue(joint.Role, out var hint) && hint.Pinned) { joint.Position = hint.Position; joint.Pinned = true; joint.Reviewed = hint.Reviewed; }
        if (setup.ReuseExistingRig && source.Rig.IsValid)
        {
            progress?.Report("Matching existing bones to body roles…"); MapExisting(result, space, previous, token); return result;
        }
        setup.ReuseExistingRig = false; setup.IncomingSegmentBinding = true;
        var geometry = new ModelRigWizardGeometry(source, setup, token, progress);
        progress?.Report("Fitting the skeleton to interior paths…");
        foreach (var joint in setup.Joints)
        {
            token.ThrowIfCancellationRequested(); if (joint.Role == "Root" || joint.Pinned) { joint.Confidence = 1; continue; }
            var fit = geometry.Fit(space.ToFit(joint.Position)); joint.Position = space.ToModel(fit.Point); joint.Confidence = fit.Confidence;
            if (fit.Confidence < .55f) joint.Issue = geometry.HasInterior ? "Placement is uncertain; adjust if needed." : "Open or thin geometry; check this joint's position.";
        }
        var lookup = setup.Joints.ToDictionary(j => j.Role);
        foreach (int side in new[] { -1, 1 }) foreach (var kind in new[] { GModelLimbKind.Leg, GModelLimbKind.Arm })
        {
            var chains = setup.Chains.Where(c => c.Kind == kind && c.Side == side).OrderBy(c => c.Pair).ToArray(); if (chains.Length == 0) continue;
            var candidates = geometry.Extremities(side, kind == GModelLimbKind.Arm);
            bool ambiguous = candidates.Count != chains.Length;
            candidates = (kind == GModelLimbKind.Arm ? candidates.Take(chains.Length).OrderByDescending(p => p.Y) : candidates.Take(chains.Length).OrderByDescending(p => p.Z)).ToList();
            for (int i = 0; i < chains.Length; i++)
            {
                var end = lookup[chains[i].Roles[^1]];
                if (!end.Pinned && i < candidates.Count)
                {
                    end.Position = space.ToModel(candidates[i]);
                    if (kind == GModelLimbKind.Arm)
                    {
                        var start = lookup[chains[i].Roles[0]];
                        if (!start.Pinned) { var p = space.ToFit(start.Position); p.Y = candidates[i].Y; start.Position = space.ToModel(geometry.Fit(p).Point); }
                    }
                }
                if (ambiguous && !end.Pinned) end.Issue = $"Detected {candidates.Count} distinct ends for {chains.Length} expected limbs on this side. Check missing or overlapping limbs.";
            }
        }
        foreach (var chain in setup.Chains)
        {
            token.ThrowIfCancellationRequested(); var nodes = chain.Roles.Select(r => lookup[r]).ToArray();
            if (chain.Kind == GModelLimbKind.Leg && setup.Body is GModelBodyPlan.Quadruped or GModelBodyPlan.Humanoid && !nodes[0].Pinned)
            {
                // Use the detected foot's sagittal column to avoid mistaking a long tail for a rear hip.
                var attachment = space.ToFit(nodes[0].Position); var end = space.ToFit(nodes[^1].Position); attachment.Z = end.Z; attachment.X = end.X * .85f + space.MirrorX * .15f;
                nodes[0].Position = space.ToModel(geometry.Fit(attachment).Point);
            }
            var path = geometry.InteriorPath(space.ToFit(nodes[0].Position), space.ToFit(nodes[^1].Position), chain.Side);
            if (path.Count < 2)
            {
                foreach (var joint in nodes.Where(j => !j.Pinned)) joint.Issue = "No continuous interior path; check this limb.";
                continue;
            }
            float[] lengths = new float[path.Count]; for (int i = 1; i < path.Count; i++) lengths[i] = lengths[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            for (int j = 1; j < nodes.Length - 1; j++) if (!nodes[j].Pinned)
            {
                float target = lengths[^1] * j / (nodes.Length - 1f); int at = Array.FindIndex(lengths, l => l >= target); at = Math.Max(1, at);
                float t = (target - lengths[at - 1]) / Math.Max(1e-6f, lengths[at] - lengths[at - 1]);
                nodes[j].Position = space.ToModel(Vector3.Lerp(path[at - 1], path[at], t));
            }
            // Large lateral detours usually mean overlapping limbs or an attachment chosen in the torso.
            float sideDistance = Math.Abs(space.ToFit(nodes[0].Position).X - space.MirrorX);
            if (chain.Side != 0 && nodes.Skip(1).Take(nodes.Length - 2).Any(j => Math.Abs(space.ToFit(j.Position).X - space.MirrorX) < sideDistance * .45f))
                foreach (var joint in nodes.Where(j => !j.Pinned)) joint.Issue = "The interior path approaches another limb; check this attachment and chain.";
        }
        // Fit pairs jointly. The review page can turn mirroring off for small mesh asymmetries.
        foreach (var joint in setup.Joints.Where(j => j.MirrorRole.Length > 0 && j.Role.EndsWith(".L", StringComparison.Ordinal)))
        {
            var other = lookup[joint.MirrorRole]; if (joint.Pinned || other.Pinned) continue;
            var average = (joint.Position + space.Mirror(other.Position)) * .5f;
            joint.Position = average; other.Position = space.Mirror(average);
        }
        foreach (var role in new[] { "Pelvis", "Chest", "Neck", "Head" })
        {
            var joint = lookup[role]; if (joint.Pinned) continue;
            joint.Position = (joint.Position + space.Mirror(joint.Position)) * .5f;
        }
        if (!lookup["Root"].Pinned) lookup["Root"].Position = lookup["Pelvis"].Position;
        BuildFreshRig(result); ValidateLandmarks(result, false); return result;
    }

    public static void EditLandmark(GModelAsset draft, string role, Vector3 position, bool mirror = false)
    {
        if (!float.IsFinite(position.LengthSquared())) throw new InvalidOperationException("Joint positions must be finite.");
        var setup = draft.RigWizard ?? throw new InvalidOperationException("Detect a rig first.");
        if (setup.ReuseExistingRig) throw new InvalidOperationException("Remap existing bones, or choose a fresh rig to change its rest shape.");
        var joint = setup.Joints.Single(j => j.Role == role); joint.Position = position; joint.Pinned = true; joint.Reviewed = true;
        if (mirror && joint.MirrorRole.Length > 0)
        {
            var other = setup.Joints.Single(j => j.Role == joint.MirrorRole); other.Position = new ModelRigWizardSpace(draft, setup).Mirror(position); other.Pinned = true; other.Reviewed = true;
        }
        setup.Bound = false; BuildFreshRig(draft);
    }

    public static GModelAsset Bind(GModelAsset source, CancellationToken token = default, IProgress<string>? progress = null)
    {
        token.ThrowIfCancellationRequested(); ValidateLandmarks(source, true); var result = ModelPoseWorkflow.Copy(source);
        var setup = result.RigWizard!; progress?.Report(setup.ReuseExistingRig ? "Validating the existing skin…" : "Binding the fitted mesh…");
        if (!setup.ReuseExistingRig)
        {
            BuildFreshRig(result); result.SkinBindingMode = GModelSkinBindingMode.Smooth4; GModelPrimitiveFactory.BindSkinToMesh(result, token);
        }
        else if (result.Meshes.Any(m => m.Vertices.Length > 0 && !m.IsSkinned))
            throw new InvalidOperationException("This rig has unbound meshes. Choose a fresh rig to bind them, or bind them in Rigging first.");
        var diagnostics = GModelPrimitiveFactory.ValidateSkin(result);
        if (!diagnostics.Passed) throw new InvalidOperationException("Skin validation failed: " + diagnostics.Summary);
        token.ThrowIfCancellationRequested(); setup.Bound = true; return result;
    }

    public static void SynchronizeLandmarks(GModelAsset asset, bool bound)
    {
        if (asset.RigWizard is not { } setup) return;
        var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, ModelPoseWorkflow.BindPose(asset));
        foreach (var joint in setup.Joints)
        {
            if (joint.BoneIndex < 0 || joint.BoneIndex >= worlds.Length) continue;
            var position = worlds[joint.BoneIndex].Translation;
            if (Vector3.DistanceSquared(joint.Position, position) > 1e-10f) joint.Pinned = joint.Reviewed = true;
            joint.Position = position;
        }
        setup.Bound = bound;
    }

    public static void ValidateLandmarks(GModelAsset asset, bool requireValidChains)
    {
        var setup = asset.RigWizard ?? throw new InvalidOperationException("Detect a rig first."); ValidateOptions(setup);
        if (setup.Joints.Count == 0 || setup.Chains.Count == 0) throw new InvalidOperationException("Detect and review the body first.");
        if (requireValidChains)
        {
            for (int i = 0; i < asset.Rig.Bones.Count; i++)
            {
                var visited = new HashSet<int>(); int at = i;
                while (at >= 0)
                {
                    if (at >= asset.Rig.Bones.Count || !visited.Add(at)) throw new InvalidOperationException("The rig hierarchy is invalid. Repair it before generating motion.");
                    at = asset.Rig.Bones[at].ParentIndex;
                }
            }
        }
        foreach (var joint in setup.Joints)
        {
            if (joint.BoneIndex < 0 || joint.BoneIndex >= asset.Rig.Bones.Count) throw new InvalidOperationException("Map a bone for " + joint.Role + ".");
            if (!float.IsFinite(joint.Position.LengthSquared())) throw new InvalidOperationException("Invalid position: " + joint.Role);
        }
        foreach (var chain in setup.Chains)
        {
            var joints = chain.Roles.Select(r => setup.Joints.Single(j => j.Role == r)).ToArray();
            for (int i = 1; i < joints.Length; i++)
            {
                if (Vector3.DistanceSquared(joints[i].Position, joints[i - 1].Position) < 1e-10f)
                {
                    if (requireValidChains) throw new InvalidOperationException("Separate coincident joints in " + chain.Name + ".");
                    joints[i].Issue = "Coincident joints; move this joint before binding.";
                }
                if (requireValidChains && setup.ReuseExistingRig)
                {
                    int at = asset.Rig.Bones[joints[i].BoneIndex].ParentIndex;
                    while (at >= 0 && at != joints[i - 1].BoneIndex) at = asset.Rig.Bones[at].ParentIndex;
                    if (at < 0) throw new InvalidOperationException("Map " + chain.Name + " along one parent-to-child limb chain.");
                }
            }
        }
    }
    private static void ValidateOptions(GModelRigWizardSetup setup)
    {
        if (setup.ArmPairs is < 1 or > 8 || setup.TailSegments is < 0 or > 8) throw new InvalidOperationException("Use 1–8 arm pairs and 0–8 tail segments.");
        if (!Enum.IsDefined(setup.Body) || !float.IsFinite(setup.GroundHeight) || !float.IsFinite(setup.SymmetryOffset)) throw new InvalidOperationException("Invalid body or alignment settings.");
        if (!float.IsFinite(setup.Up.LengthSquared()) || !float.IsFinite(setup.Forward.LengthSquared()) || setup.Up.LengthSquared() < .9f || setup.Forward.LengthSquared() < .9f
            || Math.Abs(Vector3.Dot(Vector3.Normalize(setup.Up), Vector3.Normalize(setup.Forward))) > .01f) throw new InvalidOperationException("Choose perpendicular up and forward directions.");
    }
    private static void BuildFreshRig(GModelAsset asset)
    {
        var setup = asset.RigWizard!; var bones = new List<GModelBone>(); var indices = new Dictionary<string, int>();
        foreach (var joint in setup.Joints)
        {
            joint.BoneIndex = bones.Count; int parent = joint.ParentRole.Length > 0 ? indices[joint.ParentRole] : -1;
            var position = joint.Position - (parent >= 0 ? setup.Joints[parent].Position : Vector3.Zero);
            bones.Add(new() { Name = joint.Role, ParentIndex = parent, BindLocal = Matrix4x4.CreateTranslation(position), JointRadius = joint.Role == "Root" ? 0 : asset.Bounds.Size.Length() * .012f, Deforms = joint.Role != "Root" }); indices[joint.Role] = joint.BoneIndex;
        }
        asset.Rig = new() { Bones = bones }; GModelPrimitiveFactory.RebuildInverseBindMatrices(asset.Rig);
        asset.Poses.Clear(); asset.PoseAnimations.Clear(); asset.Animations.Clear();
        foreach (var mesh in asset.Meshes)
        {
            if (mesh.Vertices.Length == 0) mesh.Vertices = mesh.SkinnedVertices.Select(v => new Genesis.Shared.Interfaces.MeshVertex
            { Position = v.Position, Normal = v.Normal, UV = v.UV, Color = v.Color }).ToArray();
            mesh.IsSkinned = false;
        }
    }
}
