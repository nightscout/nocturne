using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.SetupHub;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.SetupHub;

namespace Nocturne.API.Controllers.V4.Identity;

/// <summary>
/// The owner's setup hub: the items left to set up after onboarding, and the dashboard strip that
/// offers them. Owner-only, like the onboarding answers it follows on from.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/setup-hub")]
[Produces("application/json")]
[Authorize]
public class SetupHubController(ISetupHubService setupHub) : ControllerBase
{
    [HttpGet]
    [RemoteQuery]
    [ProducesResponseType(typeof(SetupHubStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetupHubStatus>> GetSetupHub(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        return Ok(await setupHub.GetAsync(ct));
    }

    /// <summary>
    /// Sets a listed item aside as not for this tenant, or reopens one. An item becomes done only
    /// by working, so done cannot be set here and a done item cannot be changed.
    /// </summary>
    [DenyDemoSubject]
    [HttpPut("items/{key}")]
    [RequireScope(Scope.FullAccess)]
    [RemoteCommand(Invalidates = ["GetSetupHub"])]
    [ProducesResponseType(typeof(SetupHubStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SetupHubStatus>> SetSetupHubItemState(
        [FromRoute] SetupHubItemKey key, [FromBody] SetSetupHubItemStateRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        if (request.State == SetupHubItemState.Done)
            return Problem(detail: "An item is done when it works; it cannot be marked done.", statusCode: 400, title: "Bad Request");

        try
        {
            return Ok(await setupHub.SetStateAsync(key, request.State, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException)
        {
            return Problem(detail: "A done item stays done.", statusCode: 409, title: "Conflict");
        }
    }

    [DenyDemoSubject]
    [HttpPost("strip/dismiss")]
    [RequireScope(Scope.FullAccess)]
    [RemoteCommand(Invalidates = ["GetSetupHub"])]
    [ProducesResponseType(typeof(SetupHubStatus), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SetupHubStatus>> DismissSetupStrip(
        [FromBody] DismissSetupStripRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        return Ok(await setupHub.DismissStripAsync(request.Revision, ct));
    }
}

/// <remarks>Nominal: see <see cref="SetPatientRelationshipRequest"/>.</remarks>
public record SetSetupHubItemStateRequest
{
    [JsonRequired]
    [EnumDataType(typeof(SetupHubItemState))]
    public SetupHubItemState State { get; init; }
}

public record DismissSetupStripRequest
{
    /// <summary>The <see cref="SetupHubStatus.Revision"/> the strip was showing.</summary>
    [JsonRequired]
    [MaxLength(64)]
    public string Revision { get; init; } = "";
}
