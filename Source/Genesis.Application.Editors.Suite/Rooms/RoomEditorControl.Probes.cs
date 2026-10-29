using System.ComponentModel;
using System.Numerics;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>
/// Light Probes and Reflection Probes placed in a 3D room (Engine.Lighting.Probes): Edit > Add light
/// probe / Add reflection probe puts one where the view looks, the move gizmo places it, the Inspector
/// sets its volume, and the view draws each one's volume with the edge where it starts to fade.
/// A probe is a node of its own kind, so selection, undo, delete, layers and saving work as for
/// any other; the game captures it where it stands when the room starts.
/// </summary>
public sealed partial class RoomEditorControl
{
    public enum ProbeType { Light, Reflection }

    public enum ProbeShape { Sphere, Box }

    /// <summary>Half the size of the marker a probe is picked and outlined by, metres.</summary>
    private const float ProbeMarkerHalf = 0.25f;

    private static readonly RenderColor LightProbeColor = new(1f, 0.82f, 0.32f, 0.95f);
    private static readonly RenderColor ReflectionProbeColor = new(0.38f, 0.86f, 1f, 0.95f);

    /// <summary>Whether placed probes are drawn and can be picked (View > Types > Probes, <see cref="SetKindVisible"/>).</summary>
    public bool ShowProbes { get; private set; } = true;

    /// <summary>The probes whose volumes the view drew in its last frame (for tests).</summary>
    public int LastProbeGizmosDrawn { get; private set; }

    /// <summary>
    /// Adds a Light Probe (a sphere of bounced light) or a Reflection Probe (a box it mirrors) at
    /// <paramref name="at"/>, or where the 3D view looks, on the layer Objects are placed on, and
    /// selects it. Null in a 2D room or when that layer is locked.
    /// </summary>
    public RoomNode? AddProbe(ProbeType type, Vector3? at = null)
    {
        if (!ViewMode3D)
        {
            UpdateStatus("Probes light 3D rooms: switch the room to 3D to place one.");
            return null;
        }
        string layerId = _navigation?.ObjectsPanel.ActiveObjectLayer is { Locked: false } active ? active.Id : PlacementLayerId();
        if (_room.Layers.FirstOrDefault(layer => layer.Id == layerId) is { Locked: true })
        {
            UpdateStatus("The placement layer is locked. Choose an unlocked layer.");
            return null;
        }
        bool reflection = type == ProbeType.Reflection;
        Vector3 position = at ?? _viewport.Camera.Target;
        RoomNode node = new()
        {
            Kind = RoomNodeKind.Probe,
            Name = UniqueNodeName(reflection ? "Reflection Probe" : "Light Probe"),
            LayerId = layerId,
            EnabledIn2D = false,
            Probe = new RoomProbeData
            {
                Type = reflection ? RoomProbeData.ReflectionType : RoomProbeData.LightType,
                Shape = reflection ? RoomProbeData.BoxShape : RoomProbeData.SphereShape,
            },
        };
        node.Transform.X = position.X;
        node.Transform.Y = position.Y;
        node.Transform.Z = position.Z;
        _room.Nodes.Add(node);
        PushEdit(
            $"Add '{node.Name}'",
            () => { if (!_room.Nodes.Contains(node)) _room.Nodes.Add(node); RefreshPhase4Ui(); },
            () => { _room.Nodes.Remove(node); if (_selection.Contains(node)) Select(null); RefreshPhase4Ui(); });
        Select(node);
        RefreshPhase4Ui();
        _viewport.Invalidate();
        UpdateStatus($"Added '{node.Name}'. Move it with the gizmo; its volume is set in the Inspector.");
        return node;
    }

