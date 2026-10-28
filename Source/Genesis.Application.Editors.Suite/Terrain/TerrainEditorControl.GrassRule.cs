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
            _grassRuleDraft, GrassRuleNote, inline: true);
        inspector.Dock = DockStyle.Top;
        page.Controls.Add(inspector);
        LabelGrassLayerDensities();
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
        void Use(TerrainGrassRuleSettings rule, bool rebind)
        {
            _nature.GrassRule = rule.Clone();
            ResetGrassField();
            // Undo and redo show the restored rule in the fields, as the scatter settings do.
            if (rebind) RebindGrassRuleForm();
            _viewport.Invalidate(true);
        }

        Use(after, rebind: false);
        PushEdit(after.Enabled ? "Apply grass around the camera" : "Switch off grass around the camera",
            () => Use(after, rebind: true),
            () => Use(before, rebind: true));
        UpdateStatus();
    }

    private const string GrassRuleNote = "The same rule grows the grass in this viewport, the room preview and the running game.";

    /// <summary>Shows the saved rule in the grass fields, once they are built.</summary>
    private void RebindGrassRuleForm()
    {
        if (_grassRuleInspector?.Parent is null) return;
        _grassRuleDraft = _nature.GrassRule.Clone();
        ReplaceSettingsForm(ref _grassRuleInspector, _grassRuleDraft, GrassRuleNote);
        LabelGrassLayerDensities();
    }

    /// <summary>
    /// Names each density field after its painted layer ("Layer 1 (Grass) density"), so it is clear
    /// which ground it grows on. Layers this terrain does not have keep just their number.
    /// </summary>
    private void LabelGrassLayerDensities()
    {
        if (_grassRuleInspector is null) return;
        const string prefix = "InspectorDrawer_Layer", suffix = "Density";
        Control.ControlCollection rows = _grassRuleInspector.Controls;
        // The fields are a caption followed by its field (see InspectorBuilder's inline form).
        for (int index = 1; index < rows.Count; index++)
        {
            string name = rows[index].Name;
            if (rows[index - 1] is not Label caption || name.Length <= prefix.Length + suffix.Length
                || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), out int number)
                || number < 1)
                continue;
            string layer = number <= _settings.Layers.Count ? _settings.Layers[number - 1].Name?.Trim() ?? "" : "";
            caption.Text = layer.Length > 0 ? $"Layer {number} ({layer}) density" : $"Layer {number} density";
        }
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
