using BUTR.CrashReport.Server.Contexts;
using BUTR.CrashReport.Server.Models;
using BUTR.CrashReport.Server.Models.API;
using BUTR.CrashReport.Server.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Controllers;

[ApiController]
[Route("/report")]
[Authorize]
[HttpsProtocol(Protocol = SslProtocols.Tls13)]
public class ReportManagementController : ControllerBase
{
    public sealed record GetNewCrashReportsBody(DateTime DateTime);

    private readonly AppDbContext _dbContext;
    private readonly CrashReportService _reports;
    private readonly LegacyHtmlToJsonMigrator _legacyHtmlToJsonMigrator;

    public ReportManagementController(AppDbContext dbContext, CrashReportService reports, LegacyHtmlToJsonMigrator legacyHtmlToJsonMigrator)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _legacyHtmlToJsonMigrator = legacyHtmlToJsonMigrator ?? throw new ArgumentNullException(nameof(legacyHtmlToJsonMigrator));
    }

    [HttpDelete("Delete/{tenant:int}/{filename}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<IActionResult> Delete(byte tenant, string filename)
    {
        await _reports.DeleteAsync(tenant, filename, CancellationToken.None);

        return Ok();
    }

    [HttpGet("{tenant:int}/GetAllFilenames")]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public IAsyncEnumerable<string> GetAllFilenames(byte tenant) =>
        _dbContext.ReportEntities
            .AsNoTracking()
            .Where(x => x.Tenant == tenant)
            .Select(x => x.FileId)
            .AsAsyncEnumerable();

    [HttpPost("{tenant:int}/GetMetadata")]
    [ProducesResponseType(typeof(FileMetadata[]), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public ActionResult<IEnumerable<FileMetadata>> GetFilenameDates(byte tenant, ICollection<string> filenames, CancellationToken ct)
    {
        var filenamesWithExtension = filenames.Select(Path.GetFileNameWithoutExtension).ToImmutableArray();

        return Ok(_dbContext.ReportEntities
            .Where(x => x.Tenant == tenant)
            .Where(x => filenamesWithExtension.Contains(x.FileId))
            .Select(x => new FileMetadata
            {
                File = x.FileId,
                Id = x.CrashReportId,
                Version = x.Version,
                Date = x.Created,
            }));
    }

    [HttpPost("{tenant:int}/GetNewCrashReports")]
    [ProducesResponseType(typeof(FileMetadata[]), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public ActionResult<IEnumerable<FileMetadata>> GetNewCrashReportsDates(byte tenant, [FromBody] GetNewCrashReportsBody body, CancellationToken ct)
    {
        var diff = DateTime.UtcNow - body.DateTime;
        if (diff.Ticks < 0 || diff > TimeSpan.FromDays(30))
            return BadRequest();

        return Ok(_dbContext.ReportEntities
            .Where(x => x.Tenant == tenant)
            .Where(x => x.Created > body.DateTime)
            .Select(x => new FileMetadata
            {
                File = x.FileId,
                Id = x.CrashReportId,
                Version = x.Version,
                Date = x.Created,
            }));
    }

    [HttpPost("MigrateLegacyHtmlToJson")]
    [ProducesResponseType(typeof(LegacyHtmlToJsonMigrator.MigrationResult), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<ActionResult<LegacyHtmlToJsonMigrator.MigrationResult>> MigrateLegacyHtmlToJson([FromQuery] int? limit, CancellationToken ct) =>
        Ok(await _legacyHtmlToJsonMigrator.MigrateAsync(limit, ct));
}