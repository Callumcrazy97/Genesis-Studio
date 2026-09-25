using Genesis.Application.Core.Images;

namespace Genesis.Application.Editors.Image.Imaging;

/// <summary>Clip-scoped frame indices and playback stepping for the image viewer.</summary>
public static class ImageAnimationPlayback
{
    public static IReadOnlyList<int> GetPlayableFrameIndices(
        ImageDocument document,
        int clipComboIndex,
        int frameCount)
    {
        if (frameCount <= 0)
            return Array.Empty<int>();

        if (clipComboIndex <= 0 || clipComboIndex - 1 >= document.Tags.Count)
        {
            int[] all = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
                all[i] = i;
            return all;
        }

        ImageAnimationTag tag = document.Tags[clipComboIndex - 1];
        int start = FindFrameIndex(document, tag.StartFrameId);
        int end = FindFrameIndex(document, tag.EndFrameId);
        if (start < 0 || end < 0)
            return Array.Empty<int>();

        if (start > end)
            (start, end) = (end, start);

        int[] indices = new int[end - start + 1];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = start + i;
        return indices;
    }

    public static (ImagePlaybackDirection Direction, bool Loop) ResolveClipSettings(
        ImageDocument document,
        int clipComboIndex)
    {
        if (clipComboIndex <= 0 || clipComboIndex - 1 >= document.Tags.Count)
            return (ImagePlaybackDirection.Forward, true);

        ImageAnimationTag tag = document.Tags[clipComboIndex - 1];
        return (tag.Direction, tag.Loop);
    }

    public static (int FrameIndex, int StepDirection, bool ShouldStop) AdvancePlayback(
        int currentFrameIndex,
        IReadOnlyList<int> playable,
        int stepDirection,
        ImagePlaybackDirection direction,
        bool loop)
    {
        if (playable.Count == 0)
            return (0, stepDirection, true);
        if (playable.Count == 1)
            return (playable[0], stepDirection, !loop);

        int slot = IndexOf(playable, currentFrameIndex);
        if (slot < 0)
            return (playable[0], 1, false);

        if (direction == ImagePlaybackDirection.Reverse)
        {
            if (slot <= 0)
            {
                if (!loop)
                    return (playable[0], stepDirection, true);
                slot = playable.Count - 1;
            }
            else
            {
                slot--;
            }

            return (playable[slot], stepDirection, false);
        }

        if (direction == ImagePlaybackDirection.PingPong)
        {
            int nextSlot = slot + stepDirection;
            if (nextSlot >= playable.Count)
            {
                if (!loop)
                    return (playable[playable.Count - 1], -1, true);
                nextSlot = playable.Count - 2;
                stepDirection = -1;
            }
            else if (nextSlot < 0)
            {
                if (!loop)
                    return (playable[0], 1, true);
                nextSlot = 1;
                stepDirection = 1;
            }

            return (playable[nextSlot], stepDirection, false);
        }

        if (slot >= playable.Count - 1)
        {
            if (!loop)
                return (playable[playable.Count - 1], stepDirection, true);
            slot = 0;
        }
        else
        {
            slot++;
        }

        return (playable[slot], stepDirection, false);
    }

    public static int StepManual(
        int currentFrameIndex,
        IReadOnlyList<int> playable,
        int delta,
        ImagePlaybackDirection direction,
        bool loop)
    {
        if (playable.Count == 0)
            return 0;

        int slot = IndexOf(playable, currentFrameIndex);
        if (slot < 0)
            slot = 0;

        int step = direction == ImagePlaybackDirection.Reverse ? -delta : delta;
        slot += step;
        if (slot >= playable.Count)
            slot = loop ? 0 : playable.Count - 1;
        if (slot < 0)
            slot = loop ? playable.Count - 1 : 0;
        return playable[slot];
    }

    private static int FindFrameIndex(ImageDocument document, string frameId)
    {
        if (string.IsNullOrWhiteSpace(frameId))
            return -1;

        for (int i = 0; i < document.Frames.Count; i++)
        {
            if (string.Equals(document.Frames[i].Id, frameId, StringComparison.OrdinalIgnoreCase))
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
