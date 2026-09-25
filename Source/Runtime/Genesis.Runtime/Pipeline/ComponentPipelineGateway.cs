using System;

namespace Genesis.Runtime.Pipeline
{
    /// <summary>
    /// Ordered frame phases for the runtime capacity spine.
    /// Gameplay Draw submission is only legal in Submit.
    /// </summary>
    public enum ComponentPipelinePhase
    {
        Input = 0,
        PrePhysics = 1,
        Physics = 2,
        PostPhysics = 3,
        Gameplay = 4,
        Visibility = 5,
        Submit = 6,
        Present = 7,
    }

    /// <summary>
    /// Lightweight gateway that validates which phase is active.
    /// Extends into pack quotas / command IDs in later PGSL work.
    /// </summary>
    public sealed class ComponentPipelineGateway
    {
        public static ComponentPipelineGateway Current { get; } = new();

        public ComponentPipelinePhase Phase { get; private set; } = ComponentPipelinePhase.Input;
        public int FrameIndex { get; private set; }

        public void BeginFrame(int frameIndex)
        {
            FrameIndex = frameIndex;
            Phase = ComponentPipelinePhase.Input;
        }

        public void Enter(ComponentPipelinePhase phase)
        {
            Phase = phase;
            // Shared flag so Engine/PGSL Draw* (in Genesis.Shared) can gate without referencing Runtime.
            Genesis.Shared.Rendering.RenderAutoState.AllowDrawSubmit = phase == ComponentPipelinePhase.Submit;
        }

        public bool CanSubmitDraw => Phase == ComponentPipelinePhase.Submit;

        public void EnsureCanSubmitDraw()
        {
            if (!CanSubmitDraw)
                throw new InvalidOperationException(
                    $"Draw submission is only valid in {ComponentPipelinePhase.Submit}; current={Phase}.");
        }
    }
}