    private string UniqueNodeName(string stem)
    {
        for (int number = 1; ; number++)
        {
            string name = $"{stem} {number}";
            if (_room.Nodes.All(node => !string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
    }

    /// <summary>Placed probes shown now: 3D only, enabled, on a shown layer.</summary>
    private IEnumerable<RoomNode> EnumerateVisibleProbeNodes()
    {
        if (_viewport.Mode2D || !ShowProbes) yield break;
        foreach (RoomNode node in _room.Nodes)
        {
            if (node.Kind != RoomNodeKind.Probe || node.Probe is null || !node.Enabled || !node.Supports(_room.Dimension)) continue;
            if (_room.Layers.FirstOrDefault(layer => layer.Id == node.LayerId) is { Enabled: false }) continue;
            yield return node;
        }
    }

    /// <summary>The marker a probe is picked, outlined and framed by.</summary>
    private (Vector3 Min, Vector3 Max) ProbeMarkerBounds(RoomNode node)
    {
        Vector3 centre = NodePosition3D(node);
        return (centre - new Vector3(ProbeMarkerHalf), centre + new Vector3(ProbeMarkerHalf));
    }

    /// <summary>
    /// Each placed probe's marker and volume: a Light Probe's sphere in amber, a Reflection Probe's
    /// box or sphere in blue, and inside it, fainter, where its blend distance starts to fade it.
    /// </summary>
    private void DrawProbeGizmos3D(IRenderController renderer)
    {
        int drawn = 0;
        foreach (RoomNode node in EnumerateVisibleProbeNodes())
        {
            RoomProbeData data = node.Probe!;
            bool selected = _selection.Contains(node);
            RenderColor color = data.IsReflection ? ReflectionProbeColor : LightProbeColor;
            RenderColor faint = new(color.R, color.G, color.B, selected ? 0.55f : 0.3f);
            if (!selected) color = new RenderColor(color.R, color.G, color.B, 0.7f);
            Vector3 centre = NodePosition3D(node);
            DrawGizmoBox3D(renderer, centre, ProbeMarkerHalf * 2f, color);
            float blend = data.BlendDistance;
            if (data.IsBox)
            {
                Vector3 half = new(data.SizeAxis(0) * 0.5f, data.SizeAxis(1) * 0.5f, data.SizeAxis(2) * 0.5f);
                DrawGizmoAabb3D(renderer, centre - half, centre + half, color);
                Vector3 inner = Vector3.Max(half - new Vector3(blend), Vector3.Zero);
                if (blend > 0f && inner.X > 0f && inner.Y > 0f && inner.Z > 0f)
                    DrawGizmoAabb3D(renderer, centre - inner, centre + inner, faint);
            }
            else
            {
                DrawGizmoSphere3D(renderer, centre, data.Radius, color);
                if (blend > 0f && data.Radius - blend > 0f)
                    DrawGizmoSphere3D(renderer, centre, data.Radius - blend, faint);
            }
            drawn++;
        }
        LastProbeGizmosDrawn = drawn;
    }

    /// <summary>A sphere as its three circles round the world's axes.</summary>
    private void DrawGizmoSphere3D(IRenderController renderer, Vector3 centre, float radius, RenderColor color)
    {
        const int segments = 40;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 previous = Point(axis, 0f);
            for (int i = 1; i <= segments; i++)
            {
                Vector3 next = Point(axis, i * MathF.Tau / segments);
                DrawGizmoLine3D(renderer, previous, next, color);
                previous = next;
            }
        }

        Vector3 Point(int axis, float angle)
        {
            float c = MathF.Cos(angle) * radius, s = MathF.Sin(angle) * radius;
            return centre + axis switch
            {
                0 => new Vector3(c, s, 0f),
                1 => new Vector3(c, 0f, s),
                _ => new Vector3(0f, c, s),
            };
        }
    }

    private void SetProbeValue<T>(RoomNode node, string what, Func<RoomProbeData, T> read, Action<RoomProbeData, T> write, T value)
    {
        if (node.Probe is not { } data) return;
        SetNodeValue(node, $"Set '{node.Name}' {what}", () => read(data), next => { write(data, next); data.Normalize(); }, value);
        _viewport.Invalidate();
    }

    /// <summary>The Inspector's fields for a placed Light Probe or Reflection Probe.</summary>
    private sealed class ProbeInspector : NodeInspector
    {
        public ProbeInspector(RoomEditorControl editor, RoomNode node) : base(editor, node) { }
        private RoomProbeData Data => Node.Probe!;

        [Category("Probe"), Description("Light: surfaces inside take their bounced light from it. Reflection: smooth surfaces inside mirror what it sees.")]
        public ProbeType Type
        {
            get => Data.IsReflection ? ProbeType.Reflection : ProbeType.Light;
            set => Editor.SetProbeValue(Node, "type", data => data.IsReflection ? ProbeType.Reflection : ProbeType.Light,
                (data, next) => data.Type = next == ProbeType.Reflection ? RoomProbeData.ReflectionType : RoomProbeData.LightType, value);
        }

        [Category("Probe"), Description("A Reflection Probe's volume: a box (rooms, corridors) or a sphere. A Light Probe is always a sphere.")]
        public ProbeShape Shape
        {
            get => Data.IsBox ? ProbeShape.Box : ProbeShape.Sphere;
            set => Editor.SetProbeValue(Node, "shape", data => data.IsBox ? ProbeShape.Box : ProbeShape.Sphere,
                (data, next) => data.Shape = next == ProbeShape.Box ? RoomProbeData.BoxShape : RoomProbeData.SphereShape, value);
        }

        [Category("Probe"), Description("How far a sphere reaches, in metres.")]
        public float Radius
        {
            get => Data.Radius;
            set => Editor.SetProbeValue(Node, "radius", data => data.Radius, (data, next) => data.Radius = next, value);
        }

        [Category("Probe"), DisplayName("Size X"), Description("A box's width, in metres.")]
        public float SizeX
        {
            get => Data.SizeAxis(0);
            set => Editor.SetProbeValue(Node, "size", data => data.SizeAxis(0), (data, next) => data.Size[0] = next, value);
        }

        [Category("Probe"), DisplayName("Size Y"), Description("A box's height, in metres.")]
        public float SizeY
        {
            get => Data.SizeAxis(1);
            set => Editor.SetProbeValue(Node, "size", data => data.SizeAxis(1), (data, next) => data.Size[1] = next, value);
        }

        [Category("Probe"), DisplayName("Size Z"), Description("A box's depth, in metres.")]
        public float SizeZ
        {
            get => Data.SizeAxis(2);
            set => Editor.SetProbeValue(Node, "size", data => data.SizeAxis(2), (data, next) => data.Size[2] = next, value);
        }

        [Category("Probe"), DisplayName("Blend distance"), Description("How far inside its edge the probe fades in, in metres; neighbouring probes blend over it (0 = a hard edge).")]
        public float BlendDistance
        {
            get => Data.BlendDistance;
            set => Editor.SetProbeValue(Node, "blend distance", data => data.BlendDistance, (data, next) => data.BlendDistance = next, value);
        }

        [Category("Probe"), DisplayName("Box projection"), Description("A Reflection Probe box mirrors its walls where they are, so a floor reflects the room it is in (best for boxes the size of the room).")]
        public bool BoxProjection
        {
            get => Data.BoxProjection;
            set => Editor.SetProbeValue(Node, "box projection", data => data.BoxProjection, (data, next) => data.BoxProjection = next, value);
        }
    }
}
