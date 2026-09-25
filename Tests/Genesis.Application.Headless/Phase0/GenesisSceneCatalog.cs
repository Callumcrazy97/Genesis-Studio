namespace Genesis.Application.Headless.Phase0;

internal enum GenesisSceneKind
{
    Runtime2D,
    Runtime3D,
    Golden3D,
}

internal enum GenesisSceneDimension
{
    TwoDimensional,
    ThreeDimensional,
}

internal sealed record GenesisScene(
    string Id,
    string Name,
    GenesisSceneDimension Dimension,
    GenesisSceneKind Kind,
    bool IntegrationScene,
    string Description);

/// <summary>
/// Only scenes Genesis can genuinely render today. The catalogue expands as later phases port new
/// subsystems; P0 does not invent placeholder terrain/vegetation/weather scenes.
/// </summary>
internal static class GenesisSceneCatalog
{
    public static IReadOnlyList<GenesisScene> Scenes { get; } =
    [
        new(
            "runtime-2d",
            "Runtime 2D primitives",
            GenesisSceneDimension.TwoDimensional,
            GenesisSceneKind.Runtime2D,
            IntegrationScene: false,
            "Sprite rectangles and lines through the current 2D runtime renderer."),

        new(
            "runtime-3d",
            "Runtime 3D mesh",
            GenesisSceneDimension.ThreeDimensional,
            GenesisSceneKind.Runtime3D,
            IntegrationScene: false,
            "One lit mesh through the current backend-neutral 3D runtime path."),

        new(
            "renderer-golden",
            "Renderer golden scene",
            GenesisSceneDimension.ThreeDimensional,
            GenesisSceneKind.Golden3D,
            IntegrationScene: true,
            "Genesis' deterministic renderer-parity scene exercising the existing 3D feature paths."),
    ];
}
