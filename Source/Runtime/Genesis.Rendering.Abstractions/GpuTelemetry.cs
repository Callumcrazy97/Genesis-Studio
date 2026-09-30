using System.Threading;

namespace Genesis.Rendering.Abstractions
{
    /// <summary>
    /// Process-wide counters for GPU resource creation and CPU→GPU transfer. Every backend reports
    /// the bytes it actually moves or clears, not the bytes a caller asked for, so a regression that
    /// re-uploads unchanged data every frame shows up as a steady non-zero per-frame delta.
    /// </summary>
    public static class GpuTelemetry
    {
        private static long _uploadBytes;
        private static long _buffersCreated;
        private static long _texturesCreated;
        private static long _renderTargetsCreated;
        private static long _pipelinesCreated;

        public static void Upload(long bytes)
        {
            if (bytes > 0) Interlocked.Add(ref _uploadBytes, bytes);
        }

        public static void BufferCreated(long initialBytes)
        {
            Interlocked.Increment(ref _buffersCreated);
            Upload(initialBytes);
        }

        public static void TextureCreated(long initialBytes)
        {
            Interlocked.Increment(ref _texturesCreated);
            Upload(initialBytes);
        }

        public static void RenderTargetCreated() => Interlocked.Increment(ref _renderTargetsCreated);

        public static void PipelineCreated() => Interlocked.Increment(ref _pipelinesCreated);

        public static GpuTelemetrySnapshot Capture() => new(
            Interlocked.Read(ref _uploadBytes),
            Interlocked.Read(ref _buffersCreated),
            Interlocked.Read(ref _texturesCreated),
            Interlocked.Read(ref _renderTargetsCreated),
            Interlocked.Read(ref _pipelinesCreated));
    }

    public readonly record struct GpuTelemetrySnapshot(
        long UploadBytes,
        long BuffersCreated,
        long TexturesCreated,
        long RenderTargetsCreated,
        long PipelinesCreated)
    {
        public GpuTelemetrySnapshot Since(GpuTelemetrySnapshot earlier) => new(
            UploadBytes - earlier.UploadBytes,
            BuffersCreated - earlier.BuffersCreated,
            TexturesCreated - earlier.TexturesCreated,
            RenderTargetsCreated - earlier.RenderTargetsCreated,
            PipelinesCreated - earlier.PipelinesCreated);
    }
}
