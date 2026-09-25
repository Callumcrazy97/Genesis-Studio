using System.Numerics;
using System.Windows.Forms;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

public sealed partial class ShaderEditorControl
{
    public EditorFloorStyle FloorStyle => _floorStyle;

    public bool ShowEditorFloor => _floorStyle.DrawsPlate();

    public void SetFloorStyle(EditorFloorStyle style)
    {
        _floorStyle = style;
        if (style == EditorFloorStyle.GridOnly)
        {
            _showGrid = true;
        }

        _viewport.FloorStyle = style;
        _viewport.Host.Invalidate();
    }

    private ToolStripDropDownButton BuildShaderViewMenu() =>
        EditorViewMenuChrome.BuildViewMenu(
            "Grid and reference-floor options for the shader preview",
            grid: new EditorViewMenuChrome.GridBinding
            {
                Read = () => _showGrid,
                Write = value => _showGrid = value,
                Invalidate = () => _viewport.Host.Invalidate(),
            },
            floorStyle: new EditorViewMenuChrome.FloorStyleBinding
            {
                Read = () => _floorStyle,
                Write = SetFloorStyle,
                Invalidate = () => _viewport.Host.Invalidate(),
            });

    private Mesh3DState CreateShaderSceneState()
    {
        Mesh3DState state = EditorSceneLighting.Create(
            showFloor: _floorStyle.DrawsPlate() && _document.Pipeline == ShaderAssetPipeline.Mesh && (_document.TargetType != ShaderTargetType.Terrain || IsObjectPreview));
        state.BackgroundColor = new Vector3(EditorChrome.Canvas.R, EditorChrome.Canvas.G, EditorChrome.Canvas.B) / 255f;
        state.FogEnabled = state.FogScreenSpace = false;
        state.FloorFollowsCamera = false;
        return state;
    }

    private void DrawShaderViewportOverlay(IRenderController renderer)
    {
        if (!_showGrid || _document.Pipeline != ShaderAssetPipeline.Mesh)
        {
            return;
        }

        EditorViewportGridHelper.DrawGrid3D(
            _viewport,
            renderer,
            cellSize: 1f,
            rgba: [1f, 1f, 1f, 0.07f],
            horizontalExtent: 8f,
            verticalExtent: 0f);
    }
}
