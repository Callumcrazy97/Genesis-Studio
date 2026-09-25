using System.Numerics;
using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Rigging;

public static class ImageRigPlayback
{
    public static Dictionary<string, SpriteBonePose> SampleBonePoses(
        ImageDocument document,
        int frameIndex,
        IReadOnlyList<int> frameDurationsMs)
    {
        Dictionary<string, SpriteBonePose> poses = new(StringComparer.OrdinalIgnoreCase);
        if (document.Armature == null) return poses;

        int timeMs = 0;
        for (int i = 0; i < frameIndex && i < frameDurationsMs.Count; i++)
            timeMs += Math.Max(1, frameDurationsMs[i]);

        foreach (ImageBone bone in document.Armature.Bones)
            poses[bone.Id] = SpriteBonePose.Identity;

        foreach (ImageAnimationTrack track in document.Tracks)
        {
            if (track.TargetKind != ImageTrackTargetKind.Bone || track.Keyframes.Count == 0)
                continue;

            ImageTrackKeyframe? before = null;
            ImageTrackKeyframe? after = null;
            foreach (ImageTrackKeyframe keyframe in track.Keyframes.OrderBy(key => key.TimeMilliseconds))
            {
                if (keyframe.TimeMilliseconds <= timeMs) before = keyframe;
                if (keyframe.TimeMilliseconds >= timeMs)
                {
                    after = keyframe;
                    break;
                }
            }

            if (!poses.ContainsKey(track.TargetId)) continue;
            SpriteBonePose current = poses[track.TargetId];
            if (before == null && after == null) continue;
            if (after == null || before == after || before == null)
            {
                ImageTrackKeyframe sample = after ?? before!;
                poses[track.TargetId] = ApplyTrackSample(current, track.Property, sample);
                continue;
            }

            float span = Math.Max(1, after.TimeMilliseconds - before.TimeMilliseconds);
            float t = Math.Clamp((timeMs - before.TimeMilliseconds) / span, 0f, 1f);
            poses[track.TargetId] = InterpolateTrack(current, track.Property, before, after, t);
        }

        return poses;
    }

    public static void UpsertBoneKeyframe(
        ImageDocument document,
        string boneId,
        ImageTrackProperty property,
        int timeMilliseconds,
        ImageTrackKeyframe value)
    {
        ImageAnimationTrack? track = document.Tracks.FirstOrDefault(candidate =>
            candidate.TargetKind == ImageTrackTargetKind.Bone
            && string.Equals(candidate.TargetId, boneId, StringComparison.OrdinalIgnoreCase)
            && candidate.Property == property);
        if (track == null)
        {
            track = new ImageAnimationTrack
            {
                Name = $"{property} · {boneId[..Math.Min(8, boneId.Length)]}",
                TargetKind = ImageTrackTargetKind.Bone,
                TargetId = boneId,
                Property = property,
            };
            document.Tracks.Add(track);
        }

        ImageTrackKeyframe? existing = track.Keyframes.FirstOrDefault(key => key.TimeMilliseconds == timeMilliseconds);
        if (existing == null)
        {
            value.TimeMilliseconds = timeMilliseconds;
            track.Keyframes.Add(value);
        }
        else
        {
            existing.Scalar = value.Scalar;
            existing.Vector = value.Vector;
            existing.Interpolation = value.Interpolation;
        }
        track.Keyframes.Sort((a, b) => a.TimeMilliseconds.CompareTo(b.TimeMilliseconds));
    }

    public static int FrameStartTime(IReadOnlyList<int> frameDurationsMs, int frameIndex)
    {
        int time = 0;
        for (int i = 0; i < frameIndex && i < frameDurationsMs.Count; i++)
            time += Math.Max(1, frameDurationsMs[i]);
        return time;
    }

    private static SpriteBonePose ApplyTrackSample(
        SpriteBonePose current,
        ImageTrackProperty property,
        ImageTrackKeyframe sample) =>
        property switch
        {
            ImageTrackProperty.Rotation => current with { Rotation = (float)sample.Scalar },
            ImageTrackProperty.Position => current with
            {
                Position = new Vector2((float)sample.Vector.X, (float)sample.Vector.Y),
            },
            ImageTrackProperty.Scale => current with
            {
                Scale = new Vector2(
                    (float)(sample.Vector.X <= 0 ? 1 : sample.Vector.X),
                    (float)(sample.Vector.Y <= 0 ? 1 : sample.Vector.Y)),
            },
            _ => current,
        };

    private static SpriteBonePose InterpolateTrack(
        SpriteBonePose current,
        ImageTrackProperty property,
        ImageTrackKeyframe before,
        ImageTrackKeyframe after,
        float t) =>
        property switch
        {
            ImageTrackProperty.Rotation => current with
            {
                Rotation = Lerp((float)before.Scalar, (float)after.Scalar, t),
            },
            ImageTrackProperty.Position => current with
            {
                Position = Vector2.Lerp(
                    new Vector2((float)before.Vector.X, (float)before.Vector.Y),
                    new Vector2((float)after.Vector.X, (float)after.Vector.Y),
                    t),
            },
            ImageTrackProperty.Scale => current with
            {
                Scale = Vector2.Lerp(
                    new Vector2((float)before.Vector.X, (float)before.Vector.Y),
                    new Vector2((float)after.Vector.X, (float)after.Vector.Y),
                    t),
            },
            _ => current,
        };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
