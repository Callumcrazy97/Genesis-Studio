using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Genesis.Runtime.Assets;
using Genesis.Runtime.Debugger;
using Genesis.Shared.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Runtime.Project;

public sealed partial class RoomTerrainSubsystem
{
    private sealed class TerrainMaterialState
    {
        public DateTime NextCheck;
        public string Signature = "", PendingSignature = "";
        public Task<TerrainSurfaceMaterialPixels> Pending;
        public TerrainSurfaceMaterialPixels Pixels;
        public MeshDrawCall? Draw;
    }

    public int AuthoredMaterialGroundCount => _entries.Count(entry => entry.Material.Draw.HasValue);

    private void UpdateMaterial(Entry entry, IRenderController renderer)
    {
        TerrainMaterialState state = entry.Material;
        // The Room's explicit surface override retains its existing meaning. Clearing it
        // restores the saved Terrain layers without adding a second material authoring UI.
        if (!string.IsNullOrWhiteSpace(entry.Node.Terrain.Albedo))
        {
            if (state.Draw.HasValue) ReleaseBoundMeshes(entry);
            return;
        }
        if (state.Pending is { IsCompleted: true } prepared)
        {
            state.Pending = null; state.Signature = state.PendingSignature;
            if (prepared.IsCompletedSuccessfully)
            {
                ReleaseMaterial(entry); state.Pixels = prepared.Result;
            }
            else RuntimeDiagnostics.ReportAssetProblem($"Terrain material '{entry.Node.Name}': {prepared.Exception?.GetBaseException().Message}");
        }
        if (state.Pixels is { } pixels && !state.Draw.HasValue)
        {
            state.Draw = TerrainSurfaceMaterialBinding.Create(renderer, pixels, entry.Terrain.CaptureSplatState(), entry.Terrain.ResolutionX, entry.Terrain.ResolutionZ);
            entry.Ground.SurfaceMaterial = state.Draw;
            if (entry.Bound) entry.Ground.Bind(renderer, state.Draw.Value.Texture, 1);
        }
        if (state.Pending != null || DateTime.UtcNow < state.NextCheck || !entry.ResourcePath.EndsWith(".terrain.json", StringComparison.OrdinalIgnoreCase)) return;
        state.NextCheck = DateTime.UtcNow.AddSeconds(1);
        try
        {
            (List<TerrainMaterialLayer> layers, TerrainSurfaceOptions options) = TerrainSurfaceMaterialBaker.LoadSurface(entry.ResourcePath);
            if (layers.Count == 0) return;
            long ImageStamp(TerrainMaterialLayer layer)
            {
                string path = string.IsNullOrWhiteSpace(layer.Image) ? entry.ResourcePath : ResourceNames.Resolve(_projectPath, layer.Image, ResourceType.Image);
                return string.IsNullOrWhiteSpace(path) ? 0 : File.GetLastWriteTimeUtc(path).Ticks;
            }
            string signature = File.GetLastWriteTimeUtc(entry.ResourcePath).Ticks + "|" + File.GetLastWriteTimeUtc(entry.BinaryPath).Ticks
                + "|" + string.Join('|', layers.Select(ImageStamp));
            if (signature == state.Signature) return;
            state.PendingSignature = signature;
            byte[] splats = entry.Terrain.CaptureSplatState();
            int width = entry.Terrain.ResolutionX, height = entry.Terrain.ResolutionZ;
            float worldWidth = (width - 1) * entry.Terrain.CellSize, worldHeight = (height - 1) * entry.Terrain.CellSize;
            state.Pending = Task.Run(() => TerrainSurfaceMaterialBaker.Bake(_projectPath, layers, splats, width, height, worldWidth, worldHeight, options: options));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        { RuntimeDiagnostics.ReportAssetProblem($"Terrain material '{entry.Node.Name}': {exception.Message}"); }
    }

    private static void ReleaseMaterial(Entry entry)
    {
        if (entry.Material.Draw is { } draw && entry.Renderer is { } renderer)
            TerrainSurfaceMaterialBinding.Release(renderer, draw);
        entry.Material.Draw = null;
        if (entry.Ground != null) entry.Ground.SurfaceMaterial = null;
    }
}
