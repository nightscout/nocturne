using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Health.Oura;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Models.Authorization;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

[ApiController, Authorize, DenyDemoSubject, RequireScope(Scope.TenantSettings)]
[Route("api/v4/oura")]
public class OuraController(OuraImportService imports) : ControllerBase
{
    /// <summary>Under the web app's own body limit (<c>BODY_SIZE_LIMIT</c> in its Dockerfile), which the upload passes through.</summary>
    private const long MaxUploadBytes = 40 * 1024 * 1024;

    private const string HistoryClampedCode = "history_clamped";

    /// <summary>
    /// Import the data export archive from membership.ouraring.com/data-export. Importing an
    /// export again, or a newer one, updates the records already imported.
    /// </summary>
    [HttpPost("import"), RemoteCommand]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(OuraImportResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OuraImportResult>> ImportOuraExport([FromForm] IFormFile file, CancellationToken ct)
    {
        // An export is history, and a re-import finds the records it should update by reading them:
        // a caller limited to the last 24 hours would miss them and collide on insert.
        if (HttpContext.IsCallerHistoryClamped())
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: HistoryClampedCode);

        await using var upload = file.OpenReadStream();
        try
        {
            return Ok(await imports.ImportAsync(upload, ct));
        }
        catch (OuraExportException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Code);
        }
    }
}
