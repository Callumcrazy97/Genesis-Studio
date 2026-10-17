using System;
using System.Threading;

namespace Genesis.Shared.Interfaces
{
    /// <summary>
    /// How the sun's shafts in the volumetric fog look. The defaults draw them exactly as before
    /// (a gentle, nearly even glow and hard-edged shadows); a game changes them through
    /// <c>Engine.Rendering.FogShaft*</c>. Read by the froxel fog pass every frame.
    /// </summary>
    public static class FogShaftSettings
    {
        /// <summary>The Henyey-Greenstein anisotropy the fog has always used.</summary>
        public const float DefaultAnisotropy = 0.25f;

        /// <summary>How bright fog in shadow is, against fog in sunlight (1).</summary>
        public const float DefaultShadowedFloor = 0.35f;

        private static float _anisotropy = DefaultAnisotropy;
        private static float _shadowedFloor = DefaultShadowedFloor;
        private static float _softness;

        /// <summary>
        /// 0 to 0.95: how strongly the haze scatters light forward. Above the default, shafts glow
        /// towards the sun and fade when seen side-on or with the sun behind, as in dusty air (about
        /// 0.6 to 0.8 for an interior with sunbeams).
        /// </summary>
        public static float Anisotropy
        {
            get => Volatile.Read(ref _anisotropy);
            set => Volatile.Write(ref _anisotropy, float.IsFinite(value) ? Math.Clamp(value, 0f, 0.95f) : DefaultAnisotropy);
        }

        /// <summary>0 to 1: the brightness of fog in shadow relative to sunlit fog. Higher is subtler shafts.</summary>
        public static float ShadowedFloor
        {
            get => Volatile.Read(ref _shadowedFloor);
            set => Volatile.Write(ref _shadowedFloor, float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : DefaultShadowedFloor);
        }

        /// <summary>0 to 4 metres: how wide the soft edge of a shaft is. 0 keeps the single shadow sample.</summary>
        public static float Softness
        {
            get => Volatile.Read(ref _softness);
            set => Volatile.Write(ref _softness, float.IsFinite(value) ? Math.Clamp(value, 0f, 4f) : 0f);
        }

        /// <summary>True when the anisotropy asks for the forward-scattering lobe (fainter away from the sun).</summary>
        public static bool ForwardLobe => Anisotropy > DefaultAnisotropy + 0.001f;

        public static void Reset()
        {
            Anisotropy = DefaultAnisotropy;
            ShadowedFloor = DefaultShadowedFloor;
            Softness = 0f;
        }
    }
}
