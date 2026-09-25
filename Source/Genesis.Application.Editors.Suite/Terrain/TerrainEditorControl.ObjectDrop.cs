namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    private void EnableObjectResourceDrop()
    {
        _viewport.Host.AllowDrop = true;
        string? Read(IDataObject? data)
        {
            string? path = EditorResourceDragDrop.ReadPath(data);
            return path?.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase) == true && File.Exists(path) ? path : null;
        }
        void Preview(DragEventArgs e)
        {
            string? path = Read(e.Data); e.Effect = path is null ? DragDropEffects.None : DragDropEffects.Copy;
            if (path is null) return;
            _placementEntityPath = path;
            _cursorValid = PickTerrain(_viewport.Host.PointToClient(new Point(e.X, e.Y)), out _cursorWorld);
            _viewport.Invalidate();
        }
        _viewport.Host.DragEnter += (_, e) => Preview(e);
        _viewport.Host.DragOver += (_, e) => Preview(e);
        _viewport.Host.DragLeave += (_, _) => { _placementEntityPath = null; _viewport.Invalidate(); };
        _viewport.Host.DragDrop += (_, e) =>
        {
            if (Read(e.Data) is not { } path) return;
            DropObjectResourceAt(path, _viewport.Host.PointToClient(new Point(e.X, e.Y)));
            _placementEntityPath = null; RefreshActiveToolCard();
        };
    }

    public bool DropObjectResourceAt(string path, Point client)
    {
        if (!path.EndsWith(".object.json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || !PickTerrain(client, out var point)) return false;
        BindAndPlaceSavedEntity(path, place: false);
        PlaceTerrainEntity(path, point.X, point.Z); return true;
    }
}
