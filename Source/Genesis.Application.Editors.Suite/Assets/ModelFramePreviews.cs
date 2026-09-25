using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using Genesis.Rendering.Core;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Assets;

/// <summary>One software renderer per timeline, with bounded background requests.</summary>
internal sealed class ModelFramePreviews : IDisposable
{
    internal const int Size = 96;
    internal sealed record Request(int Generation, GModelAsset Asset, string Root, int[] Frames, IntPtr Handle);
    internal sealed record Result(int Generation, int Frame, byte[]? Pixels, string? Error = null);
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _generation, _disposed;

    public ModelFramePreviews() => _worker = Task.Run(Run);
    public void Reset(int generation) => Volatile.Write(ref _generation, generation);
    public bool Submit(Request request) => _requests.Writer.TryWrite(request);
    public bool TryTake(out Result? result) => _results.TryDequeue(out result);

    public static GModelAsset Snapshot(GModelAsset asset, GModelAnimationClip clip) => new()
    {
        Name = asset.Name, Culling = asset.Culling, WindingOrder = asset.WindingOrder,
        Bounds = ModelPoseWorkflow.Copy(asset.Bounds), Pivot = ModelPoseWorkflow.Copy(asset.Pivot),
        ImportSettings = ModelPoseWorkflow.Copy(asset.ImportSettings), SkinningMode = asset.SkinningMode,
        Meshes = asset.Meshes.Select(ModelPoseWorkflow.Copy).ToList(), Rig = ModelPoseWorkflow.Copy(asset.Rig),
        Materials = asset.Materials.Select(ModelPoseWorkflow.Copy).ToList(), Animations = [ModelPoseWorkflow.Copy(clip)],
    };

    private async Task Run()
    {
        IRenderController? renderer = null;
        var models = new RuntimeModelRenderSystem(); int loadedGeneration = -1;
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                if (request.Generation != Volatile.Read(ref _generation)) continue;
                try
                {
                    renderer ??= RenderControllerFactory.Create(RenderBackendOption.Software);
                    renderer.Initialize(request.Handle, Size, Size);
                    if (loadedGeneration != request.Generation)
                    { models.InvalidateAssets(renderer); loadedGeneration = request.Generation; }
                    var asset = request.Asset; var clip = asset.Animations[0];
                    Vector3 min = asset.Bounds.Min - asset.Pivot.Position, max = asset.Bounds.Max - asset.Pivot.Position;
                    Vector3 center = (min + max) * .5f;
                    float span = Math.Max(.01f, Math.Max(max.X - min.X, max.Y - min.Y)) * 1.16f;
                    float distance = Math.Max(span, max.Z - min.Z) * 3;
                    Vector3 eye = center + Vector3.UnitZ * distance;
                    var state = EditorSceneLighting.Create(false, false, distance * 3);
                    state.BackgroundColor = new Vector3(.075f, .085f, .11f);
                    state.FogEnabled = state.FogScreenSpace = false;
                    state.AmbientColor = new Vector3(.7f); state.AmbientGroundColor = new Vector3(.45f);
                    foreach (int frame in request.Frames)
                    {
                        if (_stop.IsCancellationRequested || request.Generation != Volatile.Read(ref _generation)) break;
                        // Software previews use the canonical skin palette on the CPU. The
                        // software rasterizer does not execute the GPU skinning vertex shader.
                        BakeSkin(asset, clip.Frames[frame].LocalBoneTransforms);
                        models.InvalidateAssets(renderer);
                        renderer.BeginFrame(); renderer.Clear(.075f, .085f, .11f);
                        renderer.SetMesh3DState(state);
                        renderer.SetCamera3D(Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY),
                            Matrix4x4.CreateOrthographic(span, span, Math.Max(.00001f, distance / 1000), distance * 3));
                        models.DrawAsset(asset, request.Root, Matrix4x4.Identity,
                            new RuntimeModelAnimationState(clip.Name, frame / Math.Max(1, clip.Fps), clip.Fps, false), renderer);
                        renderer.EndFrame();
                        if (!renderer.TryReadFramePixels(out _, out _, out var pixels))
                            throw new InvalidOperationException("The frame preview could not be rendered.");
                        _results.Enqueue(new(request.Generation, frame, pixels));
                    }
                }
                catch (Exception ex) { _results.Enqueue(new(request.Generation, -1, null, ex.Message)); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (renderer is not null) { models.InvalidateAssets(renderer); renderer.Dispose(); }
        }
    }

    internal static void BakeSkin(GModelAsset asset, Matrix4x4[] locals)
    {
        var palette = GModelPrimitiveFactory.EvaluateSkinPalette(asset.Rig, locals);
        foreach (var mesh in asset.Meshes)
        {
            if (mesh.SkinnedVertices.Length == 0) continue;
            if (mesh.Vertices.Length != mesh.SkinnedVertices.Length) mesh.Vertices = new MeshVertex[mesh.SkinnedVertices.Length];
            mesh.IsSkinned = false;
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                var v = mesh.SkinnedVertices[i]; Vector3 position = Vector3.Zero, normal = Vector3.Zero; float total = 0;
                for (int j = 0; j < 4; j++)
                {
                    float weight = v.JointWeights[j]; int joint = (int)v.JointIndices[j];
                    if (weight <= 0 || joint < 0 || joint >= palette.Length) continue;
                    position += Vector3.Transform(v.Position, palette[joint]) * weight;
                    normal += Vector3.TransformNormal(v.Normal, palette[joint]) * weight; total += weight;
                }
                mesh.Vertices[i] = new() { Position = total > .00001f ? position / total : v.Position,
                    Normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : v.Normal, UV = v.UV, Color = v.Color };
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _requests.Writer.TryComplete();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
