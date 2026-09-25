using System.Numerics;

namespace Genesis.Streaming
{
    /// <summary>Per-frame context passed to streaming providers.</summary>
    public readonly struct StreamingContext
    {
        public required Vector3 FocusPosition { get; init; }
        public required Matrix4x4 ViewProjection { get; init; }
        public required Matrix4x4 StreamingViewProjection { get; init; }
        public required float CameraFarPlane { get; init; }
        public required float CameraYaw { get; init; }
        public required float CameraPitch { get; init; }
        public required StreamingSettings Settings { get; init; }
        public required StreamingJobQueue Jobs { get; init; }
        public float DeltaTime { get; init; }
    }

    /// <summary>
    /// Engine hook for load/unload of spatial cells (voxel chunks, future mesh sectors, etc.).
    /// </summary>
    public interface IStreamingProvider
    {
        string Name { get; }
        StreamingStats Stats { get; }
        void Tick(in StreamingContext context);
        void RequestStreamingRefresh();
    }
}
