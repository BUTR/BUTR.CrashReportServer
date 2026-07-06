using BUTR.CrashReport.Server.Contexts;

using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Services;

public sealed class CrashReportService
{
    private readonly AppDbContext _dbContext;
    private readonly GZipCompressor _gZipCompressor;
    private readonly ZstdCompressionService _zstd;
    private readonly IOutputCacheStore _outputCacheStore;

    public CrashReportService(AppDbContext dbContext, GZipCompressor gZipCompressor, ZstdCompressionService zstd, IOutputCacheStore outputCacheStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _gZipCompressor = gZipCompressor ?? throw new ArgumentNullException(nameof(gZipCompressor));
        _zstd = zstd ?? throw new ArgumentNullException(nameof(zstd));
        _outputCacheStore = outputCacheStore ?? throw new ArgumentNullException(nameof(outputCacheStore));
    }

    /// <summary>
    /// Returns the report body as a forward-only stream that decompresses on read; the caller must dispose it
    /// (returning it as a <see cref="Microsoft.AspNetCore.Mvc.FileStreamResult"/> does that). The body is never
    /// fully materialized in memory - it streams through in chunks so a large report can't blow the container limit.
    /// </summary>
    public async Task<Stream?> GetHtmlAsync(byte tenant, string filename, CancellationToken ct)
    {
        if (await _dbContext.HtmlEntities
                .Where(x => ResolveCrashReportIdQuery(tenant, filename).Contains(x.CrashReportId))
                .Select(x => new { x.DataCompressed, x.DictId })
                .FirstOrDefaultAsync(ct) is not { } file)
            return null;

        return file.DictId is { } dictId
            ? await _zstd.OpenDecompressionStreamAsync(file.DataCompressed, dictId, ct)
            : _gZipCompressor.OpenDecompressionStream(file.DataCompressed);
    }

    /// <inheritdoc cref="GetHtmlAsync"/>
    public async Task<Stream?> GetJsonAsync(byte tenant, string filename, CancellationToken ct)
    {
        if (await _dbContext.JsonEntities
                .Where(x => ResolveCrashReportIdQuery(tenant, filename).Contains(x.CrashReportId))
                .Select(x => new { x.DataCompressed, x.DictId })
                .FirstOrDefaultAsync(ct) is not { } file)
            return null;

        return await _zstd.OpenDecompressionStreamAsync(file.DataCompressed, file.DictId, ct);
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
}