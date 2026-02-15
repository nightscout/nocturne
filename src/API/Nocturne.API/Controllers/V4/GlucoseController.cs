using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for managing glucose data: sensor readings, meter readings, and calibrations
/// </summary>
[ApiController]
[Route("api/v4/glucose")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Glucose")]
public class GlucoseController : ControllerBase
{
    private readonly SensorGlucoseRepository _sensorRepo;
    private readonly MeterGlucoseRepository _meterRepo;
    private readonly CalibrationRepository _calibrationRepo;

    public GlucoseController(
        SensorGlucoseRepository sensorRepo,
        MeterGlucoseRepository meterRepo,
        CalibrationRepository calibrationRepo)
    {
        _sensorRepo = sensorRepo;
        _meterRepo = meterRepo;
        _calibrationRepo = calibrationRepo;
    }

    #region Sensor Glucose

    /// <summary>
    /// Get sensor glucose readings with optional filtering
    /// </summary>
    [HttpGet("sensor")]
    [ProducesResponseType(typeof(PaginatedResponse<SensorGlucose>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<SensorGlucose>>> GetSensorGlucose(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _sensorRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _sensorRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<SensorGlucose> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a sensor glucose reading by ID
    /// </summary>
    [HttpGet("sensor/{id:guid}")]
    [ProducesResponseType(typeof(SensorGlucose), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SensorGlucose>> GetSensorGlucoseById(Guid id, CancellationToken ct = default)
    {
        var result = await _sensorRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new sensor glucose reading
    /// </summary>
    [HttpPost("sensor")]
    [ProducesResponseType(typeof(SensorGlucose), StatusCodes.Status201Created)]
    public async Task<ActionResult<SensorGlucose>> CreateSensorGlucose([FromBody] SensorGlucose model, CancellationToken ct = default)
    {
        var created = await _sensorRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetSensorGlucoseById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing sensor glucose reading
    /// </summary>
    [HttpPut("sensor/{id:guid}")]
    [ProducesResponseType(typeof(SensorGlucose), StatusCodes.Status200OK)]
    public async Task<ActionResult<SensorGlucose>> UpdateSensorGlucose(Guid id, [FromBody] SensorGlucose model, CancellationToken ct = default)
    {
        var updated = await _sensorRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a sensor glucose reading
    /// </summary>
    [HttpDelete("sensor/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteSensorGlucose(Guid id, CancellationToken ct = default)
    {
        await _sensorRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion

    #region Meter Glucose

    /// <summary>
    /// Get meter glucose readings with optional filtering
    /// </summary>
    [HttpGet("meter")]
    [ProducesResponseType(typeof(PaginatedResponse<MeterGlucose>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<MeterGlucose>>> GetMeterGlucose(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _meterRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _meterRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<MeterGlucose> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a meter glucose reading by ID
    /// </summary>
    [HttpGet("meter/{id:guid}")]
    [ProducesResponseType(typeof(MeterGlucose), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MeterGlucose>> GetMeterGlucoseById(Guid id, CancellationToken ct = default)
    {
        var result = await _meterRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new meter glucose reading
    /// </summary>
    [HttpPost("meter")]
    [ProducesResponseType(typeof(MeterGlucose), StatusCodes.Status201Created)]
    public async Task<ActionResult<MeterGlucose>> CreateMeterGlucose([FromBody] MeterGlucose model, CancellationToken ct = default)
    {
        var created = await _meterRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetMeterGlucoseById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing meter glucose reading
    /// </summary>
    [HttpPut("meter/{id:guid}")]
    [ProducesResponseType(typeof(MeterGlucose), StatusCodes.Status200OK)]
    public async Task<ActionResult<MeterGlucose>> UpdateMeterGlucose(Guid id, [FromBody] MeterGlucose model, CancellationToken ct = default)
    {
        var updated = await _meterRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a meter glucose reading
    /// </summary>
    [HttpDelete("meter/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteMeterGlucose(Guid id, CancellationToken ct = default)
    {
        await _meterRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion

    #region Calibrations

    /// <summary>
    /// Get calibrations with optional filtering
    /// </summary>
    [HttpGet("calibrations")]
    [ProducesResponseType(typeof(PaginatedResponse<Calibration>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<Calibration>>> GetCalibrations(
        [FromQuery] long? from, [FromQuery] long? to,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0,
        [FromQuery] string sort = "mills_desc",
        [FromQuery] string? device = null, [FromQuery] string? source = null,
        CancellationToken ct = default)
    {
        var descending = sort == "mills_desc";
        var data = await _calibrationRepo.GetAsync(from, to, device, source, limit, offset, descending, ct);
        var total = await _calibrationRepo.CountAsync(from, to, ct);
        return Ok(new PaginatedResponse<Calibration> { Data = data, Pagination = new(limit, offset, total) });
    }

    /// <summary>
    /// Get a calibration by ID
    /// </summary>
    [HttpGet("calibrations/{id:guid}")]
    [ProducesResponseType(typeof(Calibration), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<Calibration>> GetCalibrationById(Guid id, CancellationToken ct = default)
    {
        var result = await _calibrationRepo.GetByIdAsync(id, ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Create a new calibration
    /// </summary>
    [HttpPost("calibrations")]
    [ProducesResponseType(typeof(Calibration), StatusCodes.Status201Created)]
    public async Task<ActionResult<Calibration>> CreateCalibration([FromBody] Calibration model, CancellationToken ct = default)
    {
        var created = await _calibrationRepo.CreateAsync(model, ct);
        return CreatedAtAction(nameof(GetCalibrationById), new { id = created.Id }, created);
    }

    /// <summary>
    /// Update an existing calibration
    /// </summary>
    [HttpPut("calibrations/{id:guid}")]
    [ProducesResponseType(typeof(Calibration), StatusCodes.Status200OK)]
    public async Task<ActionResult<Calibration>> UpdateCalibration(Guid id, [FromBody] Calibration model, CancellationToken ct = default)
    {
        var updated = await _calibrationRepo.UpdateAsync(id, model, ct);
        return Ok(updated);
    }

    /// <summary>
    /// Delete a calibration
    /// </summary>
    [HttpDelete("calibrations/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteCalibration(Guid id, CancellationToken ct = default)
    {
        await _calibrationRepo.DeleteAsync(id, ct);
        return NoContent();
    }

    #endregion
}
