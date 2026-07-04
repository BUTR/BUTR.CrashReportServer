using BUTR.CrashReport.Server.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.IO;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Controllers;

[ApiController]
[Route("/report")]
public class ReportController : ControllerBase
{
    private readonly CrashReportService _reports;
    private readonly RecyclableMemoryStreamManager _streamManager;

    public ReportController(CrashReportService reports, RecyclableMemoryStreamManager streamManager)
    {
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ValidateFileName(string? fileName) => fileName?.Length is 6 or 8 or 10 && fileName.All(Base32Generator.IsValidChar);

    private StatusCodeResult? ValidateRequest(string filename)
    {
        if (string.IsNullOrEmpty(filename))
            return StatusCode((int) HttpStatusCode.BadRequest);

        filename = Path.GetFileNameWithoutExtension(filename);

        if (!ValidateFileName(filename))
            return StatusCode((int) HttpStatusCode.BadRequest);

        return null;
    }

    private async Task<IActionResult> GetHtml(byte tenant, string filename, CancellationToken ct)
    {
        if (ValidateRequest(filename) is { } errorResponse)
            return errorResponse;

        if (await _reports.GetHtmlAsync(tenant, filename, ct) is not { } html)
            return StatusCode(StatusCodes.Status404NotFound);

        return TranscodedResult(html, "text/html; charset=utf-8");
    }

    private async Task<IActionResult> GetJson(byte tenant, string filename, CancellationToken ct)
    {
        if (ValidateRequest(filename) is { } errorResponse)
            return errorResponse;

        if (await _reports.GetJsonAsync(tenant, filename, ct) is not { } json)
            return StatusCode(StatusCodes.Status404NotFound);

        return TranscodedResult(json, "application/json; charset=utf-8");
    }

    private FileStreamResult TranscodedResult(MemoryStream body, string contentType)
    {
        var acceptEncoding = Request.GetTypedHeaders().AcceptEncoding;
        bool Accepts(string encoding) => acceptEncoding.Any(x => x.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase));

        if (Accepts("br"))
        {
            Response.Headers.ContentEncoding = "br";
            return File(Transcode(body, output => new BrotliStream(output, _brotliOptions, leaveOpen: true)), contentType);
        }
        if (Accepts("gzip"))
        {
            Response.Headers.ContentEncoding = "gzip";
            return File(Transcode(body, output => new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)), contentType);
        }
        return File(body, contentType);
    }

    private static readonly BrotliCompressionOptions _brotliOptions = new() { Quality = 5 };

    private MemoryStream Transcode(MemoryStream body, Func<Stream, Stream> createEncoder)
    {
        using (body)
        {
            var output = _streamManager.GetStream();
            try
            {
                using (var encoder = createEncoder(output))
                    body.CopyTo(encoder);
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{filename}.html")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "text/html")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportHtml(string filename, CancellationToken ct) => GetHtml(1, filename, ct);

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{tenant:int}/{filename}.html")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "text/html")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportHtml(byte tenant, string filename, CancellationToken ct) => GetHtml(tenant, filename, ct);

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{filename}.json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportJson(string filename, CancellationToken ct) => GetJson(1, filename, ct);

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{tenant:int}/{filename}.json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportJson(byte tenant, string filename, CancellationToken ct) => GetJson(tenant, filename, ct);

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{filename}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportBasedOnAccept(string filename, [FromQuery(Name = DeleteTokenGenerator.QueryName)] string? delete, CancellationToken ct) =>
        ReportBasedOnAccept(1, filename, delete, ct);

    [AllowAnonymous]
    [OutputCache(PolicyName = Startup.ReportsCachePolicyName)]
    [HttpGet("{tenant:int}/{filename}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> ReportBasedOnAccept(byte tenant, string filename, [FromQuery(Name = DeleteTokenGenerator.QueryName)] string? delete, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(delete))
            return DeleteConfirmation(tenant, filename, delete, ct);

        return Request.Headers.Accept.FirstOrDefault(x => x is "text/html" or "application/json") switch
        {
            "text/html" => GetHtml(tenant, filename, ct),
            "application/json" => GetJson(tenant, filename, ct),
            _ => GetHtml(tenant, filename, ct),
        };
    }

    [AllowAnonymous]
    [HttpDelete("{filename}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public Task<IActionResult> DeleteWithToken(string filename, [FromQuery(Name = DeleteTokenGenerator.QueryName)] string? delete, CancellationToken ct) =>
        DeleteWithToken(1, filename, delete, ct);

    [AllowAnonymous]
    [HttpDelete("{tenant:int}/{filename}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status500InternalServerError, "application/problem+json")]
    public async Task<IActionResult> DeleteWithToken(byte tenant, string filename, [FromQuery(Name = DeleteTokenGenerator.QueryName)] string? delete, CancellationToken ct)
    {
        if (ValidateRequest(filename) is { } errorResponse)
            return errorResponse;
        if (string.IsNullOrEmpty(delete))
            return StatusCode(StatusCodes.Status400BadRequest);

        filename = Path.GetFileNameWithoutExtension(filename);

        if (await _reports.GetTokenHashAsync(tenant, filename, ct) is not { } storedHash)
            return StatusCode(StatusCodes.Status404NotFound);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, DeleteTokenGenerator.ComputeHash(delete)))
            return StatusCode(StatusCodes.Status403Forbidden);

        await _reports.DeleteAsync(tenant, filename, ct);

        return Ok();
    }

    private async Task<IActionResult> DeleteConfirmation(byte tenant, string filename, string token, CancellationToken ct)
    {
        if (ValidateRequest(filename) is { } errorResponse)
            return errorResponse;

        filename = Path.GetFileNameWithoutExtension(filename);

        if (await _reports.GetTokenHashAsync(tenant, filename, ct) is not { } storedHash)
            return StatusCode(StatusCodes.Status404NotFound);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, DeleteTokenGenerator.ComputeHash(token)))
            return StatusCode(StatusCodes.Status403Forbidden);

        return Content(DeleteConfirmationHtml(filename), "text/html; charset=utf-8");
    }

    private static string DeleteConfirmationHtml(string filename) =>
        $$"""
          <!DOCTYPE html>
          <html lang="en">
          <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="robots" content="noindex, nofollow">
              <title>Delete crash report {{filename}}</title>
              <style>
                  body { font-family: system-ui, sans-serif; background: #1e1e1e; color: #ddd; display: flex; min-height: 100vh; margin: 0; align-items: center; justify-content: center; }
                  .card { background: #2a2a2a; padding: 2rem; border-radius: 8px; max-width: 28rem; text-align: center; box-shadow: 0 2px 12px rgba(0,0,0,.4); }
                  h1 { font-size: 1.25rem; margin-top: 0; }
                  code { background: #1e1e1e; padding: .1rem .35rem; border-radius: 4px; }
                  button { font-size: 1rem; padding: .6rem 1.4rem; border: 0; border-radius: 6px; cursor: pointer; background: #c0392b; color: #fff; }
                  button:disabled { opacity: .5; cursor: default; }
                  #status { margin-top: 1rem; min-height: 1.25rem; }
              </style>
          </head>
          <body>
              <div class="card">
                  <h1>Delete crash report <code>{{filename}}</code>?</h1>
                  <p>This permanently removes the crash report. This action cannot be undone.</p>
                  <button id="confirm" type="button">Delete permanently</button>
                  <div id="status"></div>
              </div>
              <script>
                  const btn = document.getElementById('confirm');
                  const status = document.getElementById('status');
                  btn.addEventListener('click', async () => {
                      btn.disabled = true;
                      status.textContent = 'Deleting...';
                      try {
                          const res = await fetch(window.location.href, { method: 'DELETE' });
                          status.textContent = res.ok ? 'Crash report deleted.' : ('Failed to delete (HTTP ' + res.status + ').');
                          if (!res.ok) btn.disabled = false;
                      } catch (e) {
                          status.textContent = 'Failed to delete: ' + e;
                          btn.disabled = false;
                      }
                  });
              </script>
          </body>
          </html>
          """;
}