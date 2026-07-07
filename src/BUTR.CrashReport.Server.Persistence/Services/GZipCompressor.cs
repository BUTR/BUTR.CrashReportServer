using Microsoft.IO;

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Services;

public sealed class GZipCompressor
{
    private readonly RecyclableMemoryStreamManager _streamManager;

    public GZipCompressor(RecyclableMemoryStreamManager streamManager)
    {
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
    }

    public async Task<MemoryStream> CompressAsync(byte[] data, CancellationToken ct)
    {
        await using var decompressedStream = _streamManager.GetStream(data);
        return await CompressAsync(decompressedStream, ct);
    }
    public async Task<MemoryStream> CompressAsync(Stream decompressedStream, CancellationToken ct)
    {
        var compressedStream = _streamManager.GetStream();
        await using var zipStream = new GZipStream(compressedStream, CompressionMode.Compress, true);
        await decompressedStream.CopyToAsync(zipStream, ct);
        return compressedStream;
    }

    public async Task<MemoryStream> DecompressAsync(byte[] data, CancellationToken ct)
    {
        await using var compressedStream = _streamManager.GetStream(data);
        return await DecompressAsync(compressedStream, ct);
    }

    /// <summary>
    /// Opens a read-only, forward-only stream that gunzips <paramref name="compressedSource"/> incrementally as it is
    /// read, without materializing the whole decompressed body. Takes ownership of <paramref name="compressedSource"/>:
    /// disposing the returned stream disposes it. Pair with a streaming source (e.g. a Postgres bytea read) so the
    /// compressed blob is never fully materialized either. Legacy read-path counterpart to
    /// <see cref="ZstdCompressionService.OpenDecompressionStreamAsync"/>.
    /// </summary>
    public Stream OpenDecompressionStream(Stream compressedSource) =>
        new GZipStream(compressedSource, CompressionMode.Decompress, leaveOpen: false);
    public async Task<MemoryStream> DecompressAsync(Stream compressedStream, CancellationToken ct)
    {
        var decompressedStream = _streamManager.GetStream();
        await using var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress, true);
        await zipStream.CopyToAsync(decompressedStream, ct);
        decompressedStream.Seek(0, SeekOrigin.Begin);
        return decompressedStream;
    }
}