using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Infrastructure.Data.Repositories.V4;

namespace Nocturne.API.Controllers.V4;

/// <summary>
/// Controller for querying correlated data across all V4 repositories by correlation ID
/// </summary>
[ApiController]
[Route("api/v4/correlated")]
[Authorize]
[Produces("application/json")]
[Tags("V4 Correlation")]
public class CorrelationController : ControllerBase
{
    private readonly SensorGlucoseRepository _sensorRepo;
    private readonly MeterGlucoseRepository _meterRepo;
    private readonly CalibrationRepository _calibrationRepo;
    private readonly BolusRepository _bolusRepo;
    private readonly BolusCalculationRepository _bolusCalcRepo;
    private readonly CarbIntakeRepository _carbIntakeRepo;
    private readonly BGCheckRepository _bgCheckRepo;
    private readonly NoteRepository _noteRepo;

    public CorrelationController(
        SensorGlucoseRepository sensorRepo,
        MeterGlucoseRepository meterRepo,
        CalibrationRepository calibrationRepo,
        BolusRepository bolusRepo,
        BolusCalculationRepository bolusCalcRepo,
        CarbIntakeRepository carbIntakeRepo,
        BGCheckRepository bgCheckRepo,
        NoteRepository noteRepo)
    {
        _sensorRepo = sensorRepo;
        _meterRepo = meterRepo;
        _calibrationRepo = calibrationRepo;
        _bolusRepo = bolusRepo;
        _bolusCalcRepo = bolusCalcRepo;
        _carbIntakeRepo = carbIntakeRepo;
        _bgCheckRepo = bgCheckRepo;
        _noteRepo = noteRepo;
    }

    /// <summary>
    /// Get all data correlated by a shared correlation ID across all V4 data types
    /// </summary>
    [HttpGet("{correlationId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetCorrelated(Guid correlationId, CancellationToken ct = default)
    {
        var result = new
        {
            SensorGlucose = await _sensorRepo.GetByCorrelationIdAsync(correlationId, ct),
            MeterGlucose = await _meterRepo.GetByCorrelationIdAsync(correlationId, ct),
            Calibrations = await _calibrationRepo.GetByCorrelationIdAsync(correlationId, ct),
            Boluses = await _bolusRepo.GetByCorrelationIdAsync(correlationId, ct),
            CarbIntakes = await _carbIntakeRepo.GetByCorrelationIdAsync(correlationId, ct),
            BGChecks = await _bgCheckRepo.GetByCorrelationIdAsync(correlationId, ct),
            Notes = await _noteRepo.GetByCorrelationIdAsync(correlationId, ct),
            BolusCalculations = await _bolusCalcRepo.GetByCorrelationIdAsync(correlationId, ct),
        };
        return Ok(result);
    }
}
