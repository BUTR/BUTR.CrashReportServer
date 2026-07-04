using BUTR.CrashReport.Server.Contexts;
using BUTR.CrashReport.Server.Models.Database;
using BUTR.CrashReport.Server.Options;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Microsoft.IO;

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ZstdSharp;

namespace BUTR.CrashReport.Server.Services;

public sealed class ZstdCompressionService
{
    public const byte LegacyMaxVersion = 14;

    public static byte GroupFor(byte version) => version <= LegacyMaxVersion ? LegacyMaxVersion : version;

    private const int NoDict = -1;

    private const int MaxPooledCodecs = 2;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOptionsMonitor<CompressionOptions> _options;
    private readonly RecyclableMemoryStreamManager _streamManager;

    private readonly ConcurrentDictionary<short, Task<byte[]>> _dictBytes = new();
    private readonly ConcurrentDictionary<(byte Tenant, byte Kind, byte Group), (short DictId, long ExpiresTick)> _activeDict = new();
    private readonly ConcurrentDictionary<(int DictKey, int Level), ObjectPool<Compressor>> _compressorPools = new();
    private readonly ConcurrentDictionary<int, ObjectPool<Decompressor>> _decompressorPools = new();

    public ZstdCompressionService(IDbContextFactory<AppDbContext> dbContextFactory, IOptionsMonitor<CompressionOptions> options, RecyclableMemoryStreamManager streamManager)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
    }

    public async Task<(byte[] Compressed, short DictId)> CompressAsync(ReadOnlyMemory<byte> data, byte tenant, CompressionDictionaryKind kind, byte version, CancellationToken ct)
    {
        var dictId = await GetActiveDictIdAsync(tenant, kind, GroupFor(version), ct).ConfigureAwait(false);
        var dictBytes = await GetDictBytesAsync(dictId, ct).ConfigureAwait(false);

        var pool = _compressorPools.GetOrAdd((dictId, _options.CurrentValue.Level),
            static (key, dict) => new DefaultObjectPool<Compressor>(new CompressorPolicy(key.Level, dict), MaxPooledCodecs), dictBytes);

        var compressor = pool.Get();
        try
        {
            var scratch = ArrayPool<byte>.Shared.Rent(Compressor.GetCompressBound(data.Length));
            try
            {
                var written = compressor.Wrap(data.Span, scratch);
                return (scratch.AsSpan(0, written).ToArray(), dictId);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
        finally
        {
            pool.Return(compressor);
        }
    }

    /// <summary>
    /// Decompresses into a pooled <see cref="RecyclableMemoryStream"/> positioned at 0. The caller owns the
    /// stream and must dispose it to return the buffers to the pool.
    /// </summary>
    public async Task<MemoryStream> DecompressAsync(byte[] compressed, short? dictId, CancellationToken ct)
    {
        var dictBytes = dictId is { } id ? await GetDictBytesAsync(id, ct).ConfigureAwait(false) : null;

        var pool = _decompressorPools.GetOrAdd(dictId ?? NoDict,
            static (_, dict) => new DefaultObjectPool<Decompressor>(new DecompressorPolicy(dict), MaxPooledCodecs), dictBytes);

        var decompressor = pool.Get();
        try
        {
            // Frames written by CompressAsync always embed the content size, so decompress straight into a
            // right-sized contiguous pooled buffer instead of letting the library allocate a fresh array.
            var size = Decompressor.GetDecompressedSize(compressed);
            if (size is 0 or > int.MaxValue)
            {
                // Unknown content size (foreign frame) or genuinely empty - let the library size it.
                var data = decompressor.Unwrap(compressed);
                var fallback = _streamManager.GetStream(nameof(ZstdCompressionService), data.Length);
                fallback.Write(data);
                fallback.Position = 0;
                return fallback;
            }

            var stream = _streamManager.GetStream(nameof(ZstdCompressionService), (long) size, asContiguousBuffer: true);
            try
            {
                stream.SetLength((long) size);
                var written = decompressor.Unwrap(compressed, stream.GetBuffer().AsSpan(0, (int) size));
                stream.SetLength(written);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
        finally
        {
            pool.Return(decompressor);
        }
    }

    private async Task<short> GetActiveDictIdAsync(byte tenant, CompressionDictionaryKind kind, byte group, CancellationToken ct)
    {
        var key = (tenant, (byte) kind, group);
        if (_activeDict.TryGetValue(key, out var cached) && cached.ExpiresTick > Environment.TickCount64)
            return cached.DictId;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var dictId =
            await dbContext.CompressionDictionaries
                .Where(x => x.Tenant == tenant && x.Kind == kind && x.Version == group && x.IsActive)
                .Select(x => (short?) x.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? await dbContext.CompressionDictionaries
                .Where(x => x.Tenant == 1 && x.Kind == kind && x.Version == LegacyMaxVersion && x.IsActive)
                .Select(x => (short?) x.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        if (dictId is not { } id)
            throw new InvalidOperationException(
                $"No active {kind} dictionary for tenant {tenant} version-group {group}, and no tenant 1 v{LegacyMaxVersion} fallback dictionary. Seed or upload a dictionary before compressing.");

        var ttlMs = Math.Max(1, _options.CurrentValue.ActiveDictionaryCacheSeconds) * 1000L;
        _activeDict[key] = (id, Environment.TickCount64 + ttlMs);
        return id;
    }

    private Task<byte[]> GetDictBytesAsync(short dictId, CancellationToken _ct) => _dictBytes.GetOrAdd(dictId, LoadDictBytesAsync);

    private async Task<byte[]> LoadDictBytesAsync(short dictId)
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var bytes = await dbContext.CompressionDictionaries
                .Where(x => x.Id == dictId)
                .Select(x => x.Bytes)
                .FirstOrDefaultAsync().ConfigureAwait(false);
            return bytes ?? throw new InvalidOperationException($"Compression dictionary {dictId} not found - its rows cannot be decompressed.");
        }
        catch
        {
            _dictBytes.TryRemove(dictId, out _);
            throw;
        }
    }

    private sealed class CompressorPolicy(int level, byte[]? dict) : IPooledObjectPolicy<Compressor>
    {
        public Compressor Create()
        {
            var compressor = new Compressor(level);
            if (dict is not null) compressor.LoadDictionary(dict);
            return compressor;
        }

        public bool Return(Compressor obj) => true;
    }

    private sealed class DecompressorPolicy(byte[]? dict) : IPooledObjectPolicy<Decompressor>
    {
        public Decompressor Create()
        {
            var decompressor = new Decompressor();
            if (dict is not null) decompressor.LoadDictionary(dict);
            return decompressor;
        }

        public bool Return(Decompressor obj) => true;
    }
}
