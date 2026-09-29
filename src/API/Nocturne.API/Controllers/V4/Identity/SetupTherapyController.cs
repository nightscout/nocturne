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
/// The setup hub's therapy settings item. Owner-only, like the rest of the hub; the owner's full
/// access also carries the therapy write scope its writes declare.
/// </summary>
[ApiController]
[Tags("Identity")]
[Route("api/v4/setup-hub/therapy")]
[Produces("application/json")]
[Authorize]
public class SetupTherapyController(ITherapySetupService therapy) : ControllerBase, IWriteScopedController
{
    public string WriteScope => Scope.TherapyReadWrite;

    [HttpGet]
    [RemoteQuery]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(TherapyReview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<TherapyReview>> GetTherapyReview(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        return Ok(await therapy.GetReviewAsync(ct));
    }

    [DenyDemoSubject]
    [RequireDeclaredWriteScope]
    [HttpPost("confirm")]
    [RemoteCommand(Invalidates = ["GetTherapyReview"])]
    [ProducesResponseType(typeof(TherapyReview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TherapyReview>> ConfirmTherapySettings(CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        try
        {
            return Ok(await therapy.ConfirmAsync(ct));
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409, title: "Conflict");
        }
    }

    [DenyDemoSubject]
    [RequireDeclaredWriteScope]
    [HttpPost("entry")]
    [RemoteCommand(Invalidates = ["GetTherapyReview"])]
    [ProducesResponseType(typeof(TherapyReview), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TherapyReview>> EnterTherapySettings(
        [FromBody] EnterTherapySettingsRequest request, CancellationToken ct)
    {
        if (!HttpContext.HasScope(Scope.FullAccess))
            return Forbid();

        try
        {
            return Ok(await therapy.EnterAsync(
                request.GlucoseUnits, request.Basal, request.CarbRatio, request.Sensitivity, request.TargetRange, ct));
        }
        catch (ArgumentException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 409, title: "Conflict");
        }
    }
}

/// <summary>A hand-entered profile, with sensitivity and targets in <see cref="GlucoseUnits"/>.</summary>
public record EnterTherapySettingsRequest
{
    /// <summary>"mg/dl" or "mmol": the owner's display units the form was filled in.</summary>
    [JsonRequired]
    [RegularExpression("^(mg/dl|mmol)$")]
    public string GlucoseUnits { get; init; } = "";

    public List<TherapyEntryInput> Basal { get; init; } = [];

    public List<TherapyEntryInput> CarbRatio { get; init; } = [];

    public List<TherapyEntryInput> Sensitivity { get; init; } = [];

    public List<TargetEntryInput> TargetRange { get; init; } = [];
}
