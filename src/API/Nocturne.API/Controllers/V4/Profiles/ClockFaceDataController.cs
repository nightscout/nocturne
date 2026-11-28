using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// What a clock face shows beyond the latest reading: insulin and carbs on board, the basal rate, a
/// forecast, time in range, trackers and charts. Read a public face's values and chart by its id, or
/// preview the values of a layout before it is saved.
/// </summary>
/// <remarks>
/// The clock id is the capability, as for <see cref="ClockFacesController.GetGlucose"/>: a caller
/// holding a valid id for the host's tenant gets what the face's layout shows, read as its owner
/// may read it now, clamped to the last 24 hours when the owner is (<see cref="ClockFaceOwnerScopes"/>,
/// <see cref="ClockFaceReadScopes"/>). A part the layout has no element for is never read.
/// </remarks>
[ApiController]
[ErrorEnvelope]
[Tags("Profiles")]
[Route("api/v4/clockfaces")]
public class ClockFaceDataController(
    IClockFaceService clockFaces,
    ClockFaceDataService data,
    ITenantMemberService members,
    ITenantAccessor tenant) : ControllerBase
{
    /// <summary>The values a face's elements show (public, no authentication required).</summary>
    [HttpGet("{id:guid}/data")]
    [AllowAnonymous]
    [RemoteQuery]
    [EnableRateLimiting(ServiceRegistrationExtensions.ClockFaceDataRateLimitPolicy)]
    [ProducesResponseType(typeof(ClockFaceData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClockFaceData>> GetClockFaceData(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        if (await clockFaces.GetByIdAsync(id, ct) is not { } face)
            return NotFound();

        var held = await HttpContext.ReadAsOwnerAsync(members, tenant.TenantId, face, ct);
        return Ok(await data.ReadAsync(face.Config, held, ct));
    }

    /// <summary>
    /// The chart window a face's chart elements draw (public, no authentication required). Its chart
    /// is null when the face has no chart its owner may share.
    /// </summary>
    [HttpGet("{id:guid}/chart")]
    [AllowAnonymous]
    [RemoteQuery]
    [EnableRateLimiting(ServiceRegistrationExtensions.ClockFaceDataRateLimitPolicy)]
    [ProducesResponseType(typeof(ClockFaceChart), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ClockFaceChart>> GetClockFaceChart(Guid id)
    {
        var ct = HttpContext.RequestAborted;
        if (await clockFaces.GetByIdAsync(id, ct) is not { } face)
            return NotFound();

        var held = await HttpContext.ReadAsOwnerAsync(members, tenant.TenantId, face, ct);
        return Ok(await data.ReadChartAsync(face.Config, held, ct));
    }

    /// <summary>
    /// The values a layout not yet saved would show, read as the caller may read them, so the editor
    /// shows what the face will.
    /// </summary>
    [HttpPost("preview/data")]
    [Authorize]
    [RequireScope(Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ClockFaceData), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ClockFaceData>> PreviewClockFaceData([FromBody] ClockFaceConfig config)
    {
        var held = HttpContext.GetGrantedScopes();
        if (!TrackerReadAccess.Allows(HttpContext))
        {
            // A reader without an account reads no trackers even holding the scope; see TrackerReadAccess.
            held = held.Where(s => !Scope.Satisfies(s, Scope.AlertsRead)).ToHashSet();
        }

        return Ok(await data.ReadAsync(config, held, HttpContext.RequestAborted));
    }

    /// <summary>
    /// For each part of a clock face that takes a scope, the scope the caller lacks to add it, so
    /// the editor shows what it cannot add before a save refuses it. Bounded as the save is, by what
    /// the caller may give anyone else.
    /// </summary>
    [HttpGet("parts")]
    [Authorize]
    [RequireScope(Scope.SharingManage)]
    [RemoteQuery]
    [ProducesResponseType(typeof(ClockPartAccessDto[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<ClockPartAccessDto[]> GetClockFaceParts() =>
        Ok(ClockFaceReadScopes.Access(HttpContext.ClockFaceGivableScopes()).ToArray());
}
