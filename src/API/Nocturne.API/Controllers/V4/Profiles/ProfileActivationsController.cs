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
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
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
    /// What applies now, what an AID system reports, and the switch recorded in Nocturne that is running,
    /// with whether it applies and whether its profile has changed since.
    /// </summary>
    [HttpGet("status")]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileActivationStatus), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProfileActivationStatus>> GetActivationStatus(CancellationToken ct = default) =>
        Ok(await _native.GetStatusAsync(ct));

    /// <summary>
    /// Records a switch to a stored profile starting now. It is recorded only: followers, uploaders and
    /// AAPS never read it, and it does not apply while an AID system reports what its pump runs
    /// (<see cref="NativeProfileSwitches"/>).
    /// </summary>
    [HttpPost]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActivationStatus"])]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileActivationDto>> CreateActivation(
        [FromBody] CreateProfileActivationRequest request,
        CancellationToken ct = default) =>
        Recorded(await _native.CreateAsync(request, ct), StatusCodes.Status201Created);

    /// <summary>
    /// Records a running switch again with its profile's current schedules, the same percentage and shift,
    /// and the end it already had.
    /// </summary>
    [HttpPost("{id:guid}/renew")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActivationStatus"])]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileActivationDto>> RenewActivation(Guid id, CancellationToken ct = default) =>
        Recorded(await _native.RenewAsync(id, ct), StatusCodes.Status201Created);

    /// <summary>Ends a switch recorded in Nocturne now. An uploaded or reported one belongs to its source.</summary>
    [HttpPost("{id:guid}/end")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActivationStatus"])]
    [ProducesResponseType(typeof(ProfileActivationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileActivationDto>> EndActivation(Guid id, CancellationToken ct = default) =>
        Recorded(await _native.EndAsync(id, ct), StatusCodes.Status200OK);

    /// <summary>Deletes a switch recorded in Nocturne. An uploaded or reported one belongs to its source.</summary>
    [HttpDelete("{id:guid}")]
    [RequireScope(Scope.TherapyReadWrite)]
    [RemoteCommand(Invalidates = ["GetActivations", "GetActivationStatus"])]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteActivation(Guid id, CancellationToken ct = default)
    {
        var outcome = await _native.DeleteAsync(id, ct);
        if (outcome == NativeProfileSwitchOutcome.Written)
            return NoContent();
        return Refused(outcome);
    }

    private ActionResult<ProfileActivationDto> Recorded(NativeProfileSwitchResult result, int status) =>
        result is { Outcome: NativeProfileSwitchOutcome.Written, Activation: { } activation }
            ? StatusCode(status, ProfileActivationDto.From(activation))
            : Refused(result.Outcome);

    private ObjectResult Refused(NativeProfileSwitchOutcome outcome) => outcome switch
    {
        NativeProfileSwitchOutcome.ProfileNotFound => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Profile not found",
            detail: "The profile store has no profile of that name."),
        NativeProfileSwitchOutcome.AlreadyEnded => Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Already ended",
            detail: "The switch has already ended."),
        NativeProfileSwitchOutcome.Uploaded => Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Uploaded switch",
            detail: "This switch was uploaded as a treatment and belongs to the app that uploaded it. Change it there."),
        NativeProfileSwitchOutcome.OwnedBySource => Problem(
            statusCode: StatusCodes.Status409Conflict, title: "Reported switch",
            detail: "This switch was reported by a connector or uploader and belongs to it. Change it there."),
        _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "Switch not found"),
    };

    private static long ToMills(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
}
