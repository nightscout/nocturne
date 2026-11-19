using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Health.Oura;
using Nocturne.Connectors.Oura.Export;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Services;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Health;

/// <summary>
/// Data from an Oura ring. Import the data export archive of an Oura account. Importing an export
/// again, or a newer one, updates the records already imported.
/// </summary>
[ApiController, Authorize, DenyDemoSubject, RequireScope(Scope.TenantSettings)]
[Tags("Connectors")]
[Route("api/v4/oura")]
public class OuraController(OuraImportJobService imports, ITenantAccessor tenant) : ControllerBase
{
    /// <summary>Under the web app's own body limit (<c>BODY_SIZE_LIMIT</c> in its Dockerfile), which the upload passes through.</summary>
    private const long MaxUploadBytes = 40 * 1024 * 1024;

    private const string HistoryClampedCode = "history_clamped";
    private const string ImportRunningCode = "import_running";
    private const string DeleteRunningCode = "delete_running";

    /// <summary>
    /// Start importing the data export archive from membership.ouraring.com/data-export. The
    /// archive is checked before the import starts; the import then runs in the background, and
    /// <see cref="GetOuraImport"/> reports its progress. Importing an export again, or a newer one,
    /// updates the records already imported.
    /// </summary>
    [HttpPost("import"), RemoteCommand(Invalidates = [nameof(GetOuraImport)])]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(OuraImportStatus), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OuraImportStatus>> ImportOuraExport([FromForm] IFormFile file, CancellationToken ct)
    {
        // An export is history, and a re-import finds the records it should update by reading them:
        // a caller limited to the last 24 hours would miss them and collide on insert.
        if (HttpContext.IsCallerHistoryClamped())
            return Problem(statusCode: StatusCodes.Status403Forbidden, detail: HistoryClampedCode);

        try
        {
            imports.EnsureCanStart(tenant.TenantId);
        }
        catch (OuraImportBlockedException ex)
        {
            return Blocked(ex);
        }

        var export = new byte[file.Length];
        await using (var upload = file.OpenReadStream())
            await upload.ReadExactlyAsync(export, ct);

        try
        {
            using var archive = OuraExportArchive.Open(new MemoryStream(export, writable: false));
            archive.Validate();
        }
        catch (OuraExportException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, detail: ex.Code);
        }

        try
        {
            var status = imports.Start(export, TenantReader.Of(HttpContext.RequestServices));
            return AcceptedAtAction(nameof(GetOuraImport), routeValues: null, value: status);
        }
        catch (OuraImportBlockedException ex)
        {
            return Blocked(ex);
        }
    }

    private ObjectResult Blocked(OuraImportBlockedException blocked) =>
        Problem(statusCode: StatusCodes.Status409Conflict, detail: blocked.ByDelete ? DeleteRunningCode : ImportRunningCode);

    /// <summary>The latest import of an Oura export since the server started: its state, and the records it stored.</summary>
    [HttpGet("import"), RemoteQuery]
    [ProducesResponseType(typeof(OuraImportStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OuraImportStatus> GetOuraImport() =>
        imports.GetLatest(tenant.TenantId) is { } status ? Ok(status) : NotFound();
}
