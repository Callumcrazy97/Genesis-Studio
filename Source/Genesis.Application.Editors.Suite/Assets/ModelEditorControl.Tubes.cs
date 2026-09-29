using System.Numerics;
using System.Windows.Forms;
using Genesis.Application.Editors.Image.Controls;
using Genesis.Runtime.Modeling;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ModelEditorControl
{
    private readonly List<Vector3> _tubePoints = [];
    private float _tubeWidth = .15f, _tubeTaper = .7f;
    private CollapsibleSection? _tubeSection;
    private NumericUpDown? _tubeWidthInput, _tubeTaperInput;
    public int PendingTubeTriangleCount => _gesture && _tool == ModelAuthoringTool.Tube && _tubePoints.Count > 1
        ? (_tubePoints.Count - 1) * ModelTubeGeometry.Sides * 2 + ModelTubeGeometry.Sides * 2 : 0;
    protected override string PreviewStatusDetail => PendingTubeTriangleCount > 0 ? $" · drawing tube: {PendingTubeTriangleCount:N0} triangles" : "";

    public void ConfigureTube(float width, float taper)
    {
        if (!float.IsFinite(width) || width < .001f || width > 1000 || !float.IsFinite(taper) || taper < 0 || taper > .95f)
            throw new ArgumentOutOfRangeException(nameof(width));
        _tubeWidth = width; _tubeTaper = taper;
        if (_tubeWidthInput is not null) _tubeWidthInput.Value = (decimal)width;
        if (_tubeTaperInput is not null) _tubeTaperInput.Value = (decimal)taper * 100;
    }
    public void CreateTube(IReadOnlyList<Vector3> points, float width, float taper)
    {
        GModelMesh mesh = ModelTubeGeometry.Build(points, width, taper);
        mesh.Name = "Drawn tube " + (Asset.Meshes.Count + 1);
        ChangeAsset("Draw tube", () => { mesh.MaterialIndex = NeutralMaterialIndex(); Asset.Meshes.Add(mesh); });
        SelectPart(Asset.Meshes.Count - 1);
        Status.Text = $"Created tube · {mesh.Indices.Length / 3:N0} triangles · move or rotate it, then Save and Use in game.";
    }
    private void AddTubePoint(Vector3 point)
    {
        if (_tubePoints.Count >= 512) return;
        float spacing = Math.Max(.002f, _tubeWidth * .2f);
        if (_tubePoints.Count == 0 || Vector3.DistanceSquared(_tubePoints[^1], point) >= spacing * spacing) _tubePoints.Add(point);
    }
}
