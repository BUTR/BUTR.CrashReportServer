using BUTR.CrashReport.Server.Models;
using BUTR.CrashReport.Server.Models.Database;
using BUTR.CrashReport.Server.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IO;

using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace BUTR.CrashReport.Server.Controllers;

[ApiController]
[Route("/compression")]
[Authorize]
[HttpsProtocol(Protocol = SslProtocols.Tls13)]
public class CompressionController : ControllerBase
{
    private const long MaxDictionaryBytes = 32 * 1024 * 1024;

    private readonly DictionaryService _dictionaries;
    private readonly RecyclableMemoryStreamManager _streamManager;

    public CompressionController(DictionaryService dictionaries, RecyclableMemoryStreamManager streamManager)
    {
        _dictionaries = dictionaries ?? throw new ArgumentNullException(nameof(dictionaries));
        _streamManager = streamManager ?? throw new ArgumentNullException(nameof(streamManager));
    }

    [HttpGet("dictionaries")]
    [ProducesResponseType(typeof(IReadOnlyList<DictionaryService.DictionaryInfo>), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<ActionResult<IReadOnlyList<DictionaryService.DictionaryInfo>>> List([FromQuery] byte? tenant, CancellationToken ct) =>
        Ok(await _dictionaries.ListAsync(tenant, ct));

    [HttpPost("dictionaries")]
    [Consumes("application/octet-stream")]
    [ProducesResponseType(typeof(DictionaryService.SetResult), StatusCodes.Status200OK, "application/json")]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<ActionResult<DictionaryService.SetResult>> SetDictionary([FromQuery] byte tenant, [FromQuery] CompressionDictionaryKind kind, [FromQuery] byte version, CancellationToken ct)
    {
        if (await ReadBodyAsync(ct) is not { } bytes)
            return Problem($"Dictionary too large (max {MaxDictionaryBytes} bytes).", statusCode: StatusCodes.Status400BadRequest);

        if (bytes.Length == 0)
            return Problem("Request body is empty - POST the dictionary bytes as application/octet-stream.", statusCode: StatusCodes.Status400BadRequest);

        try
        {
            var result = await _dictionaries.SetActiveAsync(tenant, kind, version, bytes, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is { } len)
        {
            if (len > MaxDictionaryBytes) return null;
            var buffer = new byte[len];
            await Request.Body.ReadExactlyAsync(buffer, ct);
            return buffer;
        }

        await using var ms = _streamManager.GetStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (ms.Length + read > MaxDictionaryBytes) return null;
            ms.Write(chunk, 0, read);
        }
        return ms.ToArray();
    }

    [HttpPost("dictionaries/{id}/activate")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<IActionResult> Activate(short id, CancellationToken ct)
    {
        try
        {
            await _dictionaries.ActivateAsync(id, ct);
            return Ok();
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status404NotFound);
        }
    }

    [HttpDelete("dictionaries/{id}")]
    [ProducesResponseType(typeof(void), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(void), StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType(typeof(TLSError), StatusCodes.Status400BadRequest, "application/json")]
    public async Task<IActionResult> Delete(short id, CancellationToken ct) =>
        await _dictionaries.DeleteAsync(id, ct)
            ? Ok()
            : Problem("Dictionary not found, active (activate another first), or still referenced by stored reports (in-use dictionaries are kept).", statusCode: StatusCodes.Status409Conflict);
}