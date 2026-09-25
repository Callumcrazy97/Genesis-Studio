namespace Genesis.Application.Editors.Suite;

/// <summary>Editor-only ground reference. Gameplay never sees this plate.</summary>
public enum EditorFloorStyle
{
    None,
    Checkerboard,
    Plain,
    GridOnly,
}

public static class EditorFloorStyleExtensions
{
    public static bool DrawsPlate(this EditorFloorStyle style) =>
        style is EditorFloorStyle.Checkerboard or EditorFloorStyle.Plain;

    public static string StatusLabel(this EditorFloorStyle style) => style switch
    {
        EditorFloorStyle.Checkerboard => "checkerboard floor",
        EditorFloorStyle.Plain => "plain floor",
        EditorFloorStyle.GridOnly => "grid only",
        _ => "no floor",
    };
}
