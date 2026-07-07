using BUTR.CrashReport.Server.Contexts;

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Services;

public sealed class CrashReportService
{
    private readonly AppDbContext _dbContext;
    private readonly NpgsqlDataSource _dataSource;
    private readonly ReportBlobSchema _blobSchema;
    private readonly GZipCompressor _gZipCompressor;
    private readonly ZstdCompressionService _zstd;
    private readonly IOutputCacheStore _outputCacheStore;

    public CrashReportService(AppDbContext dbContext, NpgsqlDataSource dataSource, ReportBlobSchema blobSchema, GZipCompressor gZipCompressor, ZstdCompressionService zstd, IOutputCacheStore outputCacheStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _blobSchema = blobSchema ?? throw new ArgumentNullException(nameof(blobSchema));
        _gZipCompressor = gZipCompressor ?? throw new ArgumentNullException(nameof(gZipCompressor));
        _zstd = zstd ?? throw new ArgumentNullException(nameof(zstd));
        _outputCacheStore = outputCacheStore ?? throw new ArgumentNullException(nameof(outputCacheStore));
    }

    public Task<Stream?> GetHtmlAsync(byte tenant, string filename, CancellationToken ct) =>
        OpenReportBodyAsync(_blobSchema.Html, isHtml: true, tenant, filename, ct);

    public Task<Stream?> GetJsonAsync(byte tenant, string filename, CancellationToken ct) =>
        OpenReportBodyAsync(_blobSchema.Json, isHtml: false, tenant, filename, ct);

    private async Task<Stream?> OpenReportBodyAsync(ReportBlobSchema.BlobTable table, bool isHtml, byte tenant, string filename, CancellationToken ct)
    {
        if (await ResolveCrashReportIdAsync(tenant, filename, ct) is not { } crashReportId)
            return null;

        var connection = await _dataSource.OpenConnectionAsync(ct);
        var handedOff = false;
        DbCommand? command = null;
        DbDataReader? reader = null;
        Stream? column = null;
        try
        {
            command = connection.CreateCommand();
            command.CommandText = $"SELECT {table.DictIdColumn}, {table.DataColumn} FROM {table.Table} WHERE {table.KeyColumn} = @id";
            command.Parameters.Add(new NpgsqlParameter("id", crashReportId));

            reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow | CommandBehavior.SingleResult, ct);
            if (!await reader.ReadAsync(ct))
                return null;

            var dictId = await reader.IsDBNullAsync(0, ct) ? (short?) null : reader.GetFieldValue<short>(0);
            column = reader.GetStream(1);

            // html predates zstd: dict_id NULL means a legacy gzip blob; everything else (all json, current html) is
            // zstd. The codec stream takes ownership of `column`; the returned stream owns the reader/command/connection.
            var decompressed = isHtml && dictId is null
                ? _gZipCompressor.OpenDecompressionStream(column)
                : await _zstd.OpenDecompressionStreamAsync(column, dictId, ct);

            var result = new DbReportStream(decompressed, reader, command, connection);
            handedOff = true;
            return result;
        }
        finally
        {
            if (!handedOff)
            {
                if (column is not null) await column.DisposeAsync();
                if (reader is not null) await reader.DisposeAsync();
                if (command is not null) await command.DisposeAsync();
                await connection.DisposeAsync();
            }
        }
    }

    public async Task<byte[]?> GetTokenHashAsync(byte tenant, string filename, CancellationToken ct)
    {
        if (await ResolveCrashReportIdAsync(tenant, filename, ct) is not { } crashReportId)
            return null;

        return await _dbContext.ReportEntities
            .Where(x => x.CrashReportId == crashReportId)
            .Select(x => x.DeleteTokenHash)
            .FirstOrDefaultAsync(ct);
    }

    public async Task DeleteAsync(byte tenant, string filename, CancellationToken ct)
    {
        if (await ResolveCrashReportIdAsync(tenant, filename, ct) is not { } crashReportId)
            return;

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
        await _dbContext.HtmlEntities.Where(x => x.CrashReportId == crashReportId).ExecuteDeleteAsync(ct);
        await _dbContext.JsonEntities.Where(x => x.CrashReportId == crashReportId).ExecuteDeleteAsync(ct);
        await _dbContext.OldHtmlEntities.Where(x => x.CrashReportId == crashReportId).ExecuteDeleteAsync(ct);
        await _dbContext.IdAliasEntities.Where(x => x.CrashReportId == crashReportId).ExecuteDeleteAsync(ct);
        await _dbContext.ReportEntities.Where(x => x.CrashReportId == crashReportId).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);

        await _outputCacheStore.EvictByTagAsync(ReportOutputCachePolicy.ReportTag(tenant, filename), ct);
    }

    private IQueryable<Guid> ResolveCrashReportIdQuery(byte tenant, string filename) =>
        _dbContext.ReportEntities.Where(x => x.Tenant == tenant && x.FileId == filename).Select(x => x.CrashReportId)
            .Concat(_dbContext.IdAliasEntities.Where(x => x.Tenant == tenant && x.FileId == filename).Select(x => x.CrashReportId));

    private Task<Guid?> ResolveCrashReportIdAsync(byte tenant, string filename, CancellationToken ct) =>
        ResolveCrashReportIdQuery(tenant, filename).Select(x => (Guid?) x).FirstOrDefaultAsync(ct);

    private sealed class DbReportStream(Stream inner, DbDataReader reader, DbCommand command, DbConnection connection) : Stream
    {
        private bool _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();       // disposes the codec and the underlying bytea column stream
                reader.Dispose();
                command.Dispose();
                connection.Dispose();  // returns the connection to the NpgsqlDataSource pool
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await inner.DisposeAsync();
                await reader.DisposeAsync();
                await command.DisposeAsync();
                await connection.DisposeAsync();
            }
            await base.DisposeAsync();
        }
    }
}