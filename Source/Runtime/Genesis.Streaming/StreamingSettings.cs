namespace Genesis.Streaming
{
    /// <summary>
    /// Frame budgets and distances for streaming. Load/unload radii are derived from
    /// <c>scene.Camera3D.FarPlane</c> (render distance × chunk size in blocks).
    /// </summary>
    public sealed class StreamingSettings
    {
        /// <summary>Load cells closer than farPlane * this value.</summary>
        public float LoadDistanceScale { get; set; } = 0.90f;

        /// <summary>Unload cells farther than farPlane * this value.</summary>
        public float UnloadDistanceScale { get; set; } = 1.05f;

        /// <summary>Extra world units added to chunk bounds for frustum load/unload tests.</summary>
        public float FrustumPadding { get; set; }

        /// <summary>Multiplies camera FOV for streaming frustum only (1 = match render camera).</summary>
        public float StreamingFrustumFovScale { get; set; } = 1f;

        /// <summary>
        /// Only generate cells that fall inside the streaming camera frustum (plus the
        /// always-resident ring). Cells behind / out of view are not generated until they
        /// come into view — this is what keeps a large render distance affordable.
        /// </summary>
        public bool FrustumCullGeneration { get; set; } = true;

        /// <summary>
        /// Horizontal chunk ring around the focus that is always generated regardless of the
        /// frustum, so the area immediately around the player exists for physics/collision and
        /// there is no pop-in when turning on the spot.
        /// </summary>
        public int AlwaysResidentRing { get; set; } = 4;

        // NOTE: in-game these are overwritten by GameApp.ApplyVideoSettings(), which scales them with
        // render distance (e.g. rd=20 → gen/mesh 20, uploads 40). These defaults only apply before that
        // runs / in tooling that doesn't call it.
        public int MaxGenerationStartsPerFrame { get; set; } = 3;
        public int MaxMeshStartsPerFrame { get; set; } = 4;
        public int MaxGpuUploadsPerFrame { get; set; } = 8;
        public int MaxUnloadsPerFrame { get; set; } = 8;
        public int MaxBackgroundJobs { get; set; } = 4;
        public int MaxMainThreadCallbacksPerFrame { get; set; } = 24;

        /// <summary>How often providers rebuild their load queue (frames). Spreads expensive frustum scans.</summary>
        public int StreamingRefreshIntervalFrames { get; set; } = 8;

        /// <summary>When true, unload ready chunks outside the streaming frustum beyond <see cref="UnloadOutsideViewMinHorizRing"/>.</summary>
        public bool UnloadOutsideViewFrustum { get; set; }

        /// <summary>Horizontal chunk ring always kept loaded even when outside the view frustum.</summary>
        public int UnloadOutsideViewMinHorizRing { get; set; } = 8;

        public float LoadDistance(float farPlane) => farPlane * LoadDistanceScale;
        public float UnloadDistance(float farPlane) => farPlane * UnloadDistanceScale;

        public void ResetToDefaults()
        {
            LoadDistanceScale = 0.90f;
            UnloadDistanceScale = 1.05f;
            FrustumPadding = 0f;
            StreamingFrustumFovScale = 1f;
            FrustumCullGeneration = true;
            AlwaysResidentRing = 4;
            MaxGenerationStartsPerFrame = 3;
            MaxMeshStartsPerFrame = 4;
            MaxGpuUploadsPerFrame = 8;
            MaxUnloadsPerFrame = 8;
            MaxBackgroundJobs = 4;
            MaxMainThreadCallbacksPerFrame = 24;
            StreamingRefreshIntervalFrames = 8;
        }
    }
}
