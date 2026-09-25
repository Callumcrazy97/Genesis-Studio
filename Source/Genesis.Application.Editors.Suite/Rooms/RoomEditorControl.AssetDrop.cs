using System.Drawing;
using System.Windows.Forms;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private void ApplyPlacementDefaults(Genesis.Runtime.Scene.RoomTransform transform)
    {
        transform.ScaleX = _placement.PlacementScale.X;
        transform.ScaleY = _placement.PlacementScale.Y;
        transform.ScaleZ = ViewMode3D ? _placement.PlacementScaleZ : 1f;
        if (ViewMode3D) transform.RotationY = _placement.PlacementRotation
            + (_pendingPlacementKind == Genesis.Runtime.Scene.RoomNodeKind.GameObject ? NextPlacementYawOffset : 0);
        else transform.RotationZ = _placement.PlacementRotation;
    }

    private void EnableRoomAssetDrop()
    {
        _viewport.Host.AllowDrop = true;
        _viewport.Host.DragEnter += (_, e) => UpdateRoomAssetDrop(e);
        _viewport.Host.DragOver += (_, e) => UpdateRoomAssetDrop(e);
        _viewport.Host.DragDrop += (_, e) =>
        {
            if (RoomObjectDropPath(e.Data) is string path)
                DropObjectAt(path, _viewport.Host.PointToClient(new Point(e.X, e.Y)));
        };
    }

    private string? RoomObjectDropPath(IDataObject? data)
    {
        string? path = EditorResourceDragDrop.ReadPath(data);
        return _paletteEntries.FirstOrDefault(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase))?.FullPath;
    }

    private void UpdateRoomAssetDrop(DragEventArgs args)
    {
        string? path = RoomObjectDropPath(args.Data);
        bool allowed = path is not null && CanPlaceInActiveContext(Genesis.Runtime.Scene.RoomNodeKind.GameObject);
        args.Effect = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        if (!allowed) return;
        if (_pendingPlacementPath != path) BeginPlacement(path!);
        EditorPointerMove(_viewport.Host.PointToClient(new Point(args.X, args.Y)), MouseButtons.None, Keys.None);
    }

    /// <summary>Palette drop follows the same terrain contact, snapping and undo path as a click.</summary>
    public bool DropObjectAt(string path, Point viewportClient)
    {
        if (!CanPlaceInActiveContext(Genesis.Runtime.Scene.RoomNodeKind.GameObject)) return false;
        if (!_paletteEntries.Any(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase))) return false;
        int before = _room.Nodes.Count;
        BeginPlacement(path);
        EditorPointerMove(viewportClient, MouseButtons.None, Keys.None);
        EditorPointerDown(viewportClient, MouseButtons.Left, Keys.None);
        EditorPointerUp(viewportClient, MouseButtons.Left, Keys.None);
        return _room.Nodes.Count > before;
    }
}
