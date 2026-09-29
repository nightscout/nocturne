using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The setup hub's Alerts item: starter alert rules, where they send, and a test alert the owner
/// confirms arrived. Owner-only, like the rest of the hub.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/setup-hub/alerts")]
[Produces("application/json")]
[Authorize]
public class SetupAlertsController(AlertSetupService alerts) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertSetupStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AlertSetupStatus>> GetAlertSetup(CancellationToken ct) =>
        Owner() is { } caller ? Ok(await alerts.GetAsync(caller, ct)) : Forbid();

    /// <summary>Creates or updates the starter rules and where they send.</summary>
    [DenyDemoSubject]
    [RequireScope(Scope.AlertsReadWrite)]
    [HttpPut]
    [RemoteCommand(Invalidates = ["GetAlertSetup", "GetSetupHub"])]
    [ProducesResponseType(typeof(AlertSetupStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AlertSetupStatus>> SaveAlertSetup(
        [FromBody] SaveAlertSetupRequest request, CancellationToken ct) =>
        Run(caller => alerts.SaveAsync(caller, request, ct));

    /// <summary>Sends a test alert where the starter rules send.</summary>
    [DenyDemoSubject]
    [RequireScope(Scope.AlertsReadWrite)]
    [HttpPost("test")]
    [RemoteCommand]
    [ProducesResponseType(typeof(AlertSetupTest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AlertSetupTest>> SendSetupTestAlert(CancellationToken ct) =>
        Run(caller => alerts.SendTestAsync(caller, ct));

    [HttpGet("test/{instanceId:guid}")]
    [RemoteQuery]
    [ProducesResponseType(typeof(AlertSetupTest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<AlertSetupTest>> GetSetupTestAlert(Guid instanceId, CancellationToken ct) =>
        Run(_ => alerts.GetTestAsync(instanceId, ct));

    /// <summary>Records that the test alert arrived, which is what finishes the item.</summary>
    [DenyDemoSubject]
    [RequireScope(Scope.AlertsReadWrite)]
    [HttpPost("test/{instanceId:guid}/received")]
    [RemoteCommand(Invalidates = ["GetAlertSetup", "GetSetupHub"])]
    [ProducesResponseType(typeof(AlertSetupStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AlertSetupStatus>> ConfirmSetupTestAlertReceived(Guid instanceId, CancellationToken ct) =>
        Run(caller => alerts.ConfirmReceivedAsync(caller, instanceId, ct));

    /// <summary>Also sends urgent low alerts to another member, or stops.</summary>
    [DenyDemoSubject]
    [RequireScope(Scope.AlertsReadWrite)]
    [HttpPut("urgent-low-recipients/{subjectId:guid}")]
    [RemoteCommand(Invalidates = ["GetAlertSetup"])]
    [ProducesResponseType(typeof(AlertSetupStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<AlertSetupStatus>> SetUrgentLowRecipient(
        Guid subjectId, [FromBody] SetUrgentLowRecipientRequest request, CancellationToken ct) =>
        Run(caller => alerts.SetUrgentLowRecipientAsync(caller, subjectId, request.Alerted, ct));

    private Guid? Owner() =>
        HttpContext.HasScope(Scope.FullAccess) ? HttpContext.GetSubjectId() : null;

    private async Task<ActionResult<T>> Run<T>(Func<Guid, Task<T>> action)
    {
        if (Owner() is not { } caller)
            return Forbid();

        try
        {
            return Ok(await action(caller));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException e)
        {
            return Problem(detail: e.Message, statusCode: 400, title: "Bad Request");
        }
        catch (InvalidOperationException e)
        {
            return Problem(detail: e.Message, statusCode: 409, title: "Conflict");
        }
    }
}
