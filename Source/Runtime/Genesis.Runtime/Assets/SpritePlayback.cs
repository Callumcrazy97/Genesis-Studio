using System;
using System.Collections.Generic;
using Genesis.Runtime.ECS.Components;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Assets;

/// <summary>Duration-based sprite frame advancement for greenfield <c>.image.json</c> assets.</summary>
public static class SpritePlayback
{
    public static IReadOnlyList<int> GetPlayableFrameIndices(SpriteRuntimeAsset asset, int animationTagIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Frames.Count == 0)
            return Array.Empty<int>();

        if (animationTagIndex < 0 || asset.Tags.Count == 0 || animationTagIndex >= asset.Tags.Count)
            return Range(0, asset.Frames.Count - 1);

        SpriteRuntimeAnimationTag tag = asset.Tags[animationTagIndex];
        int start = FindFrameIndex(asset, tag.StartFrameId);
        int end = FindFrameIndex(asset, tag.EndFrameId);
        if (start < 0 || end < 0)
            return Array.Empty<int>();

        if (start > end)
            (start, end) = (end, start);
        return Range(start, end);
    }

    // The playable frames are always a run start..end; one shared, read-only array per run.
    // Every animated sprite built a new list on every advance (Kinfield: a third of its allocation).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, int[]> Ranges = new();

    private static int[] Range(int start, int end) =>
        Ranges.GetOrAdd(((long)start << 32) | (uint)end, static key =>
        {
            int first = (int)(key >> 32);
            int last = (int)(uint)key;
            int[] run = new int[last - first + 1];
            for (int i = 0; i < run.Length; i++)
                run[i] = first + i;
            return run;
        });

    public static void Advance(
        ref SpriteComponent sprite,
        SpriteRuntimeAsset asset,
        float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(asset);
        IReadOnlyList<int> playable = GetPlayableFrameIndices(asset, sprite.AnimationTagIndex);
        if (playable.Count == 0)
            return;

        if (sprite.ImageSpeed <= 0f)
        {
            sprite.ImageIndex = playable[0];
            sprite.PlaybackElapsedMs = 0f;
            sprite.PlaybackStepDirection = 1;
            return;
        }

        int currentSlot = IndexOf(playable, sprite.ImageIndex);
        if (currentSlot < 0)
        {
            currentSlot = 0;
            sprite.ImageIndex = playable[0];
            sprite.PlaybackElapsedMs = 0f;
            sprite.PlaybackStepDirection = 1;
        }

        if (sprite.PlaybackStepDirection == 0)
            sprite.PlaybackStepDirection = 1;

        (string direction, bool loop) = ResolveClipSettings(
            asset,
            sprite.AnimationTagIndex,
            sprite.AnimationLoopOverride);
        int frameIndex = playable[currentSlot];
        int duration = Math.Max(1, asset.Frames[frameIndex].DurationMilliseconds);
        sprite.PlaybackElapsedMs += deltaSeconds * 1000f * sprite.ImageSpeed;
        while (sprite.PlaybackElapsedMs >= duration)
        {
            sprite.PlaybackElapsedMs -= duration;
            (currentSlot, sprite.PlaybackStepDirection, bool shouldStop) = StepSlot(
                currentSlot,
                playable,
                sprite.PlaybackStepDirection,
                direction,
                loop);
            if (shouldStop)
            {
                sprite.ImageIndex = playable[currentSlot];
                return;
            }

            frameIndex = playable[currentSlot];
            duration = Math.Max(1, asset.Frames[frameIndex].DurationMilliseconds);
        }

        sprite.ImageIndex = frameIndex;
    }

    public static SpriteRuntimeOrigin ResolveOrigin(SpriteRuntimeAsset asset, int frameIndex)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Frames.Count == 0)
            return asset.Origin;

        int index = SpriteAssetLoader.NormalizeFrameIndex(frameIndex, asset.Frames.Count);
        SpriteRuntimeFrame frame = asset.Frames[index];
        return frame.OriginOverride ?? asset.Origin;
    }

    private static (int Slot, int StepDirection, bool ShouldStop) StepSlot(
        int slot,
        IReadOnlyList<int> playable,
        int stepDirection,
        string direction,
        bool loop)
    {
        if (playable.Count <= 1)
            return (0, stepDirection, !loop);

        string normalized = direction?.Trim().ToLowerInvariant() ?? "forward";
        if (normalized == "reverse")
        {
            if (slot <= 0)
            {
                if (!loop)
                    return (0, stepDirection, true);
                return (playable.Count - 1, stepDirection, false);
            }

            return (slot - 1, stepDirection, false);
        }

        if (normalized == "pingpong")
        {
            int nextSlot = slot + stepDirection;
            if (nextSlot >= playable.Count)
            {
                if (!loop)
                    return (playable.Count - 1, -1, true);
                return (Math.Max(0, playable.Count - 2), -1, false);
            }

            if (nextSlot < 0)
            {
                if (!loop)
                    return (0, 1, true);
                return (Math.Min(playable.Count - 1, 1), 1, false);
            }

            return (nextSlot, stepDirection, false);
        }

        if (slot >= playable.Count - 1)
        {
            if (!loop)
                return (playable.Count - 1, stepDirection, true);
            return (0, stepDirection, false);
        }

        return (slot + 1, stepDirection, false);
    }

    private static (string Direction, bool Loop) ResolveClipSettings(
        SpriteRuntimeAsset asset,
        int animationTagIndex,
        int loopOverride)
    {
        bool? runtimeLoop = loopOverride switch { 0 => false, 1 => true, _ => null };
        if (animationTagIndex < 0 || animationTagIndex >= asset.Tags.Count)
            return ("forward", runtimeLoop ?? true);

        SpriteRuntimeAnimationTag tag = asset.Tags[animationTagIndex];
        return (tag.Direction, runtimeLoop ?? tag.Loop);
    }

    private static int FindFrameIndex(SpriteRuntimeAsset asset, string frameId)
    {
        if (string.IsNullOrWhiteSpace(frameId))
            return -1;

        for (int i = 0; i < asset.Frames.Count; i++)
        {
            if (string.Equals(asset.Frames[i].Id, frameId, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int IndexOf(IReadOnlyList<int> indices, int frameIndex)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            if (indices[i] == frameIndex)
                return i;
        }

        return -1;
    }
}
