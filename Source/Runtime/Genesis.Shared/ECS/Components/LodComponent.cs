using System;

namespace Genesis.Shared.ECS.Components
{
    /// <summary>How <see cref="LodComponent"/> chooses its local, pre-budget LOD level.</summary>
    public enum LodSelectionPolicy : byte
    {
        /// <summary>Resolution/FOV-aware projected-size selection. This is the preferred default.</summary>
        ScreenError = 0,
        /// <summary>Authored world-distance thresholds.</summary>
        Distance = 1,
        /// <summary>Keep the authored <see cref="LodComponent.Selected"/> level.</summary>
        Fixed = 2,
        /// <summary>Never reduce detail; always select LOD0.</summary>
        Never = 3,
    }

    /// <summary>
    /// Backend-neutral LOD state and cost metadata. <c>LodSystem</c> writes <see cref="Selected"/>;
    /// the later frame-budget phase writes <see cref="Bias"/> without feeding that bias back into
    /// the local hysteresis decision.
    /// </summary>
    public struct LodComponent : IComponent
    {
        public LodSelectionPolicy Policy;

        /// <summary>
        /// Target projected-error size in pixels for screen-aware selection. Values &lt;= 0 use the
        /// engine default. The selector derives four bands from this target and the bounds radius.
        /// </summary>
        public float ScreenErrorPixels;

        /// <summary>Distance thresholds between LOD0/1, LOD1/2 and LOD2/3.</summary>
        public float Distance0;
        public float Distance1;
        public float Distance2;

        /// <summary>Triangle cost for each level, filled when the drawable asset is loaded.</summary>
        public int Cost0;
        public int Cost1;
        public int Cost2;
        public int Cost3;

        /// <summary>Local LOD chosen by LodSystem, before any frame-budget bias is applied.</summary>
        public byte Selected;

        /// <summary>Additional coarsening written by the future FrameBudgetSystem.</summary>
        public byte Bias;

        /// <summary>Hysteresis percentage (0 = engine default).</summary>
        public byte Hysteresis;

        /// <summary>Runtime state: zero until LodSystem has made the first local selection.</summary>
        public byte SelectionValid;

        /// <summary>Selected level after applying the budget bias, clamped to LOD3.</summary>
        public readonly byte EffectiveSelected => (byte)Math.Min(3, Selected + Bias);

        public readonly int CostForLevel(int level) => Math.Clamp(level, 0, 3) switch
        {
            0 => Math.Max(0, Cost0),
            1 => Math.Max(0, Cost1),
            2 => Math.Max(0, Cost2),
            _ => Math.Max(0, Cost3),
        };

        public readonly int EffectiveCost => CostForLevel(EffectiveSelected);

        public static LodComponent Default => new LodComponent
        {
            Policy = LodSelectionPolicy.ScreenError,
            ScreenErrorPixels = 2f,
            Hysteresis = 14,
            Selected = 0,
            Bias = 0,
            SelectionValid = 0,
        };
    }
}
