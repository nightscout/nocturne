using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Profiles;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// The profile over time: its versions and the changes between them, each schedule in real time, and
/// the sensor glucose beside them.
/// </summary>
/// <remarks>Never response-cached, as <see cref="ProfileActivationsController"/> is not.</remarks>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/profile/history")]
[Authorize]
[Produces("application/json")]
public class ProfileHistoryController : ControllerBase
{
    private const int MaxLaneSpanDays = 14;
    private const int MaxTraceBuckets = 2000;
    private const int MaxAroundDays = 30;

    /// <summary>The year 9000, which leaves <see cref="DateTimeOffset"/> room for a window after it.</summary>
    private const long MaxMills = 221_845_392_000_000;

    private readonly IProfileHistoryService _history;
    private readonly ProfileHistoryGlucoseReader _glucose;
    private readonly TimeProvider _time;

    public ProfileHistoryController(
        IProfileHistoryService history, ProfileHistoryGlucoseReader glucose, TimeProvider time)
    {
        _history = history;
        _glucose = glucose;
        _time = time;
    }

    /// <summary>The profile versions of [<paramref name="from"/>, <paramref name="to"/>) and the changes between them.</summary>
    [HttpGet]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileHistory), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProfileHistory>> GetProfileHistory(
        [FromQuery] long from, [FromQuery] long to, CancellationToken ct = default)
    {
        if (RejectRange(from, to, V4ReadLimits.MaxDateSpanDays) is { } rejected)
            return rejected;
        return Ok(await _history.GetHistoryAsync(from, to, ct));
    }

    /// <summary>
    /// Each schedule over [<paramref name="from"/>, <paramref name="to"/>) in real time, at most
    /// 14 days of it. The loop's sensitivity and the temporary targets are each left out for a caller
    /// who may not read the category they belong to.
    /// </summary>
    [HttpGet("lanes")]
    [RemoteQuery]
    [RequireScope(Scope.TherapyRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(ProfileLanes), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProfileLanes>> GetProfileLanes(
        [FromQuery] long from, [FromQuery] long to, CancellationToken ct = default)
    {
        if (RejectRange(from, to, MaxLaneSpanDays) is { } rejected)
            return rejected;
        var granted = HttpContext.GetGrantedScopes();
        return Ok(await _history.GetLanesAsync(
            from,
            to,
            loop: Scope.Satisfies(granted, Scope.DevicesRead),
            temporaryTargets: StateSpanReadScopeGuard.CanRead(granted, StateSpanCategory.TemporaryTarget),
            ct));
    }

    /// <summary>The sensor glucose of [<paramref name="from"/>, <paramref name="to"/>) in equal buckets.</summary>
    /// <param name="from">Window start, in Unix milliseconds.</param>
    /// <param name="to">Window end, in Unix milliseconds.</param>
    /// <param name="buckets">How many buckets to cut the window into, 1 to 2000.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("glucose")]
    [RemoteQuery]
    [RequireScope(Scope.GlucoseRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(List<GlucoseTracePoint>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<GlucoseTracePoint>>> GetGlucoseTrace(
        [FromQuery] long from, [FromQuery] long to, [FromQuery] int buckets = 288, CancellationToken ct = default)
    {
        if (RejectRange(from, to, V4ReadLimits.MaxDateSpanDays) is { } rejected)
            return rejected;
        return Ok(await _glucose.GetTraceAsync(from, to, Math.Clamp(buckets, 1, MaxTraceBuckets), ct));
    }

    /// <summary>
    /// The sensor glucose of <paramref name="days"/> days before <paramref name="at"/> and of as many
    /// after it, the later window ending now at the latest.
    /// </summary>
    /// <param name="at">The instant between the two windows, in Unix milliseconds.</param>
    /// <param name="days">The length of each window, 1 to 30.</param>
    /// <param name="ct">Cancels the operation.</param>
    [HttpGet("glucose-around")]
    [RemoteQuery]
    [RequireScope(Scope.GlucoseRead)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(GlucoseAroundChange), StatusCodes.Status200OK)]
    public async Task<ActionResult<GlucoseAroundChange>> GetGlucoseAroundChange(
        [FromQuery] long at, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        if (!IsInstant(at))
            return Problem(detail: "The time is out of range.", statusCode: 400, title: "Bad Request");
        return Ok(await _glucose.GetAroundAsync(
            at,
            Math.Clamp(days, 1, MaxAroundDays) * TimeSpan.MillisecondsPerDay,
            _time.GetUtcNow().ToUnixTimeMilliseconds(),
            ct));
    }

    private static bool IsInstant(long mills) => mills is >= 0 and <= MaxMills;

    private ObjectResult? RejectRange(long from, long to, int maxDays) =>
        !IsInstant(from) || !IsInstant(to)
            ? Problem(detail: "The times are out of range.", statusCode: 400, title: "Bad Request")
            : to <= from
                ? Problem(detail: "The range must end after it starts.", statusCode: 400, title: "Bad Request")
                : this.RejectDateSpan(from, to, maxDays);
}
