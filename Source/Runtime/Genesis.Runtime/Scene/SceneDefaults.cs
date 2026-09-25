using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Scene
{
    public static class SceneDefaults
    {
        public static void PrepareDemo(RendererOptions options)
        {
            if (options == null) return;
            options.FaceCulling = MeshRasterDefaults.Culling != FaceCullingOverride.None;
            options.CullFrontFaces = MeshRasterDefaults.Culling == FaceCullingOverride.Front;
            options.FrontCounterClockwise =
                MeshRasterDefaults.Winding == FrontFaceWindingOverride.CounterClockwise;
            options.FrustumCulling = true;
            options.Wireframe      = false;
        }

        public static void PrepareDemo(ref Mesh3DState state)
        {
            state.FrustumCullingEnabled = true;
        }

        public static void ApplyVoxelWorld(Genesis.Runtime.RuntimeScene scene)
        {
            var env = scene.Environment;
            env.BackgroundColor    = new Vector4(0.48f, 0.72f, 0.95f, 1f);
            env.SunEnabled         = true;
            env.ShadowsEnabled     = true;
            env.ShowFloor          = false;
            env.FogEnabled         = false;
            env.FogStart           = 60f;
            env.FogEnd             = 260f;
            env.FogDensity         = 0.008f;
            env.FogHeightFalloff   = 0.18f;
            env.FogAerialBlend     = 0.45f;
            env.FogSunPreserve     = 0.4f;
            env.FogNoiseStrength   = 0f;
            env.FogScreenSpace     = true;
            env.VolumetricFogEnabled = false;
            env.VolumetricFogQuality = 1;
            env.VolumetricTemporalBlend = 0.8f;
            env.FogColor           = new Vector4(0.62f, 0.78f, 0.94f, 1f);

            scene.Camera3D.FarPlane  = 240f;
            scene.Camera3D.NearPlane = 0.15f;

            scene.Streaming.Settings.ResetToDefaults();
        }
    }
}
