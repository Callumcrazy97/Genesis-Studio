using System.Globalization;
using Genesis.Runtime.Scene;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>A command in the contextual Inspector, rendered as a button rather than a stored value.</summary>
internal sealed record RoomInspectorAction(string Text);

public sealed partial class RoomEditorControl
{
    private const string ContextPlacementPrefix = "Context.Placement.";

    public bool RandomYaw => _room.Settings.RandomYaw;
    public float RandomYawDegrees => _room.Settings.RandomYawDegrees;
    public int PlacementRandomSeed => _room.Settings.PlacementRandomSeed;
    public float NextPlacementYawOffset => RandomYaw
        ? PlacementYawSample(PlacementRandomSeed, _room.Settings.PlacementRandomSequence) * RandomYawDegrees : 0f;

    // Integer mixing is stable across processes and save/reopen; camera movement never consumes a sample.
    private static float PlacementYawSample(int seed, int sequence)
    {
        uint value = unchecked((uint)seed + (uint)sequence * 0x9E3779B9u);
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        value ^= value >> 16;
        return (value >> 8) / 16777215f * 2f - 1f;
    }

    public void SetRandomYaw(bool enabled)
    {
        bool before = RandomYaw;
        if (before == enabled) return;
        void Apply(bool value) { _room.Settings.RandomYaw = value; RefreshSurfacePlacementSettings(); }
        Apply(enabled);
        PushEdit("Random placement yaw", () => Apply(enabled), () => Apply(before));
    }

    public void SetRandomYawDegrees(float degrees)
    {
        if (!float.IsFinite(degrees)) return;
        float before = RandomYawDegrees, after = Math.Clamp(degrees, 0, 180);
        if (before == after) return;
        void Apply(float value) { _room.Settings.RandomYawDegrees = value; RefreshSurfacePlacementSettings(); }
        Apply(after);
        PushEdit("Placement yaw range", () => Apply(after), () => Apply(before));
    }

    public void SetPlacementRandomSeed(int seed)
    {
        int before = PlacementRandomSeed, sequenceBefore = _room.Settings.PlacementRandomSequence;
        if (before == seed) return;
        void Apply(int value, int sequence)
        {
            _room.Settings.PlacementRandomSeed = value;
            _room.Settings.PlacementRandomSequence = sequence;
            RefreshSurfacePlacementSettings();
        }
        Apply(seed, 0);
        PushEdit("Placement random seed", () => Apply(seed, 0), () => Apply(before, sequenceBefore));
    }

    /// <summary>Seats the selection without changing rotation, regardless of future-placement preferences.</summary>
    public bool SnapSelectionToTerrainWithoutRotation() => ApplySelectedTerrainContact(false);

    /// <summary>Seats the selection and aligns its local up to the terrain, retaining its horizontal heading.</summary>
    public bool AlignSelectionToTerrain() => ApplySelectedTerrainContact(true);

    private bool ApplySelectedTerrainContact(bool align)
    {
        if (!ViewMode3D) return false;
        Dictionary<string, RoomTransform> before = SnapshotSelectionTransforms();
        foreach (RoomNode node in SelectionTransformRoots())
        {
            if (node.Kind != RoomNodeKind.GameObject) continue;
            RoomTransform world = GetNodeWorldTransform(node);
            if (TryGetTerrainSurface(world.X, world.Z, out RoomSurfaceHit surface))
                ApplySurfaceContact(node, surface, align);
        }
        Dictionary<string, RoomTransform> after = SnapshotSelectionTransforms();
        if (TransformSnapshotsEqual(before, after)) return false;
        PushTransformSnapshotEdit(align ? "Align selection to terrain" : "Snap selection to terrain", before, after);
        RefreshPhase4Ui();
        return true;
    }

    private void AddContextSurfaceValues(List<ResourceInspectorLiveValue> values, RoomNode node, bool locked)
    {
        if (_room.Dimension != RoomDimension.ThreeD || node.Kind != RoomNodeKind.GameObject) return;
        values.Add(new("Surface alignment", ContextPlacementPrefix + "SnapSelected", "Snap selected to terrain",
            new RoomInspectorAction("Snap selected to terrain"), ReadOnly: locked,
            Description: "Seat selected objects on visible terrain without changing their rotation. Undo restores the previous transforms."));
        values.Add(new("Surface alignment", ContextPlacementPrefix + "AlignSelected", "Align selected to terrain",
            new RoomInspectorAction("Align selected to terrain"), ReadOnly: locked,
            Description: "Seat selected objects and align their up axis to the terrain normal, retaining their heading."));
        AddContextPlacementPreferences(values, includeSurface: true);
    }

    private void AddContextPlacementPreferences(List<ResourceInspectorLiveValue> values, bool includeSurface)
    {
        const string group = "Future placement";
        if (includeSurface)
        {
            values.Add(new(group, ContextPlacementPrefix + "SnapToTerrain", "Snap to terrain", SnapToTerrain,
                Description: "Room-wide preference for new placements and subsequent surface dragging; does not move the current selection."));
            values.Add(new(group, ContextPlacementPrefix + "AlignToTerrainNormal", "Align to normal", AlignToTerrainNormal,
                Description: "Room-wide preference for new placements and subsequent surface dragging; use Align selected for an immediate change."));
        }
        values.Add(new(group, ContextPlacementPrefix + "RandomYaw", "Random yaw", RandomYaw,
            Description: "Vary each newly placed object's yaw around the placement angle. Existing objects are unchanged."));
        values.Add(new(group, ContextPlacementPrefix + "RandomYawDegrees", "Yaw range ± (°)", RandomYawDegrees,
            ReadOnly: !RandomYaw, Minimum: 0, Maximum: 180, Increment: 1, DecimalPlaces: 1));
        values.Add(new(group, ContextPlacementPrefix + "RandomSeed", "Random seed", PlacementRandomSeed,
            ReadOnly: !RandomYaw, Minimum: int.MinValue, Maximum: int.MaxValue, Increment: 1, DecimalPlaces: 0,
            Description: "A repeatable sequence; changing the seed restarts it. Previewing or cancelling does not consume an angle."));
    }

    private bool TryApplyContextPlacementValue(string path, object? value)
    {
        if (_room.Dimension != RoomDimension.ThreeD) return false;
        switch (path[ContextPlacementPrefix.Length..])
        {
            case "SnapToTerrain": SetSnapToTerrain(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "AlignToTerrainNormal": SetAlignToTerrainNormal(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "RandomYaw": SetRandomYaw(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); return true;
            case "RandomYawDegrees": SetRandomYawDegrees(Convert.ToSingle(value, CultureInfo.InvariantCulture)); return true;
            case "RandomSeed": SetPlacementRandomSeed(Convert.ToInt32(value, CultureInfo.InvariantCulture)); return true;
            case "SnapSelected": return SnapSelectionToTerrainWithoutRotation();
            case "AlignSelected": return AlignSelectionToTerrain();
            default: return false;
        }
    }
}
