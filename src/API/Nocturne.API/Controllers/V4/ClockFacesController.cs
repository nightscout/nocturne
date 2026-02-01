using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Extensions;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for clock face configuration management
/// </summary>
[ApiController]
[Route("api/v4/clockfaces")]
[Tags("V4 Clock Faces")]
public class ClockFacesController : ControllerBase
{
    private readonly IClockFaceService _clockFaceService;
    private readonly ILogger<ClockFacesController> _logger;

    public ClockFacesController(
        IClockFaceService clockFaceService,
        ILogger<ClockFacesController> logger)
    {
        _clockFaceService = clockFaceService;
        _logger = logger;
    }

    /// <summary>
    /// Get a clock face configuration by ID (public, no authentication required)
    /// </summary>
    /// <param name="id">Clock face UUID</param>
    /// <returns>Clock face configuration</returns>
    [HttpGet("{id:guid}")]
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
    /// List all clock faces for the current user
    /// </summary>
    /// <returns>List of clock faces</returns>
    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(ClockFaceListItem[]), 200)]
    public async Task<ActionResult<ClockFaceListItem[]>> List()
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var clockFaces = await _clockFaceService.GetByUserAsync(userId, HttpContext.RequestAborted);

        return Ok(clockFaces.ToArray());
    }

    /// <summary>
    /// Create a new clock face
    /// </summary>
    /// <param name="request">Clock face creation request</param>
    /// <returns>Created clock face</returns>
    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(ClockFace), 201)]
    [ProducesResponseType(400)]
    public async Task<ActionResult<ClockFace>> Create([FromBody] CreateClockFaceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        var userId = HttpContext.GetSubjectIdString()!;
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
    [Authorize]
    [ProducesResponseType(typeof(ClockFace), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ClockFace>> Update(Guid id, [FromBody] UpdateClockFaceRequest request)
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var clockFace = await _clockFaceService.UpdateAsync(id, userId, request, HttpContext.RequestAborted);

        if (clockFace == null)
        {
            return NotFound();
        }

        return Ok(clockFace);
    }

    /// <summary>
    /// Delete a clock face (owner only)
    /// </summary>
    /// <param name="id">Clock face UUID</param>
    /// <returns>Success status</returns>
    [HttpDelete("{id:guid}")]
    [Authorize]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> Delete(Guid id)
    {
        var userId = HttpContext.GetSubjectIdString()!;
        var deleted = await _clockFaceService.DeleteAsync(id, userId, HttpContext.RequestAborted);

        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }
}
