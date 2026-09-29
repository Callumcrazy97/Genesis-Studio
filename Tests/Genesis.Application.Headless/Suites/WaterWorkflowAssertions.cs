using System.Numerics;
using Genesis.World.Terrain;
using Genesis.World.Water;

namespace Genesis.Application.Headless.Suites;

internal static class WaterWorkflowAssertions
{
    internal static void SurfaceAndVolume(TerrainWaterDefinition definition)
    {
        WaterBody body = definition.ToWaterBody();
        var surface = WaterSurfaceMesh.BuildVisual(body, Vector3.Zero);
        HeadlessHarness.Assert(surface.Vertices is { Length: > 0 }
            && surface.Indices is { Length: >= 3 }
            && surface.Vertices.All(vertex => Math.Abs(vertex.Position.Y - definition.SurfaceHeight) < .001f),
            "Authored standing water has no visible surface at the saved water level.");

        var volume = TerrainColliderMesh.CreateVolume(definition, Matrix4x4.Identity);
        HeadlessHarness.Assert(Math.Abs(volume.SurfaceY - definition.SurfaceHeight) < .001f
            && volume.Maximum.Y - volume.Minimum.Y >= definition.PhysicsDepth - .001f
            && Math.Abs(volume.Maximum.X - volume.Minimum.X - definition.SizeX) < .001f
            && Math.Abs(volume.Maximum.Z - volume.Minimum.Z - definition.SizeZ) < .001f
            && volume.Swimmable == definition.Swimmable
            && Math.Abs(volume.Density - definition.FluidDensity) < .001f
            && Math.Abs(body.VisualDepth - definition.PhysicsDepth) < .001f,
            "Authored standing water lost its depth, footprint, density or explicit swimming mode.");
    }
}
