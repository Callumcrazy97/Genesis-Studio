using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Genesis.Shared.Interfaces;
using Genesis.World.Foliage;

namespace Genesis.Runtime.Project;

public sealed partial class RoomTerrainSubsystem
{
    /// <summary>What each terrain's grass around the camera held and drew in the last frame.</summary>
    public IReadOnlyList<TerrainGrassStatistics> GrassRuleStatistics =>
        _entries.Select(entry => entry.GrassField?.Statistics ?? default).ToArray();

    /// <summary>
    /// Grows and draws grass around the camera when the terrain's nature document switches the
    /// rule on. Growth is budgeted per frame inside the field, so this never holds a frame up.
    /// </summary>
    private static void SubmitGrassRule(
        Entry entry,
        Vector3 camera,
        Matrix4x4 viewProjection,
        Matrix4x4 placement,
        IRenderController renderer)
    {
        TerrainGrassRuleSettings rule = entry.Nature?.GrassRule;
        if (rule is not { Enabled: true } || entry.Terrain == null || renderer == null)
        {
            if (entry.GrassField != null)
            {
                entry.GrassField.Dispose();
                entry.GrassField = null;
            }

            return;
        }

        // Reloaded heights, paint or rule: grow the grass again from the new ones.
        if (entry.GrassField != null
            && (!ReferenceEquals(entry.GrassField.Terrain, entry.Terrain) || !entry.GrassField.GrowsFrom(rule)))
        {
            entry.GrassField.Dispose();
            entry.GrassField = null;
        }

        entry.GrassField ??= new TerrainGrassField(entry.Terrain, rule);
        entry.GrassField.Update(camera, placement);
        entry.GrassField.Draw(renderer, camera, viewProjection);
    }
}
