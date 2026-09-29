using System.Numerics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Runtime.ECS.Components;
using Genesis.Runtime.Rendering;
using Genesis.Runtime.Assets;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private sealed record TerrainPreviewIdentity(RoomNode Node, string Asset, string Albedo, float UvScale);

    private sealed class AuthoredTerrainPreviewState : IDisposable
    {
        public TerrainPreviewIdentity[] Identity = [];
        public RoomTerrainSubsystem? World;
        public readonly Genesis.Runtime.ECS.World Parts = new();
        public readonly ObjectDrawAssetRegistry.PreviewScope Assets = new();
        public RoomBuildResult? Placements;
        public MeshDrawCall[] Draws = [];
        public bool Failed;

        public void Dispose()
        {
            World?.Dispose();
            using (Assets.Activate()) Parts.Dispose();
            Assets.Clear();
        }
    }

    private readonly Dictionary<IRenderController, AuthoredTerrainPreviewState> _authoredTerrainPreviews = [];
    private MeshDrawCall[] _lastAuthoredTerrainDraws = [];
    private string _terrainPreviewError = string.Empty;

    /// <summary>Actual F5-compatible draw submissions from the last Room terrain preview.</summary>
    public IReadOnlyList<MeshDrawCall> AuthoredTerrainDraws => _lastAuthoredTerrainDraws;
    public string TerrainPreviewError => _terrainPreviewError;

    private void DrawAuthoredTerrainPreview(IRenderController renderer)
    {
        RoomNode[] nodes = EnumerateVisibleTerrainNodes().ToArray();
        if (nodes.Length == 0) { _lastAuthoredTerrainDraws = []; return; }
        TerrainPreviewIdentity[] identity = nodes.Select(node => new TerrainPreviewIdentity(
            node, node.Terrain!.Asset, node.Terrain.Albedo, node.Terrain.UvScale)).ToArray();
        if (!_authoredTerrainPreviews.TryGetValue(renderer, out AuthoredTerrainPreviewState? state)
            || !state.Identity.SequenceEqual(identity))
        {
            state?.Dispose();
            state = new AuthoredTerrainPreviewState { Identity = identity };
            _authoredTerrainPreviews[renderer] = state;
            try
            {
                // Keep the authored nodes, including their live transform objects. Selection,
                // dragging and undo therefore use the same placement matrix as runtime draws.
                RoomAsset previewRoom = _room;
                state.World = new RoomTerrainSubsystem(ProjectRoot, previewRoom, null!);
                using (state.Assets.Activate())
                    state.Placements = new RoomSceneBuilder(ProjectRoot).BuildTerrainParts(state.Parts, previewRoom);
                _terrainPreviewError = string.Empty;
            }
            catch (Exception exception) when (IsTerrainPreviewFailure(exception))
            {
                state.Failed = true;
                ReportTerrainPreviewFailure(exception);
            }
        }

        if (state.Failed || state.World is null)
        {
            _lastAuthoredTerrainDraws = [];
            return;
        }

        try
        {
            using IDisposable assetScope = state.Assets.Activate();
            if (state.Placements is not null)
                RoomSceneBuilder.UpdateTerrainPartPreviewTransforms(state.Parts, state.Placements);
            state.Parts.Query<ModelAnimatorComponent>((entity, ref animator) =>
                animator.TimeSeconds = animator.Playing ? _roomTime : 0);
            state.Parts.Query<SpriteComponent>((entity, ref sprite) =>
            {
                if (!ObjectDrawAssetRegistry.TryGet(entity, out ObjectDrawAssetEntry assets) || assets.TerrainTextureMode == null) return;
                int available = Math.Max(1, SpriteAssetLoader.GetFrameCount(assets.Image));
                int count = assets.TerrainTextureFrameCount <= 0 ? available : Math.Min(available, assets.TerrainTextureFrameCount);
                sprite.ImageIndex = (int)(_roomTime * assets.TerrainTextureFps) % count;
            });
            int required = state.World.GetMeshDrawCapacity(renderer) + Math.Max(1024, state.Parts.LivingEntityCount * 32);
            if (state.Draws.Length < required)
                state.Draws = new MeshDrawCall[required];
            int count = 0;
            Vector3 eye = Matrix4x4.Invert(_viewport.ViewMatrix, out Matrix4x4 cameraWorld)
                ? cameraWorld.Translation : _viewport.Camera.Eye;
            Matrix4x4 viewProjection = _viewport.ViewMatrix * _viewport.ProjectionMatrix;
            Vector3 forward = Vector3.Normalize(_viewport.Camera.Target - eye);
            while (true)
            {
                count = 0;
                state.World.SubmitPreviewMeshes(eye, viewProjection, state.Draws, ref count, renderer);
                ObjectDrawPass.SubmitMeshes3D(state.Parts, ProjectRoot, state.Draws, ref count, renderer, eye, forward, viewProjection);
                if (count < state.Draws.Length) break;
                Array.Resize(ref state.Draws, checked(state.Draws.Length * 2));
            }
            for (int index = 0; index < count; index++)
                renderer.DrawMesh(state.Draws[index]);
            _lastAuthoredTerrainDraws = state.Draws.AsSpan(0, count).ToArray();
        }
        catch (Exception exception) when (IsTerrainPreviewFailure(exception))
        {
            state.Failed = true;
            _lastAuthoredTerrainDraws = [];
            ReportTerrainPreviewFailure(exception);
        }
    }

    private static bool IsTerrainPreviewFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
        or ArgumentException or System.Text.Json.JsonException or Newtonsoft.Json.JsonException;

    private void ReportTerrainPreviewFailure(Exception exception)
    {
        _terrainPreviewError = exception.Message;
        UpdateStatus("Terrain preview could not load: " + exception.Message);
    }

    private void ReleaseAuthoredTerrainPreviews()
    {
        foreach (AuthoredTerrainPreviewState state in _authoredTerrainPreviews.Values)
            state.Dispose();
        foreach (IRenderController renderer in _authoredTerrainPreviews.Keys)
            ObjectDrawPass.InvalidateAssets(renderer);
        _authoredTerrainPreviews.Clear();
        _lastAuthoredTerrainDraws = [];
        _terrainPreviewError = string.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _roomUiTimer?.Dispose();
        if (disposing)
            ReleaseAuthoredTerrainPreviews();
        base.Dispose(disposing);
    }
}
