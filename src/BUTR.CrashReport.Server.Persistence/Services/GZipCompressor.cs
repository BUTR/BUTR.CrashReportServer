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
    /// Opens a read-only, forward-only stream that gunzips <paramref name="data"/> incrementally as it is read,
    /// without materializing the whole decompressed body. The caller must dispose the returned stream (which disposes
    /// the underlying compressed source). Legacy read path counterpart to <see cref="ZstdCompressionService"/>'s streaming decompressor.
    /// </summary>
    public Stream OpenDecompressionStream(byte[] data)
    {
        // The compressed bytes are already fully in managed memory (small); wrap without copying and gunzip on read.
        var source = new MemoryStream(data, writable: false);
        return new GZipStream(source, CompressionMode.Decompress, leaveOpen: false);
    }
    public async Task<MemoryStream> DecompressAsync(Stream compressedStream, CancellationToken ct)
    {
        var decompressedStream = _streamManager.GetStream();
        await using var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress, true);
        await zipStream.CopyToAsync(decompressedStream, ct);
        decompressedStream.Seek(0, SeekOrigin.Begin);
        return decompressedStream;
    }
}