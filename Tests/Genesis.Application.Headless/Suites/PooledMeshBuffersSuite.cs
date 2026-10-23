using System.Numerics;
using System.Windows.Forms;
using Genesis.Rendering.Core;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Headless.Suites;

/// <summary>
/// A script mesh rebuilt again and again (RegisterMeshPooled) gives its buffers back when released:
/// after resting a few frames they are updated in place for the next mesh of a similar size, which
/// must then draw its own geometry and colour, on every renderer.
/// </summary>
internal static class PooledMeshBuffersSuite
{
    private const int Width = 96, Height = 64;

    public static void Run(HeadlessContext ctx)
    {
        HeadlessHarness.BeginMajor(ctx.Report, "Pooled mesh buffers");
        HeadlessHarness.RunCase(ctx.Report, "Render.Meshes.APooledMeshReusesReleasedBuffersOnEveryBackend", () => Reuse(ctx));
    }

    // A quad filling the middle of the view, all one colour.
    private static (MeshVertex[] Vertices, ushort[] Indices) Quad(Vector4 colour, float size)
    {
        var vertices = new MeshVertex[4];
        Vector3[] corners = [new(-size, -size, 4), new(size, -size, 4), new(size, size, 4), new(-size, size, 4)];
        for (int i = 0; i < 4; i++)
            vertices[i] = new MeshVertex { Position = corners[i], Normal = new Vector3(0, 0, -1), UV = Vector2.Zero, Color = colour };
        return (vertices, [0, 2, 1, 0, 3, 2]);
    }

    private static void Reuse(HeadlessContext ctx)
    {
        var failures = new List<string>();
        var readings = new List<string>();
        Matrix4x4 view = Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, Width / (float)Height, 0.1f, 50f);
        foreach (RenderBackendDescriptor backend in RenderBackendCatalog.All)
        {
            using Form host = UnattendedWindowing.NewHost(Width, Height); UnattendedWindowing.ShowWithoutFocus(host);
            IRenderController renderer;
            try
            {
                renderer = RenderControllerFactory.Create(backend.Backend);
                renderer.Initialize(host.Handle, Width, Height);
            }
            catch (Exception error) { readings.Add($"{backend.ShortName}: not available ({error.Message})"); continue; }
            using (renderer)
            {
                byte[] Frame(MeshHandle mesh)
                {
                    Mesh3DState state = Mesh3DState.Default;
                    state.ShowFloor = false; state.ShowSunVisual = false; state.FogEnabled = false; state.ShadowsEnabled = false;
                    state.LightingEnabled = false;
                    renderer.SetMesh3DState(state); renderer.Set3DFrameActive(true);
                    renderer.SetCamera3D(view, projection);
                    renderer.BeginFrame(); renderer.Clear(0f, 0f, 0f);
                    if (mesh.IsValid)
                        renderer.DrawMesh(new MeshDrawCall { Mesh = mesh, World = Matrix4x4.Identity, Tint = RenderColor.White, Alpha = 1, Flags = MeshDrawFlags.NoShadow | MeshDrawFlags.NoCull });
                    renderer.EndFrame();
                    byte[] pixels = renderer.TryReadSubmittedFramePixels(out int w, out int h, out byte[] read) && w == Width && h == Height ? read : [];
                    renderer.Present();
                    return pixels;
                }
                // The middle pixel's red and green (BGRA).
                (int Red, int Green) Middle(byte[] pixels)
                {
                    int at = ((Height / 2) * Width + Width / 2) * 4;
                    return pixels.Length > at + 2 ? (pixels[at + 2], pixels[at + 1]) : (-1, -1);
                }

                (MeshVertex[] redVertices, ushort[] redIndices) = Quad(new Vector4(1, 0, 0, 1), 1f);
                MeshHandle red = renderer.RegisterMeshPooled(redVertices, redIndices);
                (int Red, int Green) first = Middle(Frame(red));
                renderer.ReleaseMesh(red);
                // The released buffers rest (eight frames and a tenth of a second) before reuse.
                for (int i = 0; i < 12; i++) Frame(MeshHandle.Invalid);
                Thread.Sleep(120);
                Frame(MeshHandle.Invalid);
                (long madeBefore, long reusedBefore) = renderer is GpuRenderController gpu ? gpu.PooledMeshBuffers : (0, 0);
                (MeshVertex[] greenVertices, ushort[] greenIndices) = Quad(new Vector4(0, 1, 0, 1), 1.2f);
                MeshHandle green = renderer.RegisterMeshPooled(greenVertices, greenIndices);
                (int Red, int Green) second = Middle(Frame(green));
                (long madeAfter, long reusedAfter) = renderer is GpuRenderController gpuAfter ? gpuAfter.PooledMeshBuffers : (0, 0);
                renderer.ReleaseMesh(green);
                bool reused = renderer is not GpuRenderController || (reusedAfter - reusedBefore == 2 && madeAfter == madeBefore);
                // A renderer whose submitted frame cannot be read back here has its reuse checked, not its colours.
                bool readable = first.Red >= 0 && second.Red >= 0;
                bool right = !readable || (first.Red > 200 && first.Green < 60 && second.Green > 200 && second.Red < 60);
                readings.Add($"{backend.ShortName}: first {first}, second {second}{(readable ? "" : " (frame not readable: colours not checked)")}, buffers reused {reusedAfter - reusedBefore}, made {madeAfter - madeBefore}");
                if (!reused || !right) failures.Add($"{backend.ShortName}: red {first}, then green {second}; reused {reusedAfter - reusedBefore} buffers, made {madeAfter - madeBefore}");
            }
        }
        Console.WriteLine("Pooled mesh buffers:\n  " + string.Join("\n  ", readings));
        HeadlessHarness.Assert(failures.Count == 0, "A pooled mesh did not reuse its buffers or drew wrong: " + string.Join("; ", failures));
    }
}
