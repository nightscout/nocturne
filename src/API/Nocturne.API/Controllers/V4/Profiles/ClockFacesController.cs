using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.API.Attributes;
using Nocturne.API.Authorization;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Controllers.V4.Profiles;

/// <summary>
/// Controller for clock face configuration management.
/// </summary>
/// <remarks>
/// A face serves glucose to anyone holding its link, so creating, changing or deleting one is
/// managing a public link to the tenant and takes <see cref="Scope.SharingManage"/>, as
/// <see cref="Identity.ShareLinkController"/> does. A face belongs to the caller's own subject, so a
/// caller with none (the instance key) is refused rather than saving an ownerless face, and reads
/// an empty list and an empty starter.
/// </remarks>
/// <seealso cref="IClockFaceService"/>
[ApiController]
[Tags("Profiles")]
[Route("api/v4/clockfaces")]
public class ClockFacesController : ControllerBase
{
    private readonly IClockFaceService _clockFaceService;
    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly ILogger<ClockFacesController> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ClockFacesController"/>.
    /// </summary>
    /// <param name="clockFaceService">Service for clock face storage and retrieval.</param>
    /// <param name="sensorGlucoseRepository">Repository for the glucose readings a public clock displays.</param>
    /// <param name="logger">Logger instance.</param>
    public ClockFacesController(
        IClockFaceService clockFaceService,
        ISensorGlucoseRepository sensorGlucoseRepository,
        ILogger<ClockFacesController> logger)
    {
        _clockFaceService = clockFaceService;
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _logger = logger;
    }

    /// <summary>
    /// Get a clock face configuration by ID (public, no authentication required)
    /// </summary>
    /// <param name="id">Clock face UUID</param>
    /// <returns>Clock face configuration</returns>
    [HttpGet("{id:guid}")]
    [RemoteQuery]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ClockFacePublicDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ClockFacePublicDto>> GetById(Guid id)
    {
        var clockFace = await _clockFaceService.GetByIdAsync(id, HttpContext.RequestAborted);

        if (clockFace == null)
        {
            return NotFound();
        }

        return Ok(new ClockFacePublicDto
        {
            Id = clockFace.Id,
            Config = clockFace.Config
        });
    }

