using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// Central stylized lighting presets. Apply to <see cref="SceneEnvironment"/> once per room
    /// or from an <c>EntityBehavior</c> to drive the whole world's look from a single place.
    /// </summary>
    public static class StylizedLighting
    {
        public static void Apply(SceneEnvironment env, Preset preset)
        {
            if (env == null) return;

            switch (preset)
            {
                case Preset.Default:
                    env.StylizedEnabled = false;
                    env.StylizedToonSteps = 1f;
                    env.StylizedDiffuseWrap = 0.5f;
                    env.StylizedSpecularStrength = 0.2f;
                    env.StylizedRimStrength = 0.18f;
                    env.StylizedSaturation = 1f;
                    break;

                case Preset.GhibliForest:
                    env.StylizedEnabled = true;
                    env.StylizedToonSteps = 4f;
                    env.StylizedDiffuseWrap = 0.62f;
                    env.StylizedSpecularStrength = 0.08f;
                    env.StylizedRimStrength = 0.32f;
                    env.StylizedSaturation = 1.18f;
                    env.SunColor = new System.Numerics.Vector4(1f, 0.94f, 0.78f, 1f);
                    env.SunIntensity = 1.25f;
                    env.SunAmbientStrength = 0.42f;
                    env.AmbientColor = new System.Numerics.Vector4(0.62f, 0.78f, 0.92f, 1f);
                    env.AmbientGroundColor = new System.Numerics.Vector4(0.38f, 0.52f, 0.36f, 1f);
                    break;

                case Preset.AnimeBright:
                    env.StylizedEnabled = true;
                    env.StylizedToonSteps = 3f;
                    env.StylizedDiffuseWrap = 0.55f;
                    env.StylizedSpecularStrength = 0.12f;
                    env.StylizedRimStrength = 0.28f;
                    env.StylizedSaturation = 1.25f;
                    break;
            }
        }

        public static void ApplyToMeshState(Mesh3DState state, Preset preset)
        {
            if (state.Equals(default(Mesh3DState))) return;
            switch (preset)
            {
                case Preset.GhibliForest:
                    state.StylizedLightingEnabled = true;
                    state.StylizedToonSteps = 4f;
                    state.StylizedDiffuseWrap = 0.62f;
                    state.StylizedSpecularStrength = 0.08f;
                    state.StylizedRimStrength = 0.32f;
                    state.StylizedSaturation = 1.18f;
                    break;
                case Preset.AnimeBright:
                    state.StylizedLightingEnabled = true;
                    state.StylizedToonSteps = 3f;
                    state.StylizedDiffuseWrap = 0.55f;
                    state.StylizedSpecularStrength = 0.12f;
                    state.StylizedRimStrength = 0.28f;
                    state.StylizedSaturation = 1.25f;
                    break;
                default:
                    state.StylizedLightingEnabled = false;
                    break;
            }
        }

        public enum Preset
        {
            Default,
            GhibliForest,
            AnimeBright,
        }
    }
}
