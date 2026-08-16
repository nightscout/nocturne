using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.V4;
using OpenApi.Remote.Attributes;

namespace Nocturne.API.Controllers.V4.Analytics;

/// <summary>
/// Compares two of the patient's CGMs against each other over a window: readings time-paired
/// device to device, plus the agreement measures over that pairing.
/// </summary>
/// <seealso cref="CgmComparisonCalculator"/>
[ApiController]
[Tags("Analytics")]
[Route("api/v4/cgm-comparison")]
[Produces("application/json")]
[RequireScope(OAuthScopes.ReportsRead)]
public class CgmComparisonController : ControllerBase
{
    private const double MaxRangeDays = 90;
    private const double MaxToleranceMinutes = 30;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IPatientDeviceRepository _patientDeviceRepository;

    public CgmComparisonController(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IPatientDeviceRepository patientDeviceRepository)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _patientDeviceRepository = patientDeviceRepository;
    }

    /// <summary>
    /// Pair two registered CGMs' readings over a UTC window and measure their agreement.
    /// </summary>
    /// <param name="deviceAId">Registered CGM compared as A.</param>
    /// <param name="deviceBId">Registered CGM compared as B, the reference for relative measures.</param>
    /// <param name="startDate">Inclusive UTC start of the window.</param>
    /// <param name="endDate">Inclusive UTC end of the window.</param>
    /// <param name="toleranceMinutes">Maximum time difference at which two readings are matched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet]
    [RemoteQuery]
    [ResponseCache(Duration = 60, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(typeof(CgmComparisonResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CgmComparisonResult>> Compare(
        [FromQuery] Guid deviceAId,
        [FromQuery] Guid deviceBId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate,
        [FromQuery] double toleranceMinutes = 5,
        CancellationToken cancellationToken = default)
    {
        if (startDate == default || endDate == default)
            return BadRequest(new { error = "startDate and endDate are required." });

        if (endDate <= startDate)
            return BadRequest(new { error = "endDate must be after startDate." });

        if ((endDate - startDate).TotalDays > MaxRangeDays)
            return BadRequest(new { error = $"Date range must not exceed {MaxRangeDays} days." });

        if (deviceAId == deviceBId)
            return BadRequest(new { error = "deviceAId and deviceBId must be different devices." });

        // Stated as what a tolerance must be rather than what it must not be, so NaN — which
        // compares false against every bound — is rejected rather than reaching TimeSpan.
        if (!(toleranceMinutes > 0 && toleranceMinutes <= MaxToleranceMinutes))
            return BadRequest(new { error = $"toleranceMinutes must be greater than 0 and at most {MaxToleranceMinutes}." });

        var deviceA = await _patientDeviceRepository.GetByIdAsync(deviceAId, cancellationToken);
        var deviceB = await _patientDeviceRepository.GetByIdAsync(deviceBId, cancellationToken);

        if (deviceA is null || deviceB is null)
            return NotFound(new { error = "One or both devices were not found." });

        if (deviceA.DeviceCategory != DeviceCategory.CGM || deviceB.DeviceCategory != DeviceCategory.CGM)
            return BadRequest(new { error = "Both devices must be CGMs." });

        // Kind=Unspecified is what query-bound dates arrive as when the client omits an offset, and
        // Npgsql rejects those against timestamptz. Same normalization as StatisticsController.
        var startUtc = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        var readingsATask = ReadAsync(startUtc, endUtc, deviceAId, cancellationToken);
        var readingsBTask = ReadAsync(startUtc, endUtc, deviceBId, cancellationToken);
        await Task.WhenAll(readingsATask, readingsBTask);

        var result = CgmComparisonCalculator.Compare(
            await readingsATask,
            await readingsBTask,
            TimeSpan.FromMinutes(toleranceMinutes));

        result.DeviceAId = deviceAId;
        result.DeviceAName = deviceA.DisplayName();
        result.DeviceBId = deviceBId;
        result.DeviceBName = deviceB.DisplayName();
        result.StartDate = startUtc;
        result.EndDate = endUtc;

        return Ok(result);
    }

    private Task<IEnumerable<SensorGlucose>> ReadAsync(
        DateTime from, DateTime to, Guid patientDeviceId, CancellationToken ct) =>
        _sensorGlucoseRepository.GetAsync(
            from, to, null, null, int.MaxValue, descending: false, ct: ct, patientDeviceId: patientDeviceId);
}
