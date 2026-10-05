using System.Numerics;
using System.Windows.Forms;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    // A draft keeps field edits apart from the saved rule until Apply, as the scatter settings do.
    private TerrainGrassRuleSettings _grassRuleDraft = new();
    private Control? _grassRuleInspector;
    private TerrainGrassField? _grassField;
    private TerrainAsset? _grassFieldTerrain;
    private int _grassFieldGroundRevision;
    private int _grassFieldPaintRevision;
    private List<TerrainWaterDefinition>? _grassFieldWaters;

    /// <summary>What the viewport's grass around the camera held and drew in the last frame.</summary>
    public TerrainGrassStatistics LastGrassRuleStatistics => _grassField?.Statistics ?? default;

    /// <summary>The saved grass rule (a copy).</summary>
    public TerrainGrassRuleSettings GrassRule => _nature.GrassRule.Clone();

    /// <summary>Adds the grass-around-the-camera settings to the foliage page.</summary>
    private void AddGrassRuleSection(FlowLayoutPanel page)
    {
        page.Controls.Add(MakeContextCaption("Grass Around the Camera"));
        page.Controls.Add(new Label
        {
            Text = "Grows grass from the painted layers in cells around the camera as it moves, instead of storing every tuft. "
                + "Give each painted layer a density, switch it on and Apply.",
            AutoSize = true, ForeColor = EditorChrome.Muted, Font = EditorChrome.SmallFont,
        });
        _grassRuleDraft = _nature.GrassRule.Clone();
        Control inspector = _grassRuleInspector = Inspector.InspectorBuilder.BuildForObject(
            _grassRuleDraft,
            "The same rule grows the grass in this viewport, the room preview and the running game.", inline: true);
        inspector.Dock = DockStyle.Top;
        page.Controls.Add(inspector);
        page.Controls.Add(MakeContextAction(
            "Apply Grass Rule",
            "Save the grass-around-the-camera rule above with this terrain",
            () => ApplyGrassRule(_grassRuleDraft)));
    }

    /// <summary>Replaces the terrain's grass rule as one undo step.</summary>
    public void ApplyGrassRule(TerrainGrassRuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TerrainGrassRuleSettings before = _nature.GrassRule.Clone();
        TerrainGrassRuleSettings after = settings.Clone();
        after.Normalize();
        void Use(TerrainGrassRuleSettings rule)
        {
            _nature.GrassRule = rule.Clone();
            ResetGrassField();
            _viewport.Invalidate(true);
        }

        Use(after);
        PushEdit(after.Enabled ? "Apply grass around the camera" : "Switch off grass around the camera",
            () => Use(after),
            () => Use(before));
        UpdateStatus();
    }

    private void ResetGrassField()
    {
        _grassField?.Dispose();
        _grassField = null;
        _grassFieldTerrain = null;
        _grassFieldWaters = null;
    }

    /// <summary>Grows and draws the rule's grass around the viewport camera when the rule is on.</summary>
    private void DrawGrassRule(IRenderController renderer, Vector3 camera, Matrix4x4 viewProjection)
    {
        TerrainGrassRuleSettings rule = _nature.GrassRule;
        if (rule is not { Enabled: true } || _terrain is null)
        {
            if (_grassField is not null) ResetGrassField();
            return;
        }

        // Replaced ground, changed water or a changed rule starts again.
        if (_grassField is not null && (!ReferenceEquals(_grassFieldTerrain, _terrain)
            || !ReferenceEquals(_grassFieldWaters, _nature.WaterBodies) || !_grassField.GrowsFrom(rule)))
            ResetGrassField();

        if (_grassField is null)
        {
            _grassField = new TerrainGrassField(_terrain, rule, _nature.WaterBodies);
            _grassFieldTerrain = _terrain;
            _grassFieldWaters = _nature.WaterBodies;
            _grassFieldGroundRevision = _groundRevision;
            _grassFieldPaintRevision = MaterialPreviewRevision;
        }
        else if (_grassFieldGroundRevision != _groundRevision || _grassFieldPaintRevision != MaterialPreviewRevision)
        {
            // Sculpted or painted: regrow in place, so the grass follows the stroke without going bare.
            _grassField.Refresh();
            _grassFieldGroundRevision = _groundRevision;
            _grassFieldPaintRevision = MaterialPreviewRevision;
        }

        _grassField.Update(camera, Matrix4x4.Identity);
        _grassField.Draw(renderer, camera, viewProjection);
        // Keep painting frames until every cell in reach has grown.
        if (_grassField.Pending) _viewport.Invalidate(true);
    }
}
