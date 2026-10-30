using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Mcp.Reports;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Custom reports: report specs, as an assistant composes them over MCP, run and saved in the web
/// app. Reads need <see cref="Scope.ReportsRead"/>; a run reads each panel's dataset under the
/// dataset's own scope, as <c>render_report</c> does.
/// </summary>
/// <remarks>
/// A spec is a definition, not a patient record, and the taxonomy has no <c>reports.readwrite</c>.
/// Every write needs the member's own sign-in (<see cref="RequireInteractiveSessionAttribute"/>), so
/// an app's token, a direct grant or an api-secret that may read reports cannot change them. Saving
/// is refused to a history-clamped member, who is limited to a day of the record; renaming and
/// deleting are for the report's creator, or a member who edits every report (an owner or a holder
/// of <see cref="Scope.TenantSettings"/>, <see cref="MemberScopeResolver.IsExemptFromHistoryClamp"/>).
/// Share and guest links are refused everything (<see cref="DenyNoAccountViewerAttribute"/>); the
/// table is hidden from shares too. Nothing here is cached: a saved spec is the tenant's, and a run
/// is per caller.
/// </remarks>
[ApiController]
[Tags("CustomReports")]
[Route("api/v4/reports/custom")]
[RequireScope(Scope.ReportsRead)]
[DenyNoAccountViewer]
[Produces("application/json")]
[ClientPropertyName("customReports")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class CustomReportsController(IReportSpecStore store, ICustomReportRunner runner) : ControllerBase
{
    /// <summary>The largest request body: a spec at its limit, a name and the JSON around them.</summary>
    private const long MaxBodyBytes = ReportSpecLink.MaxSpecBytes + 4 * 1024;

    /// <summary>The tenant's saved custom reports, by name.</summary>
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(IReadOnlyList<CustomReportSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CustomReportSummary>>> GetCustomReports(CancellationToken cancellationToken) =>
        Ok(await store.ListAsync(Caller(), cancellationToken));

    /// <summary>
    /// One saved custom report, its spec rewritten to the current version and normalised; or, when
    /// it is no longer valid, as stored with the errors.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(CustomReport), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomReport>> GetCustomReport(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, Caller(), cancellationToken) is { } report ? Ok(report) : NotFound();

    /// <summary>
    /// Runs a spec for the caller. An invalid spec is not an error status: the response lists what
    /// to change, so a page can show it.
    /// </summary>
    [HttpPost("run")]
    [RemoteQuery]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(CustomReportRunResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CustomReportRunResponse>> RunCustomReport(
        [FromBody] CustomReportRunRequest request, CancellationToken cancellationToken) =>
        Ok(await runner.RunAsync(request, cancellationToken));

    /// <summary>
    /// Saves a spec under a name unique among the tenant's custom reports. Refused to a
    /// history-clamped member, and once the tenant keeps <see cref="ReportSpecStore.MaxReports"/>.
    /// </summary>
    [HttpPost]
    [RequireInteractiveSession]
    [RemoteCommand(Invalidates = ["GetCustomReports"])]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(CustomReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CustomReport>> CreateCustomReport(
        [FromBody] CustomReportCreateRequest request, CancellationToken cancellationToken)
    {
        if (HttpContext.IsCallerHistoryClamped())
            return Forbid();
        return Result(await store.CreateAsync(request.Name, request.Spec, Caller(), cancellationToken));
    }

    /// <summary>Renames a custom report, replaces its spec, or both: its creator, or a member who edits every report.</summary>
    [HttpPut("{id:guid}")]
    [RequireInteractiveSession]
    [RemoteCommand(Invalidates = ["GetCustomReports", "GetCustomReport"])]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(CustomReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CustomReport>> UpdateCustomReport(
        Guid id, [FromBody] CustomReportUpdateRequest request, CancellationToken cancellationToken) =>
        Result(await store.UpdateAsync(id, request.Name, request.Spec, Caller(), cancellationToken));

    /// <summary>Deletes a custom report: its creator, or a member who edits every report.</summary>
    [HttpDelete("{id:guid}")]
    [RequireInteractiveSession]
    [RemoteCommand(Invalidates = ["GetCustomReports"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteCustomReport(Guid id, CancellationToken cancellationToken) =>
        await store.DeleteAsync(id, Caller(), cancellationToken) switch
        {
            CustomReportWriteStatus.Saved => NoContent(),
            CustomReportWriteStatus.Forbidden => Forbid(),
            _ => NotFound(),
        };

    /// <summary>The caller's subject, and whether it edits every report: an owner or a holder of tenant.settings.</summary>
    private CustomReportEditor Caller() => new(
        HttpContext.GetAuthContext()?.SubjectId,
        MemberScopeResolver.IsExemptFromHistoryClamp(HttpContext.GetGrantedScopes()));

    private ActionResult<CustomReport> Result(CustomReportWrite write) => write.Status switch
    {
        CustomReportWriteStatus.Saved => Ok(write.Report),
        CustomReportWriteStatus.NotFound => NotFound(),
        CustomReportWriteStatus.Forbidden => Forbid(),
        CustomReportWriteStatus.Invalid => Refused(write.Errors, StatusCodes.Status400BadRequest),
        CustomReportWriteStatus.NameTaken => Refused(write.Errors, StatusCodes.Status409Conflict),
        _ => throw new ArgumentOutOfRangeException(nameof(write), write.Status, null),
    };

    private static ObjectResult Refused(IReadOnlyList<CustomReportSpecError> errors, int status) =>
        new(new ValidationProblemDetails(errors.GroupBy(e => e.Path).ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
        {
            Status = status,
            Title = status == StatusCodes.Status409Conflict ? "Conflict" : "Bad Request",
        })
        { StatusCode = status };
}
