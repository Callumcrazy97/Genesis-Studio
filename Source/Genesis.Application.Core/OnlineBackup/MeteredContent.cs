using System.Net;
using System.Net.Http.Headers;

namespace Genesis.Application.Core.Backup;

/// <summary>
/// An in-memory request body that reports bytes to a meter as they are written to the socket,
/// which is what makes the upload speed readout real rather than estimated.
/// </summary>
internal sealed class MeteredContent : HttpContent
{
    private const int ChunkSize = 64 * 1024;

    private readonly ReadOnlyMemory<byte> _payload;
    private readonly IByteMeter? _meter;

    public MeteredContent(ReadOnlyMemory<byte> payload, string mediaType, IByteMeter? meter)
    {
        _payload = payload;
        _meter = meter;
        Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < _payload.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(ChunkSize, _payload.Length - offset);
            await stream.WriteAsync(_payload.Slice(offset, take), cancellationToken).ConfigureAwait(false);
            offset += take;
            _meter?.AddWireBytes(take);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _payload.Length;
        return true;
    }
}