    /// <summary>
    /// Get the latest glucose readings a public clock face displays (public, no authentication required).
    /// </summary>
    /// <remarks>
    /// The clock UUID is the capability: a caller must hold a valid clock id for the host-resolved
    /// tenant to get any data, and tenant-isolation RLS scopes the read to that tenant. This endpoint
    /// only ever reads sensor glucose — never treatments, device status, or any other category — so a
    /// clock link grants exactly what the clock shows and nothing more. It is deliberately independent
    /// of the tenant's global anonymous-read setting.
    /// </remarks>
    /// <param name="id">Clock face UUID</param>
    /// <returns>The two most recent sensor glucose readings (newest first), or 404 if the clock does not exist.</returns>
    [HttpGet("{id:guid}/glucose")]
    [RemoteQuery]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ClockGlucoseDto[]), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ClockGlucoseDto[]>> GetGlucose(Guid id)
    {
        var clockFace = await _clockFaceService.GetByIdAsync(id, HttpContext.RequestAborted);

        if (clockFace == null)
        {
            return NotFound();
        }

        // Latest two readings: the newest drives the value/trend; the previous is a delta fallback.
        var readings = await _sensorGlucoseRepository.GetAsync(
            from: null, to: null, device: null, source: null,
            limit: 2, offset: 0, descending: true, ct: HttpContext.RequestAborted);

        var dtos = readings.Select(r => new ClockGlucoseDto
        {
            Mills = r.Mills,
            Mgdl = r.Mgdl,
            Direction = r.Direction?.ToString(),
            Delta = r.Delta,
            DataSource = r.DataSource,
        }).ToArray();

        return Ok(dtos);
    }

    /// <summary>
    /// List all clock faces for the current user
    /// </summary>
    /// <returns>List of clock faces</returns>
    [HttpGet]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(ClockFaceListItem[]), 200)]
    public async Task<ActionResult<ClockFaceListItem[]>> List()
    {
        if (HttpContext.GetSubjectIdString() is not { } userId)
        {
            return Ok(Array.Empty<ClockFaceListItem>());
        }

        var clockFaces = await _clockFaceService.GetByUserAsync(userId, HttpContext.RequestAborted);

        return Ok(clockFaces.ToArray());
    }

    /// <summary>
    /// The layout a new face starts from, with the current user's units and time format
    /// </summary>
    /// <returns>Starter clock face configuration</returns>
    [HttpGet("starter")]
    [RemoteQuery]
    [Authorize]
    [ProducesResponseType(typeof(ClockFaceConfig), 200)]
    public async Task<ActionResult<ClockFaceConfig>> GetStarter()
    {
        if (HttpContext.GetSubjectIdString() is not { } userId)
        {
            return Ok(new ClockFaceConfig());
        }

        return Ok(await _clockFaceService.GetStarterConfigAsync(userId, HttpContext.RequestAborted));
    }

    /// <summary>
    /// Create a new clock face
    /// </summary>
    /// <param name="request">Clock face creation request</param>
    /// <returns>Created clock face</returns>
    [HttpPost]
    [RemoteCommand(Invalidates = ["List"])]
    [Authorize]
    [RequireScope(Scope.SharingManage)]
    [ProducesResponseType(typeof(ClockFace), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    public async Task<ActionResult<ClockFace>> Create([FromBody] CreateClockFaceRequest request)
    {
        if (HttpContext.GetSubjectIdString() is not { } userId)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Problem(detail: "Name is required", statusCode: 400, title: "Bad Request");
        }

        if (request.Config?.Validate() is { } configError)
        {
            return Problem(detail: configError, statusCode: 400, title: "Bad Request");
        }

        if (AlertsSettingWithoutScope(request.Config) is { } refused)
        {
            return refused;
        }

        var clockFace = await _clockFaceService.CreateAsync(userId, request, HttpContext.RequestAborted);

        return CreatedAtAction(nameof(GetById), new { id = clockFace.Id }, clockFace);
    }

    /// <summary>
    /// Update an existing clock face (owner only)
    /// </summary>
    /// <param name="id">Clock face UUID</param>
    /// <param name="request">Update request</param>
    /// <returns>Updated clock face</returns>
    [HttpPut("{id:guid}")]
    [RemoteCommand(Invalidates = ["List", "GetById"])]
    [Authorize]
    [RequireScope(Scope.SharingManage)]
    [ProducesResponseType(typeof(ClockFace), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ClockFace>> Update(Guid id, [FromBody] UpdateClockFaceRequest request)
    {
        if (HttpContext.GetSubjectIdString() is not { } userId)
        {
            return Unauthorized();
        }

        if (request.Config?.Validate() is { } configError)
        {
            return Problem(detail: configError, statusCode: 400, title: "Bad Request");
        }

        var existing = await _clockFaceService.GetByIdAsync(id, HttpContext.RequestAborted);
        if (AlertsSettingWithoutScope(request.Config, existing?.Config.Settings) is { } refused)
        {
            return refused;
        }

        var clockFace = await _clockFaceService.UpdateAsync(id, userId, request, HttpContext.RequestAborted);

        if (clockFace == null)
        {
            return NotFound();
        }

        return Ok(clockFace);
    }

    /// <summary>
    /// Refuses turning on alerts for a face from a caller who may not read them: the face would hand
    /// them to anyone holding its link. Keeping a setting the face already had is not turning it on,
    /// and <see cref="ClockFaceAlertsController"/> stops serving alerts once the owner loses them.
    /// </summary>
    private ObjectResult? AlertsSettingWithoutScope(ClockFaceConfig? config, ClockSettings? before = null)
    {
        var turnsOn = config?.Settings is { } after
            && ((after.ShowAlerts && before?.ShowAlerts != true)
                || (after.AllowMarkSeen && before?.AllowMarkSeen != true));

        return turnsOn && !HttpContext.HasScope(Scope.AlertsRead)
            ? this.ForbiddenForScope(Scope.AlertsRead)
            : null;
    }

    /// <summary>
    /// Delete a clock face (owner only)
    /// </summary>
    /// <param name="id">Clock face UUID</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id:guid}")]
    [RemoteCommand(Invalidates = ["List"])]
    [Authorize]
    [RequireScope(Scope.SharingManage)]
    [ProducesResponseType(204)]
    [ProducesResponseType(401)]
    [ProducesResponseType(403)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(Guid id)
    {
        if (HttpContext.GetSubjectIdString() is not { } userId)
        {
            return Unauthorized();
        }

        var deleted = await _clockFaceService.DeleteAsync(id, userId, HttpContext.RequestAborted);

        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }
}
