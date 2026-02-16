using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing device status data: APS snapshots, pump snapshots, and uploader snapshots
/// </summary>
[ApiController]
[Route("api/v4/device-status")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Device Status")]
public class DeviceStatusController : ControllerBase
{
    private readonly IApsSnapshotRepository _apsRepo;
    private readonly IPumpSnapshotRepository _pumpRepo;
    private readonly IUploaderSnapshotRepository _uploaderRepo;

    public DeviceStatusController(
        IApsSnapshotRepository apsRepo,
        IPumpSnapshotRepository pumpRepo,
        IUploaderSnapshotRepository uploaderRepo)
    {
        _apsRepo = apsRepo;
        _pumpRepo = pumpRepo;
        _uploaderRepo = uploaderRepo;
    }

    #region APS Snapshots

    /// <summary>
    /// Get APS snapshots with optional filtering
    /// </summary>
    [HttpGet("aps")]
    [ProducesResponseType(typeof(PaginatedResponse<ApsSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<ApsSnapshot>>> GetApsSnapshots(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." });
        var descending = sort == "mills_desc";
        var data = await _apsRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _apsRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<ApsSnapshot> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get an APS snapshot by ID
    /// </summary>
    [HttpGet("aps/{id:guid}")]
    [ProducesResponseType(typeof(ApsSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApsSnapshot>> GetApsSnapshotById(Guid id, CancellationToken ct = default)
    {
        var result = await _apsRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    #endregion

    #region Pump Snapshots

    /// <summary>
    /// Get pump snapshots with optional filtering
    /// </summary>
    [HttpGet("pump")]
    [ProducesResponseType(typeof(PaginatedResponse<PumpSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<PumpSnapshot>>> GetPumpSnapshots(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." });
        var descending = sort == "mills_desc";
        var data = await _pumpRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _pumpRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<PumpSnapshot> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a pump snapshot by ID
    /// </summary>
    [HttpGet("pump/{id:guid}")]
    [ProducesResponseType(typeof(PumpSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PumpSnapshot>> GetPumpSnapshotById(Guid id, CancellationToken ct = default)
    {
        var result = await _pumpRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    #endregion

    #region Uploader Snapshots

    /// <summary>
    /// Get uploader snapshots with optional filtering
    /// </summary>
    [HttpGet("uploader")]
    [ProducesResponseType(typeof(PaginatedResponse<UploaderSnapshot>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaginatedResponse<UploaderSnapshot>>> GetUploaderSnapshots(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        if (sort is not "mills_desc" and not "mills_asc")
            return BadRequest(new { error = $"Invalid sort value '{sort}'. Must be 'mills_asc' or 'mills_desc'." });
        var descending = sort == "mills_desc";
        var data = await _uploaderRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _uploaderRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<UploaderSnapshot> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get an uploader snapshot by ID
    /// </summary>
    [HttpGet("uploader/{id:guid}")]
    [ProducesResponseType(typeof(UploaderSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UploaderSnapshot>> GetUploaderSnapshotById(Guid id, CancellationToken ct = default)
    {
        var result = await _uploaderRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    #endregion
}
