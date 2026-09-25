using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite;

/// <summary>Inspection settings local to a viewport; they never modify the resource.</summary>
public sealed class Editor3DViewSettings
{
    public RenderDebugView DebugView { get; set; } = RenderDebugView.Shaded;
    public bool? Wireframe { get; set; }
    public bool? Lighting { get; set; }
    public bool? Shadows { get; set; }

    public Mesh3DState Apply(Mesh3DState state)
    {
        state.DebugView = DebugView;
        if (Wireframe is bool wireframe) state.Wireframe = wireframe;
        if (Lighting is bool lighting) state.LightingEnabled = lighting;
        if (!state.LightingEnabled) state.LightingWeight = 0f;
        if (Shadows is bool shadows) state.ShadowsEnabled = shadows;
        if (DebugView != RenderDebugView.Shaded)
        {
            state.FogEnabled = state.FogScreenSpace = state.VolumetricFogEnabled = false;
            state.ShowSunVisual = false;
        }
        return state;
    }
}
