namespace Genesis.Streaming
{
    /// <summary>Per-provider streaming counters for diagnostics.</summary>
    public sealed class StreamingStats
    {
        public int CellsLoaded { get; set; }
        public int CellsVisible { get; set; }
        public int CellsPending { get; set; }
        public int CellsDrawn { get; set; }
        /// <summary>Ready chunks scanned for frustum culling this frame (before hierarchical reject).</summary>
        public int CellsCullCandidates { get; set; }
        public int BackgroundJobs { get; set; }
        public int UnloadsLastFrame { get; set; }
        public int LoadsStartedLastFrame { get; set; }

        public void ResetFrameCounters()
        {
            UnloadsLastFrame = 0;
            LoadsStartedLastFrame = 0;
        }
    }
}
