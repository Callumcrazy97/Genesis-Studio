using System;
using System.Numerics;

namespace Genesis.Shared.Audio
{
    /// <summary>Which ear hears a positioned sound, from where the listener is and which way is its right.</summary>
    public static class AudioPanning
    {
        /// <summary>A sound fully to one side is still heard this loudly by the other ear.</summary>
        public const float FarEarLevel = 0.15f;

        /// <summary>
        /// Left and right levels for a sound at <paramref name="source"/>. The nearer ear is always
        /// at full level and the further one falls towards <see cref="FarEarLevel"/>, so a sound
        /// straight ahead, behind, above or at the listener is as loud as it was without panning.
        /// </summary>
        public static void StereoLevels(Vector3 listener, Vector3 listenerRight, Vector3 source, out float left, out float right)
        {
            left = 1f;
            right = 1f;
            Vector3 offset = source - listener;
            float distance = offset.Length();
            if (!(distance > 1e-4f) || !(listenerRight.LengthSquared() > 1e-6f)) return;
            float side = Vector3.Dot(offset / distance, Vector3.Normalize(listenerRight));
            // Within arm's length a sound is in both ears: walking through an emitter must not
            // throw it from one ear to the other.
            side *= Math.Clamp((distance - 0.25f) / 1.25f, 0f, 1f);
            float far = 1f - (1f - FarEarLevel) * Math.Clamp(MathF.Abs(side), 0f, 1f);
            if (side > 0f) left = far;
            else right = far;
        }

        /// <summary>
        /// Fills the level matrix XAudio2 expects (destination by source) so that the first two
        /// output channels carry the left and right levels. False when the source is neither mono
        /// nor stereo or the output has fewer than two channels; the matrix is then left alone.
        /// </summary>
        public static bool FillMatrix(float[] matrix, int sourceChannels, int outputChannels, float left, float right)
        {
            if (matrix == null || sourceChannels is < 1 or > 2 || outputChannels < 2
                || matrix.Length < sourceChannels * outputChannels) return false;
            Array.Clear(matrix, 0, sourceChannels * outputChannels);
            if (sourceChannels == 1)
            {
                matrix[0] = left;
                matrix[1] = right;
            }
            else
            {
                matrix[0] = left;                     // left source into left output
                matrix[sourceChannels + 1] = right;   // right source into right output
            }

            return true;
        }
    }
}
