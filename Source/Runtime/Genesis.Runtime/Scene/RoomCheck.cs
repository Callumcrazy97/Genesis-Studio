using System;
using System.Collections.Generic;
using System.IO;
using Genesis.Shared.Assets;

namespace Genesis.Runtime.Scene;

/// <summary>
/// Every Room of a project read once as the game would read it, without running it: each must
/// load, and every Object and Terrain it places must be one the project has. Export runs it with
/// the script check, so a game is not shipped with a room that stops when the player reaches it.
/// </summary>
public static class RoomCheck
{
    /// <summary>One line per problem, naming the room ("Cellar: places 'Crate', which is not an Object in the project").</summary>
    public static IReadOnlyList<string> Problems(string projectPath)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return problems;
        foreach (NamedResource room in ResourceCatalog.For(projectPath).Entries)
        {
            if (room.Type != ResourceType.Room) continue;
            RoomAsset asset;
            try
            {
                asset = RoomAssetLoader.Parse(room.FullPath);
                if (asset == null)
                {
                    problems.Add($"{room.Name}: could not be read.");
                    continue;
                }
                asset.Normalize();
                RoomAssetLoader.Validate(asset);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException
                                                  or Newtonsoft.Json.JsonException or FormatException)
            {
                problems.Add($"{room.Name}: could not be read ({exception.Message}).");
                continue;
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RoomNode node in asset.Nodes)
            {
                if (node.Kind == RoomNodeKind.GameObject && !string.IsNullOrWhiteSpace(node.GameObject?.Prefab))
                {
                    string prefab = node.GameObject.Prefab;
                    if (RoomSceneBuilder.ResolvePrefabPath(projectPath, prefab) == null && reported.Add("o:" + prefab))
                        problems.Add($"{room.Name}: places '{prefab}', which is not an Object in the project.");
                }
                else if (node.Kind == RoomNodeKind.Terrain && !string.IsNullOrWhiteSpace(node.Terrain?.Asset))
                {
                    string terrain = node.Terrain.Asset;
                    if (string.IsNullOrEmpty(ResourceCatalog.Resolve(projectPath, terrain, ResourceType.Terrain)) && reported.Add("t:" + terrain))
                        problems.Add($"{room.Name}: uses the terrain '{terrain}', which is not in the project.");
                }
            }
        }
        return problems;
    }
}
