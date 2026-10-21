using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>Reads the <see cref="ProfileActivation"/>s: each one stored, and the one in force.</summary>
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

    public ProfileActivationsController(IProfileActivationRepository activations, IProfileActivationResolver resolver)
    {
        _activations = activations;
        _resolver = resolver;
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

    private static long ToMills(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();
}
