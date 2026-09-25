using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Channels;
using Genesis.Application.Editors.Suite.Assets;
using Genesis.Rendering.Core;
using Genesis.Runtime.Modeling;
using Genesis.Shared.Interfaces;
using Newtonsoft.Json.Linq;

namespace Genesis.Application.Editors.Suite.Rooms;

/// <summary>Bounded software previews keep model loading and rasterization out of UI paint.</summary>
internal sealed class RoomObjectThumbnailRenderer : IDisposable
{
    public const int Size = 96;
    internal sealed record Request(string ObjectPath, string ProjectRoot, IntPtr Handle, int Generation);
    internal sealed record Result(string ObjectPath, int Generation, byte[]? Pixels, string? Error);
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(12) { SingleReader = true });
    private readonly ConcurrentQueue<Result> _results = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private int _generation;
    private int _disposed;

    public RoomObjectThumbnailRenderer() => _worker = Task.Run(Run);
    public void Reset(int generation) => Volatile.Write(ref _generation, generation);
    public bool Submit(Request request) => _requests.Writer.TryWrite(request);
    public bool TryTake(out Result? result) => _results.TryDequeue(out result);

    private async Task Run()
    {
        IRenderController? renderer = null;
        var models = new RuntimeModelRenderSystem();
        try
        {
            await foreach (Request request in _requests.Reader.ReadAllAsync(_stop.Token))
            {
                if (request.Generation != Volatile.Read(ref _generation)) continue;
                try
                {
                    JObject document = Genesis.Runtime.Scene.ObjectDefinitionResolver.PreviewPrefab(
                        Genesis.Runtime.Scene.ObjectDefinitionResolver.Load(request.ProjectRoot, request.ObjectPath));
                    JObject prefab = document["prefab"] as JObject ?? document;
                    string? model = (string?)prefab["model"] ?? (string?)document["model"];
                    model ??= (prefab["components"] as JArray)?.OfType<JObject>()
                        .Where(component => ((string?)component["type"] ?? (string?)component["id"] ?? "").Contains("Model", StringComparison.OrdinalIgnoreCase))
                        .Select(component => (string?)(component["props"] as JObject)?["ModelAsset"] ?? (string?)(component["props"] as JObject)?["Model"])
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    if (string.IsNullOrWhiteSpace(model)) { _results.Enqueue(new(request.ObjectPath, request.Generation, null, null)); continue; }
                    string path = StudioModelResourceLoader.Resolve(request.ProjectRoot, model);
                    if (path.Length == 0) throw new FileNotFoundException("Model preview source is missing: " + model);
                    GModelAsset asset = StudioModelResourceLoader.LoadReadOnly(path);
                    if (asset.ImportRequired) throw new InvalidDataException(asset.ImportMessage);
                    // Software does not execute GPU skinning. Bake the saved neutral pose in
                    // this private in-memory copy so imported bind corrections match its bounds.
                    ModelFramePreviews.BakeSkin(asset, asset.Rig.Bones.Select(bone => bone.BindLocal).ToArray());
                    if (_stop.IsCancellationRequested || request.Generation != Volatile.Read(ref _generation)) continue;
                    renderer ??= RenderControllerFactory.Create(RenderBackendOption.Software);
                    renderer.Initialize(request.Handle, Size, Size);
                    models.InvalidateAssets(renderer);
                    Vector3 min = asset.Bounds.Min - asset.Pivot.Position, max = asset.Bounds.Max - asset.Pivot.Position;
                    Vector3 center = (min + max) * .5f;
                    float span = MathF.Max(.01f, Vector3.Distance(min, max)) * 1.08f;
                    float distance = span * 3;
                    Vector3 eye = center + Vector3.Normalize(new Vector3(.7f, .35f, 1)) * distance;
                    Mesh3DState state = EditorSceneLighting.Create(false, false, distance * 3);
                    state.BackgroundColor = new(.075f, .085f, .11f);
                    state.FogEnabled = state.FogScreenSpace = false;
                    state.AmbientColor = new(.65f); state.AmbientGroundColor = new(.4f);
                    renderer.BeginFrame(); renderer.Clear(.075f, .085f, .11f); renderer.SetMesh3DState(state);
                    renderer.SetCamera3D(Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY),
                        Matrix4x4.CreateOrthographic(span, span, MathF.Max(.00001f, distance / 1000), distance * 3));
                    models.DrawAsset(asset, request.ProjectRoot, Matrix4x4.Identity,
                        new RuntimeModelAnimationState(string.Empty, 0, 30, false), renderer);
                    renderer.EndFrame();
                    if (!renderer.TryReadFramePixels(out _, out _, out byte[] pixels)) throw new InvalidOperationException("Model thumbnail readback failed.");
                    if (request.Generation == Volatile.Read(ref _generation)) _results.Enqueue(new(request.ObjectPath, request.Generation, pixels, null));
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception exception) { _results.Enqueue(new(request.ObjectPath, request.Generation, null, exception.Message)); }
                finally
                {
                    // Material texture caches belong to the controller. Release full-resolution
                    // source textures after each thumbnail rather than accumulating the library.
                    if (renderer is not null) { models.InvalidateAssets(renderer); renderer.Dispose(); renderer = null; }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { if (renderer is not null) { models.InvalidateAssets(renderer); renderer.Dispose(); } }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _requests.Writer.TryComplete();
        _ = _worker.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
