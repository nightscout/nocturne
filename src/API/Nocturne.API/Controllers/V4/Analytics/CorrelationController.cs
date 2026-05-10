using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nocturne.Core.Contracts.V4.Repositories;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Controller for querying correlated data across all V4 repositories by correlation ID.
/// A correlation ID links related records — for example, a sensor glucose reading with the
/// bolus that was delivered in response to it.
/// </summary>
/// <remarks>
/// Queries all supported V4 repositories in parallel and returns a composite result keyed by
/// data type. The following repositories are searched:
/// <see cref="ISensorGlucoseRepository"/>, <see cref="IMeterGlucoseRepository"/>,
/// <see cref="ICalibrationRepository"/>, <see cref="IBolusRepository"/>,
/// <see cref="ICarbIntakeRepository"/>, <see cref="IBGCheckRepository"/>,
/// <see cref="INoteRepository"/>, and <see cref="IBolusCalculationRepository"/>.
/// </remarks>
/// <seealso cref="ISensorGlucoseRepository"/>
/// <seealso cref="IBolusRepository"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/correlated")]
[Authorize]
[Produces("application/json")]
public class CorrelationController : ControllerBase
{
    private readonly ISensorGlucoseRepository _sensorRepo;
    private readonly IMeterGlucoseRepository _meterRepo;
    private readonly ICalibrationRepository _calibrationRepo;
    private readonly IBolusRepository _bolusRepo;
    private readonly IBolusCalculationRepository _bolusCalcRepo;
    private readonly ICarbIntakeRepository _carbIntakeRepo;
    private readonly IBGCheckRepository _bgCheckRepo;
    private readonly INoteRepository _noteRepo;

    public CorrelationController(
        ISensorGlucoseRepository sensorRepo,
        IMeterGlucoseRepository meterRepo,
        ICalibrationRepository calibrationRepo,
        IBolusRepository bolusRepo,
        IBolusCalculationRepository bolusCalcRepo,
        ICarbIntakeRepository carbIntakeRepo,
        IBGCheckRepository bgCheckRepo,
        INoteRepository noteRepo)
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
    /// Retrieves all records that share the given correlation ID across every V4 data type.
    /// </summary>
    /// <param name="correlationId">The shared correlation ID to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// An anonymous object with typed arrays for each data category
    /// (<c>SensorGlucose</c>, <c>MeterGlucose</c>, <c>Calibrations</c>, <c>Boluses</c>,
    /// <c>CarbIntakes</c>, <c>BGChecks</c>, <c>Notes</c>, <c>BolusCalculations</c>).
    /// Arrays are empty when no matching records exist in that category.
    /// </returns>
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
