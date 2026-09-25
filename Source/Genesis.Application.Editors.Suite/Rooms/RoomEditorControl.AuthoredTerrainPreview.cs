using System.Numerics;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.Application.Editors.Suite.Rooms;

public sealed partial class RoomEditorControl
{
    private sealed record TerrainPreviewIdentity(RoomNode Node, string Asset, string Albedo, float UvScale);

    private sealed class AuthoredTerrainPreviewState : IDisposable
    {
        public TerrainPreviewIdentity[] Identity = [];
        public RoomTerrainSubsystem? World;
        public MeshDrawCall[] Draws = [];
        public bool Failed;

        public void Dispose() => World?.Dispose();
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
            int required = state.World.GetMeshDrawCapacity(renderer);
            if (state.Draws.Length < required)
                state.Draws = new MeshDrawCall[required];
            int count = 0;
            Vector3 eye = Matrix4x4.Invert(_viewport.ViewMatrix, out Matrix4x4 cameraWorld)
                ? cameraWorld.Translation : _viewport.Camera.Eye;
            state.World.SubmitPreviewMeshes(eye,
                _viewport.ViewMatrix * _viewport.ProjectionMatrix, state.Draws, ref count, renderer);
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
