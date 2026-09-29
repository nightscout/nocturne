using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.SetupHub;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The setup hub's Devices item: device guesses with their evidence, and the writes that record
/// what the owner confirms. Owner-only, like the rest of the setup hub.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/setup-hub/devices")]
[Produces("application/json")]
[Authorize]
public class SetupDevicesController(DeviceSetupService deviceSetup) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(DeviceSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DeviceSetup>> GetDeviceSetup(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        return Ok(await deviceSetup.GetAsync(HttpContext.GetSubjectIdString()!, ct));
    }

    /// <summary>Records a guessed device, or the catalogue model it was swapped for.</summary>
    [DenyDemoSubject]
    [HttpPost("confirm")]
    [RequireScope(Scope.DevicesReadWrite)]
    [RemoteCommand(Invalidates = ["GetDeviceSetup"])]
    [ProducesResponseType(typeof(DeviceSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<DeviceSetup>> ConfirmSetupDevice([FromBody] ConfirmSetupDeviceRequest request, CancellationToken ct) =>
        Write(() => deviceSetup.ConfirmDeviceAsync(request.CatalogId, request.AidAlgorithm, ct), ct);

    [DenyDemoSubject]
    [HttpPost("insulins")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetDeviceSetup"])]
    [ProducesResponseType(typeof(DeviceSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<DeviceSetup>> AddSetupInsulin([FromBody] AddSetupInsulinRequest request, CancellationToken ct) =>
        Write(() => deviceSetup.AddInsulinAsync(request.FormulationId, ct), ct);

    /// <summary>Answers the insulin question with "none", or takes that answer back.</summary>
    [DenyDemoSubject]
    [HttpPut("no-insulin")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetDeviceSetup"])]
    [ProducesResponseType(typeof(DeviceSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<DeviceSetup>> SetTakesNoInsulin([FromBody] SetTakesNoInsulinRequest request, CancellationToken ct) =>
        Write(() => deviceSetup.SetTakesNoInsulinAsync(request.TakesNoInsulin, ct), ct);

    /// <summary>Creates an offered tracker with the catalogue's rated wear time.</summary>
    [DenyDemoSubject]
    [HttpPost("trackers")]
    [RequireScope(Scope.AlertsReadWrite)]
    [RemoteCommand(Invalidates = ["GetDeviceSetup"])]
    [ProducesResponseType(typeof(DeviceSetup), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<DeviceSetup>> AddSetupTracker([FromBody] AddSetupTrackerRequest request, CancellationToken ct) =>
        Write(() => deviceSetup.AddTrackerAsync(request.Kind, request.Name, HttpContext.GetSubjectIdString()!, ct), ct);

    private async Task<ActionResult<DeviceSetup>> Write(Func<Task> write, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        try
        {
            await write();
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
        catch (InvalidOperationException)
        {
            return Problem(detail: "Remove the insulins on record before answering none.", statusCode: 409, title: "Conflict");
        }

        return Ok(await deviceSetup.GetAsync(HttpContext.GetSubjectIdString()!, ct));
    }
}

public record ConfirmSetupDeviceRequest
{
    [Required]
    [MaxLength(64)]
    public string CatalogId { get; init; } = "";

    /// <summary>The AID algorithm to record on a pump; ignored for a CGM.</summary>
    [EnumDataType(typeof(AidAlgorithm))]
    public AidAlgorithm? AidAlgorithm { get; init; }
}

public record AddSetupInsulinRequest
{
    [Required]
    [MaxLength(64)]
    public string FormulationId { get; init; } = "";
}

/// <remarks>Nominal: see <see cref="SetPatientRelationshipRequest"/>.</remarks>
public record SetTakesNoInsulinRequest
{
    [JsonRequired]
    public bool TakesNoInsulin { get; init; }
}

public record AddSetupTrackerRequest
{
    [JsonRequired]
    [EnumDataType(typeof(TrackerOfferKind))]
    public TrackerOfferKind Kind { get; init; }

    /// <summary>The tracker's name, in the owner's language.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; init; } = "";
}
