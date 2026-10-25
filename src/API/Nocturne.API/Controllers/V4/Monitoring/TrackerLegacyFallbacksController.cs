using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Controllers.V4.Base;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Monitoring;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Monitoring;

/// <summary>
/// The legacy consumable ages (SAGE, CAGE, IAGE, BAGE) a tenant without a tracker for them sees in
/// a tracker's place, and their conversion into trackers.
/// </summary>
/// <seealso cref="ILegacyFallbackService"/>
[ApiController]
[Tags("Monitoring")]
[Route("api/v4/trackers")]
public class TrackerLegacyFallbacksController(ILegacyFallbackService fallbacks)
    : ControllerBase, IWriteScopedController
{
    /// <inheritdoc cref="TrackersController.WriteScope"/>
    public string WriteScope => Scope.AlertsReadWrite;

    /// <summary>
    /// A fallback is a tracker read of device data: it needs what tracker reads need
    /// (<see cref="TrackerReadAccess"/>) and <see cref="Scope.DevicesRead"/> for the ages.
    /// </summary>
    private bool ReadsFallbacks =>
        TrackerReadAccess.Allows(HttpContext) && HttpContext.HasScope(Scope.DevicesRead);

    /// <summary>Coverage is read from tracker definitions alone.</summary>
    private bool ReadsCoverage => TrackerReadAccess.Allows(HttpContext);

    /// <summary>
    /// The consumables no tracker covers whose latest device event is recent enough to trust, and
    /// the definitions covering the others. The fallbacks need tracker read access and
    /// <c>devices.read</c>; the coverage needs tracker read access only. Empty without either.
    /// </summary>
    [HttpGet("legacy-fallbacks")]
    [RemoteQuery]
    [ProducesResponseType(typeof(LegacyFallbacksDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<LegacyFallbacksDto>> GetLegacyFallbacks()
    {
        if (!ReadsCoverage)
            return Ok(new LegacyFallbacksDto());

        var found = await fallbacks.GetAsync(ReadsFallbacks, HttpContext.RequestAborted);
        return Ok(new LegacyFallbacksDto
        {
            Fallbacks = [.. found.Fallbacks.Select(LegacyFallbackDto.From)],
            Covered = [.. found.Covered.Select(c => new LegacyCoverageDto
            {
                Kind = c.Kind,
                DefinitionIds = [.. c.DefinitionIds],
            })],
        });
    }

    /// <summary>
    /// Creates a tracker for each requested consumable that has a fallback, and starts it at the
    /// latest device event that restarts it. Repeating the request creates nothing
    /// (<see cref="ILegacyFallbackService.CreateTrackersAsync"/>).
    /// </summary>
    [HttpPost("from-legacy")]
    [RequireDeclaredWriteScope]
    [Authorize]
    [RemoteCommand(Invalidates = [
        "Trackers_GetDefinitions",
        "Trackers_GetActiveInstances",
        "AlertRules_GetRules",
        "GetLegacyFallbacks",
    ])]
    [ProducesResponseType(typeof(LegacyConversionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<LegacyConversionDto>> CreateFromLegacy(
        [FromBody] CreateTrackersFromLegacyRequest request)
    {
        if (!ReadsFallbacks)
            return Problem(
                detail: "Creating trackers from device ages needs an account, not a share or guest link, that holds alerts.read and devices.read",
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden");

        if (request.Kinds.Where(k => !Enum.IsDefined(k)).Select(k => (int?)k).FirstOrDefault() is { } unknown)
            return Problem(
                detail: $"'{unknown}' is not a consumable kind",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");

        var conversion = await fallbacks.CreateTrackersAsync(
            request.Kinds, HttpContext.GetSubjectIdString()!, HttpContext.RequestAborted);

        return Ok(new LegacyConversionDto
        {
            Definitions = [.. conversion.Definitions.Select(TrackerDefinitionDto.FromEntity)],
            Instances = [.. conversion.Instances.Select(TrackerInstanceDto.FromEntity)],
            FailedKinds = [.. conversion.FailedKinds],
        });
    }
}

/// <summary>The legacy consumable ages, covered or not.</summary>
public class LegacyFallbacksDto
{
    public LegacyFallbackDto[] Fallbacks { get; set; } = [];
    public LegacyCoverageDto[] Covered { get; set; } = [];
}

/// <summary>A consumable trackers cover; see <see cref="LegacyFallbackRules.Covers"/>.</summary>
public class LegacyCoverageDto
{
    public LegacyConsumableKind Kind { get; set; }
    public Guid[] DefinitionIds { get; set; } = [];
}

/// <summary>A consumable with no tracker, aged from its latest device event.</summary>
public class LegacyFallbackDto
{
    public LegacyConsumableKind Kind { get; set; }

    /// <summary>The device event the age runs from.</summary>
    public Guid LatestEventId { get; set; }

    public DateTime LatestEventAt { get; set; }
    public DateTime? ExpectedEndAt { get; set; }
    public double AgeHours { get; set; }

    /// <summary>
    /// The schedule the tracker created from <see cref="Spec"/> would have, read as
    /// <see cref="TrackerInstanceDto.Schedule"/> is.
    /// </summary>
    public TrackerThresholdTimeDto[] Schedule { get; set; } = [];

    /// <summary>The tracker converting this fallback creates.</summary>
    public LegacyFallbackSpecDto Spec { get; set; } = new();

    public static LegacyFallbackDto From(LegacyFallback fallback)
    {
        var run = TrackerInstanceDto.FromEntity(fallback.Projected);
        return new()
        {
            Kind = fallback.Kind,
            LatestEventId = fallback.LatestEvent.Id,
            LatestEventAt = fallback.LatestEvent.Timestamp,
            ExpectedEndAt = run.ExpectedEndAt,
            AgeHours = run.AgeHours,
            Schedule = run.Schedule,
            Spec = new LegacyFallbackSpecDto
            {
                Name = fallback.Spec.Name,
                Category = fallback.Spec.Category,
                Icon = fallback.Spec.Icon,
                LifespanHours = fallback.Spec.LifespanHours,
            },
        };
    }
}

/// <summary>The tracker a <see cref="LegacyFallbackDto"/> converts into.</summary>
public class LegacyFallbackSpecDto
{
    public string Name { get; set; } = string.Empty;
    public TrackerCategory Category { get; set; }
    public string Icon { get; set; } = "activity";
    public int LifespanHours { get; set; }
}

public class CreateTrackersFromLegacyRequest
{
    [Required]
    public List<LegacyConsumableKind> Kinds { get; set; } = [];
}

/// <summary>The trackers a conversion created and their runs.</summary>
public class LegacyConversionDto
{
    public TrackerDefinitionDto[] Definitions { get; set; } = [];
    public TrackerInstanceDto[] Instances { get; set; } = [];

    /// <summary>Kinds whose conversion failed and committed nothing.</summary>
    public LegacyConsumableKind[] FailedKinds { get; set; } = [];
}
