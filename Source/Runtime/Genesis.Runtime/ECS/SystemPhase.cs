namespace Genesis.Runtime.ECS
{
    public enum FixedPhase
    {
        PrePhysics,
        PostPhysics,
    }

    public static class SystemPhase
    {
        public const int PrePhysicsMin  = 90;
        public const int PrePhysicsMax  = 99;
        public const int PostPhysicsMin = 100;
        public const int PostPhysicsMax = 109;

        // Variable-update ordering for the render-preparation spine. These stay below the render
        // threshold so they execute before the deferred flush/render-system bucket.
        public const int Visibility = 270;
        public const int Lod = 280;
        public const int FrameBudget = 290;
        public const int RenderThreshold = 300;
    }
}
