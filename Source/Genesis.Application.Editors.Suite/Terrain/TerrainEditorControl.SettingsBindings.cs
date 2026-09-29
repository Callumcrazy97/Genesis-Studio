using System.Windows.Forms;
using Genesis.World.Foliage;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

public sealed partial class TerrainEditorControl
{
    // Drafts keep field edits separate from committed settings and undo snapshots.
    // Generation and undo can replace the source settings; rebind the visible form then.
    private TerrainPathSettings _pathAuthoringSettings = null!;
    private FoliageScatterSettings _foliageAuthoringSettings = null!;
    private TerrainPathSettings? _pathSettingsSource;
    private FoliageScatterSettings? _foliageSettingsSource;
    private Control? _pathSettingsInspector;
    private Control? _foliageSettingsInspector;

    private void RefreshTerrainSettingsBindings()
    {
        if (_pathSettingsInspector?.Parent is not null && !ReferenceEquals(_pathSettingsSource, _nature.PathSettings))
        {
            _pathSettingsSource = _nature.PathSettings;
            _pathAuthoringSettings = Clone(_nature.PathSettings);
            ReplaceSettingsForm(ref _pathSettingsInspector, _pathAuthoringSettings,
                "Generation is deterministic and grades/paints the terrain with full undo support.");
        }
        if (_foliageSettingsInspector?.Parent is not null && !ReferenceEquals(_foliageSettingsSource, _nature.FoliageSettings))
        {
            _foliageSettingsSource = _nature.FoliageSettings;
            _foliageAuthoringSettings = Clone(_nature.FoliageSettings);
            ReplaceSettingsForm(ref _foliageSettingsInspector, _foliageAuthoringSettings,
                "The same deterministic cell streaming, LOD and performance budgets run in this preview and in gameplay.");
        }
    }

    private void ReplaceSettingsForm(ref Control? form, object settings, string note)
    {
        Control parent = form!.Parent!;
        int index = parent.Controls.GetChildIndex(form);
        parent.SuspendLayout();
        form.Dispose();
        form = Inspector.InspectorBuilder.BuildForObject(settings, note, inline: true);
        parent.Controls.Add(form);
        parent.Controls.SetChildIndex(form, index);
        parent.ResumeLayout(true);
        QueueTerrainLayout();
    }
}
