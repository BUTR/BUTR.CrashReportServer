using BUTR.CrashReport.Server.Contexts;
using BUTR.CrashReport.Server.Models.Database;
using BUTR.CrashReport.Server.v13;

using Microsoft.EntityFrameworkCore;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Services;

/// <summary>
/// One-off maintenance: re-renders legacy (&lt;13) reports whose stored HTML is the original verbatim upload. For each
/// report it archives the original HTML into <see cref="OldHtmlEntity"/>, replaces the live <see cref="HtmlEntity"/>
/// with the trusted renderer's output (so any injected markup is stripped), and stores the parsed model as JSON.
/// HTML is still streamed straight from <see cref="HtmlEntity"/> on read; this only sanitizes its contents.
/// <para>
/// Reports whose HTML can't be parsed are left untouched (no archive, no re-render) so nothing is lost and a later
/// run can retry them.
/// </para>
/// <para>Idempotent: a report is considered migrated once it has an <see cref="OldHtmlEntity"/> row, so re-runs skip
/// it. Pass a <c>limit</c> to bound the work per call and drain the backlog in chunks: call until <c>Migrated</c> is
/// 0 (a residual <c>Remaining</c> then is reports whose HTML cannot be parsed and need manual attention).</para>
/// </summary>
public sealed class LegacyHtmlToJsonMigrator
{
    public sealed record MigrationResult(int Processed, int Migrated, int Skipped, int Remaining);

    private const int BatchSize = 100;

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly GZipCompressor _gZipCompressor;
    private readonly ZstdCompressionService _zstd;

    public LegacyHtmlToJsonMigrator(IDbContextFactory<AppDbContext> dbContextFactory, GZipCompressor gZipCompressor, ZstdCompressionService zstd)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
        _gZipCompressor = gZipCompressor ?? throw new ArgumentNullException(nameof(gZipCompressor));
        _zstd = zstd ?? throw new ArgumentNullException(nameof(zstd));
    }

    private static IQueryable<Guid> Candidates(AppDbContext dbContext) => dbContext.HtmlEntities
        .Where(x => x.Report!.Version < 13)
        .Where(x => !dbContext.OldHtmlEntities.Any(o => o.CrashReportId == x.CrashReportId))
        .Select(x => x.CrashReportId);

    /// <param name="limit">Maximum number of reports to re-render this call; null processes the whole backlog at once.</param>
    public async Task<MigrationResult> MigrateAsync(int? limit, CancellationToken ct)
    {
        List<Guid> ids;
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
        {
            var query = Candidates(dbContext).OrderBy(x => x);
            ids = limit is { } l
                ? await query.Take(Math.Max(0, l)).ToListAsync(ct)
                : await query.ToListAsync(ct);
        }

        var processed = 0;
        var migrated = 0;
        var skipped = 0;

        for (var offset = 0; offset < ids.Count; offset += BatchSize)
        {
            var slice = ids.GetRange(offset, Math.Min(BatchSize, ids.Count - offset));

            await using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct);
            var htmlEntities = await dbContext.HtmlEntities.Include(x => x.Report).Where(x => slice.Contains(x.CrashReportId)).ToListAsync(ct);
            foreach (var htmlEntity in htmlEntities)
            {
                processed++;

                var originalCompressed = htmlEntity.DataCompressed;

                byte[] originalBytes;
                {
                    await using var decompressed = htmlEntity.DictId is { } dictId
                        ? await _zstd.DecompressAsync(originalCompressed, dictId, ct)
                        : await _gZipCompressor.DecompressAsync(originalCompressed, ct);
                    originalBytes = decompressed.ToArray();
                }
                var original = Encoding.UTF8.GetString(originalBytes);

                if (HtmlHandlerV13.Rebuild(original) is not { } rebuilt)
                {
                    skipped++;
                    continue;
                }

                var (archiveCompressed, archiveDictId) = await _zstd.CompressAsync(originalBytes, htmlEntity.Report!.Tenant, CompressionDictionaryKind.Html, htmlEntity.Report!.Version, ct);
                await dbContext.OldHtmlEntities.AddAsync(new OldHtmlEntity { CrashReportId = htmlEntity.CrashReportId, DataCompressed = archiveCompressed, DictId = archiveDictId, }, ct);

                var (htmlCompressed, htmlDictId) = await _zstd.CompressAsync(Encoding.UTF8.GetBytes(rebuilt.Html), htmlEntity.Report!.Tenant, CompressionDictionaryKind.Html, htmlEntity.Report!.Version, ct);
                htmlEntity.DataCompressed = htmlCompressed;
                htmlEntity.DictId = htmlDictId;

                var (jsonCompressed, jsonDictId) = await _zstd.CompressAsync(Encoding.UTF8.GetBytes(rebuilt.Json), htmlEntity.Report!.Tenant, CompressionDictionaryKind.Json, htmlEntity.Report!.Version, ct);
                await dbContext.JsonEntities.AddAsync(new JsonEntity { CrashReportId = htmlEntity.CrashReportId, DataCompressed = jsonCompressed, DictId = jsonDictId, }, ct);
                migrated++;
            }
            await dbContext.SaveChangesAsync(ct);
        }

        int remaining;
        await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(ct))
            remaining = await Candidates(dbContext).CountAsync(ct);

        return new MigrationResult(processed, migrated, skipped, remaining);
    }
}