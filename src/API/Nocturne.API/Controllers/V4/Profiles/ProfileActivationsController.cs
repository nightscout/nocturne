using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Models.Requests.V4;
using Nocturne.API.Services.ProfileActivations;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Reads the <see cref="ProfileActivation"/>s, each one stored and the one in force, and writes the
/// profile switches a person records in Nocturne (<see cref="NativeProfileSwitches"/>).
/// </summary>
/// <remarks>
/// Never response-cached: an activation an uploader posts must read back on the next poll, and the
/// one in force changes with it. See <see cref="ProfileController.GetProfileSummary"/>.
/// </remarks>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/profile/activations")]
[Authorize]
[Produces("application/json")]
public class ProfileActivationsController : ControllerBase
{
    private const int MaxActivations = 1000;
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);

    private readonly IProfileActivationRepository _activations;
    private readonly IProfileActivationResolver _resolver;
    private readonly NativeProfileSwitches _native;

    public ProfileActivationsController(
        IProfileActivationRepository activations,
        IProfileActivationResolver resolver,
        NativeProfileSwitches native)
    {
        _activations = activations;
        _resolver = resolver;
        _native = native;
    }

    /// <summary>
    /// The activations that ran during the window, requested and effective alike, oldest first.
    /// </summary>
    /// <param name="from">Window start; 30 days before <paramref name="to"/> when omitted.</param>
    /// <param name="to">Window end; now when omitted.</param>
    /// <param name="applied">
    /// Only the activations in force at some moment of the window, as the profile resolvers select them,
    /// each whole: what another instance replicates to read the same profile at every moment.
    /// </param>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(IReadOnlyList<ProfileActivationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<ProfileActivationDto>>> GetActivations(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] bool applied = false,
        CancellationToken ct = default)
    {
        var end = to?.ToUniversalTime() ?? DateTime.UtcNow;
        var start = from?.ToUniversalTime() ?? end - DefaultWindow;
        if (start > end)
            return BadRequest("from must not be after to");

        if (applied)
        {
            var segments = await _resolver.GetRangeAsync(ToMills(start), ToMills(end), ct);
            return Ok(segments
                .Select(s => s.Active.Activation)
                .DistinctBy(a => a.Id)
                .OrderBy(a => a.Mills)
                .Select(ProfileActivationDto.From)
                .ToList());
        }

        var activations = await _activations.GetOverlappingAsync(start, end, MaxActivations, ct);
        return Ok(activations.Select(ProfileActivationDto.From).ToList());
    }

    /// <summary>The activation in force at <paramref name="at"/>, or no content when the profile store's default is.</summary>
    /// <param name="at">The moment; now when omitted.</param>
    [HttpGet("active")]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status200OK)]
    // Typed so the generated client reads no content as null instead of throwing.
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status204NoContent)]
    public async Task<ActionResult<ProfileActivationDto>> GetActiveActivation(
        [FromQuery] DateTime? at = null,
        CancellationToken ct = default)
    {
        var moment = ToMills((at ?? DateTime.UtcNow).ToUniversalTime());
        return await _resolver.GetAtAsync(moment, ct) is { } active
            ? Ok(ProfileActivationDto.From(active.Activation))
            : NoContent();
    }

    /// <summary>
    /// Records a switch to a stored profile starting now. It is recorded only: it is not served on the
    /// v1 or v3 treatments feed, so no uploader reads it back and no pump runs it.
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActiveActivation"])]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileActivationDto>> CreateActivation(
        [FromBody] CreateProfileActivationRequest request,
        CancellationToken ct = default)
    {
        var (outcome, activation) = await _native.CreateAsync(request, ct);
        return outcome switch
        {
            NativeProfileSwitchOutcome.ProfileNotFound => Problem(
                statusCode: StatusCodes.Status404NotFound, title: "Profile not found",
                detail: $"No stored profile is named '{request.BaseProfileName}'."),
            NativeProfileSwitchOutcome.PatientDeviceNotFound => Problem(
                statusCode: StatusCodes.Status400BadRequest, title: "Device not found",
                detail: $"No patient device has id {request.PatientDeviceId}."),
            _ when activation is not null => StatusCode(StatusCodes.Status201Created, ProfileActivationDto.From(activation)),
            _ => Problem(statusCode: StatusCodes.Status409Conflict, title: "Not recorded"),
        };
    }

    /// <summary>Ends a person's switch now. An uploader's or a connector's is its source's to end.</summary>
    [HttpPost("{id:guid}/end")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActiveActivation"])]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileActivationDto>> EndActivation(Guid id, CancellationToken ct = default)
    {
        var (outcome, activation) = await _native.EndAsync(id, ct);
        return outcome switch
        {
            NativeProfileSwitchOutcome.Written when activation is not null => Ok(ProfileActivationDto.From(activation)),
            NativeProfileSwitchOutcome.AlreadyEnded => Problem(
                statusCode: StatusCodes.Status409Conflict, title: "Already ended",
                detail: "The activation has already ended."),
            _ => Refused(outcome),
        };
    }

    /// <summary>Deletes a person's switch. An uploader's or a connector's is its source's to delete.</summary>
    [HttpDelete("{id:guid}")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActiveActivation"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteActivation(Guid id, CancellationToken ct = default) =>
        await _native.DeleteAsync(id, ct) is var outcome && outcome == NativeProfileSwitchOutcome.Written
            ? NoContent()
            : Refused(outcome);

    private ObjectResult Refused(NativeProfileSwitchOutcome outcome) => outcome == NativeProfileSwitchOutcome.NotOwnedByUser
        ? Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Owned by its source",
            detail: "Only a switch a person recorded can be changed here; an uploader's or a connector's belongs to its source.")
        : Problem(statusCode: StatusCodes.Status404NotFound, title: "Activation not found");

    private static long ToMills(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
}
