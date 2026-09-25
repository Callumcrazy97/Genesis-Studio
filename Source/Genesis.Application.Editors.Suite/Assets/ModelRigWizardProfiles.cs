using System.Numerics;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public static partial class ModelRigWizardWorkflow
{
    private static void BuildProfile(GModelRigWizardSetup setup, ModelRigWizardSpace space)
    {
        setup.Joints = []; setup.Chains = [];
        bool humanoid = setup.Body == GModelBodyPlan.Humanoid, animal = setup.Body == GModelBodyPlan.Quadruped;
        Add("Root", "", new(space.MirrorX, space.Ground, space.Size.Z * .5f));
        Add("Pelvis", "Root", space.Fraction(.5f, humanoid ? .48f : .5f, humanoid ? .5f : .4f));
        Add("Chest", "Pelvis", space.Fraction(.5f, humanoid ? .72f : .54f, humanoid ? .5f : .65f));
        Add("Neck", "Chest", space.Fraction(.5f, humanoid ? .83f : .68f, humanoid ? .5f : .76f));
        Add("Head", "Neck", space.Fraction(.5f, .91f, humanoid ? .5f : .8f));
        int legPairs = setup.Body switch { GModelBodyPlan.Humanoid => 1, GModelBodyPlan.Quadruped => 2, GModelBodyPlan.Insect => 3, _ => 4 };
        for (int pair = 0; pair < legPairs; pair++) foreach (int side in new[] { -1, 1 })
        {
            string suffix = side < 0 ? ".L" : ".R", prefix = $"Leg{pair + 1}";
            float z = humanoid ? .5f : legPairs == 2 ? (pair == 0 ? .72f : .3f) : .76f - pair * .52f / (legPairs - 1);
            float attachX = .5f + side * (humanoid ? .1f : animal ? .18f : .14f), tipX = .5f + side * (humanoid ? .13f : animal ? .22f : .46f);
            float height = humanoid ? .46f : animal ? .4f : .48f;
            var start = space.Fraction(attachX, height, z); var end = space.Fraction(tipX, 0, z + (humanoid ? .04f : .02f)); end.Y = space.Ground;
            string parent = humanoid || pair >= legPairs / 2 ? "Pelvis" : "Chest";
            var chain = new GModelRigChain
            {
                Name = prefix + suffix,
                Kind = GModelLimbKind.Leg,
                Pair = pair,
                Side = side,
                BendDirection = humanoid ? space.Forward : animal ? (pair == 0 ? -space.Forward : space.Forward) : space.Right * side
            };
            string[] labels = ["Hip", "Knee", "Ankle", "Foot"];
            for (int i = 0; i < labels.Length; i++)
            {
                string role = prefix + "." + labels[i] + suffix; var p = Vector3.Lerp(start, end, i / 3f);
                if (i == 1) p.Z += space.Size.Z * .04f * (animal && pair == 0 ? -1 : 1);
                Add(role, parent, p, role[..^2] + (side < 0 ? ".R" : ".L")); chain.Roles.Add(role); parent = role;
            }
            setup.Chains.Add(chain);
        }
        if (humanoid) for (int pair = 0; pair < setup.ArmPairs; pair++) foreach (int side in new[] { -1, 1 })
        {
            string suffix = side < 0 ? ".L" : ".R", prefix = $"Arm{pair + 1}"; float y = .75f - pair * .045f;
            var chain = new GModelRigChain { Name = prefix + suffix, Kind = GModelLimbKind.Arm, Pair = pair, Side = side, BendDirection = -space.Forward }; string parent = "Chest";
            string[] labels = ["Shoulder", "Elbow", "Hand"];
            for (int i = 0; i < 3; i++) { string role = prefix + "." + labels[i] + suffix; Add(role, parent, space.Fraction(.5f + side * (.14f + i * .17f), y - i * .015f, .5f), role[..^2] + (side < 0 ? ".R" : ".L")); chain.Roles.Add(role); parent = role; }
            setup.Chains.Add(chain);
        }
        if (setup.TailSegments > 0)
        {
            var chain = new GModelRigChain { Name = "Tail", Kind = GModelLimbKind.Tail, BendDirection = space.Up }; string parent = "Pelvis";
            for (int i = 0; i <= setup.TailSegments; i++) { string role = "Tail." + i; Add(role, parent, space.Fraction(.5f, .5f + i * .03f, .28f * (1 - i / (float)setup.TailSegments))); chain.Roles.Add(role); parent = role; }
            setup.Chains.Add(chain);
        }
        void Add(string role, string parent, Vector3 p, string mirror = "") => setup.Joints.Add(new() { Role = role, ParentRole = parent, MirrorRole = mirror, Position = space.ToModel(p) });
    }

    private static void MapExisting(GModelAsset asset, ModelRigWizardSpace space, Dictionary<string, GModelRigLandmark> previous, CancellationToken token)
    {
        var setup = asset.RigWizard!; var worlds = GModelPrimitiveFactory.ComputeWorldTransforms(asset.Rig.Bones, asset.Rig.Bones.Select(b => b.BindLocal).ToArray()); var used = new HashSet<int>();
        setup.IncomingSegmentBinding = asset.Metadata.ContainsKey("genesis.skin.binding") || asset.RigWizard.IncomingSegmentBinding && asset.RigWizard.Bound;
        // Skin centroids are a geometric tie-breaker for rigs with generic joint names.
        var centers = new Vector3[worlds.Length]; var weights = new float[worlds.Length];
        foreach (var mesh in asset.Meshes) foreach (var v in mesh.SkinnedVertices)
        {
            for (int k = 0; k < 4; k++) { int b = (int)v.JointIndices[k]; float w = v.JointWeights[k]; if (b >= 0 && b < worlds.Length && w > 0) { centers[b] += v.Position * w; weights[b] += w; } }
        }
        for (int i = 0; i < centers.Length; i++) centers[i] = weights[i] > 0 ? centers[i] / weights[i] : worlds[i].Translation;
        foreach (var hint in setup.Joints)
        {
            token.ThrowIfCancellationRequested(); var aliases = Aliases(hint.Role); int chosen = -1; float best = float.MaxValue; bool named = false;
            if (previous.TryGetValue(hint.Role, out var saved) && saved.Pinned && saved.BoneIndex >= 0 && saved.BoneIndex < worlds.Length)
            { chosen = saved.BoneIndex; named = true; hint.Reviewed = saved.Reviewed; }
            else for (int i = 0; i < worlds.Length; i++)
            {
                if (used.Contains(i) && hint.Role != "Pelvis") continue;
                string name = Clean(asset.Rig.Bones[i].Name); int alias = Array.FindIndex(aliases, a => name.EndsWith(a, StringComparison.Ordinal)); bool match = alias >= 0 || name == Clean(hint.Role);
                if (hint.Role == "Root") match = asset.Rig.Bones[i].ParentIndex < 0;
                float score = Vector3.Distance(worlds[i].Translation, hint.Position) / space.Scale + .15f * Vector3.Distance(centers[i], hint.Position) / space.Scale - (match ? 2 - Math.Max(0, alias) * .2f : 0);
                if (score < best) { best = score; chosen = i; named = match; }
            }
            if (chosen < 0) { hint.Issue = "No matching bone. Select its bone in the mapping list."; continue; }
            hint.BoneIndex = chosen; hint.Position = worlds[chosen].Translation; hint.Confidence = named ? .95f : .35f;
            if (!named) hint.Issue = "Geometric match; confirm the mapped bone and limb.";
            used.Add(chosen);
        }
    }
    private static string Clean(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string[] Aliases(string role)
    {
        string side = role.EndsWith(".L", StringComparison.Ordinal) ? "left" : "right";
        if (role.StartsWith("Leg1.", StringComparison.Ordinal)) return role.Split('.')[1] switch
        { "Hip" => [side + "upleg", side + "thigh"], "Knee" => [side + "leg", side + "calf"], "Ankle" => [side + "foot"], _ => [side + "toebase", side + "toe"] };
        if (role.StartsWith("Arm1.", StringComparison.Ordinal)) return role.Split('.')[1] switch
        { "Shoulder" => [side + "arm", side + "upperarm"], "Elbow" => [side + "forearm", side + "lowerarm"], _ => [side + "hand"] };
        return role switch { "Pelvis" => ["hips", "pelvis"], "Chest" => ["spine2", "chest", "spine1"], "Neck" => ["neck"], "Head" => ["head"], _ => [Clean(role)] };
    }
}
