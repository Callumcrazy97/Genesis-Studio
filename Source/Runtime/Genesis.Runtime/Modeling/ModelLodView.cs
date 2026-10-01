using System;
using System.Numerics;

namespace Genesis.Runtime.Modeling
{
    /// <summary>
    /// The camera models are being drawn for, so each can decide how much detail it is worth.
    /// </summary>
    /// <remarks>
    /// Detail follows the size a model appears on screen, measured as a fraction of the screen's
    /// height, so the same thresholds hold at any resolution and field of view. A scene sets the
    /// view before it collects its models; with no view set (editors, thumbnails, tests) every model
    /// is drawn in full.
    /// </remarks>
    public static class ModelLodView
    {
        private static Vector3 _camera;
        private static float _projectionScale;

        /// <summary>True while a scene is collecting models for a camera.</summary>
        public static bool Active { get; private set; }

        /// <summary>Master switch for automatic model detail and small-object culling.</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Multiplies every threshold: 2 keeps full detail to half the on-screen size.</summary>
        public static float Quality { get; set; } = 1f;

        /// <summary>Screen-height fractions below which levels 1, 2 and 3 are used.</summary>
        public static float Level1Size { get; set; } = 0.22f;
        public static float Level2Size { get; set; } = 0.09f;
        public static float Level3Size { get; set; } = 0.035f;

        /// <summary>Models smaller than this fraction of the screen's height are not drawn.</summary>
        public static float CullSize { get; set; } = 0.0025f;

        /// <summary>Models drawn at each level since the view began, and those too small to draw.</summary>
        public static int[] LevelCounts { get; } = new int[4];
        public static int CulledSmall { get; private set; }

        internal static void Count(int level)
        {
            if (level < 0) CulledSmall++;
            else LevelCounts[Math.Min(level, LevelCounts.Length - 1)]++;
        }

        public static void Begin(Vector3 cameraPosition, in Matrix4x4 projection)
        {
            Array.Clear(LevelCounts);
            CulledSmall = 0;
            _camera = cameraPosition;
            // M22 is cot(fov / 2): a sphere of radius r at distance d covers r * M22 / d of the height.
            _projectionScale = MathF.Abs(projection.M22);
            Active = Enabled && _projectionScale > 1e-6f && projection.M44 == 0f; // perspective only
        }

        public static void End() => Active = false;

        /// <summary>Fraction of the screen's height a sphere covers, top to bottom.</summary>
        public static float ScreenSize(Vector3 centre, float radius)
        {
            float distance = MathF.Max(Vector3.Distance(centre, _camera), MathF.Max(radius, 0.01f));
            return radius * _projectionScale / distance;
        }

        /// <summary>
        /// The level for a sphere: 0 full detail to 3 coarsest, or -1 when it is too small to draw.
        /// </summary>
        public static int LevelFor(Vector3 centre, float radius) =>
            Active ? LevelForSize(ScreenSize(centre, radius)) : 0;

        /// <summary>Multiply a radius over a distance by this for the share of the screen's height.</summary>
        public static float ProjectionScale => _projectionScale;

        /// <summary>The level for something covering this share of the screen's height.</summary>
        public static int LevelForSize(float screenSize)
        {
            float size = screenSize * MathF.Max(0.05f, Quality);
            if (size < CullSize) return -1;
            return size < Level3Size ? 3 : size < Level2Size ? 2 : size < Level1Size ? 1 : 0;
        }
    }
}
