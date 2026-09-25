namespace Genesis.Application.Editors.Suite;

/// <summary>The Studio resource browser and editor palettes share one path-based drag contract.</summary>
public static class EditorResourceDragDrop
{
    public static string? ReadPath(IDataObject? data)
    {
        string? path = data?.GetData("Genesis.Application.ResourcePath") as string
            ?? data?.GetData(typeof(string)) as string;
        if (path is null && data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
            path = files[0];
        return path;
    }
}
