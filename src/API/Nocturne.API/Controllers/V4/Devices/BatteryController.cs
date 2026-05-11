using Microsoft.AspNetCore.Mvc;
using OpenApi.Remote.Attributes;
using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Models.Battery;

namespace Nocturne.API.Controllers.V4.Devices;

/// <summary>
/// Controller for tracking and analyzing device battery status across all connected devices.
/// </summary>
/// <remarks>
/// Battery readings are derived from uploader device status records ingested by connectors.
/// This controller surfaces five views:
/// <list type="bullet">
///   <item><description><c>GET /current</c> — latest reading per device within the <c>recentMinutes</c> window (default 30 min).</description></item>
///   <item><description><c>GET /readings</c> — raw time-series readings for a device and date range.</description></item>
///   <item><description><c>GET /statistics</c> — aggregated statistics (min/max/avg) for a device and period.</description></item>
///   <item><description><c>GET /cycles</c> — detected charge cycle events for a device.</description></item>
///   <item><description><c>GET /devices</c> — all device identifiers that have battery data.</description></item>
/// </list>
///
/// All endpoints delegate to <see cref="IBatteryService"/>. Time range parameters are expressed as
/// UTC <see cref="DateTime"/> values and converted internally to Unix milliseconds.
/// </remarks>
/// <seealso cref="IBatteryService"/>
/// <seealso cref="CurrentBatteryStatus"/>
/// <seealso cref="BatteryReading"/>
/// <seealso cref="BatteryStatistics"/>
/// <seealso cref="ChargeCycle"/>
[ApiController]
[Tags("Devices")]
[Route("api/v4/[controller]")]
public class BatteryController : ControllerBase
{
    private readonly IBatteryService _batteryService;
    private readonly ILogger<BatteryController> _logger;

    public BatteryController(IBatteryService batteryService, ILogger<BatteryController> logger)
    {
        _batteryService = batteryService;
        _logger = logger;
    }

    /// <summary>
    /// Get the current battery status for all tracked devices
    /// </summary>
    /// <param name="recentMinutes">How many minutes back to consider for "recent" readings (default: 30)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current battery status for all devices</returns>
    [HttpGet("current")]
    [RemoteQuery]
    [ProducesResponseType(typeof(CurrentBatteryStatus), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<CurrentBatteryStatus>> GetCurrentBatteryStatus(
        [FromQuery] int recentMinutes = 30,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Current battery status requested with recentMinutes: {RecentMinutes}",
            recentMinutes
        );

        try
        {
            var status = await _batteryService.GetCurrentBatteryStatusAsync(
                recentMinutes,
                cancellationToken
            );

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current battery status");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get battery readings for a device over a time period
    /// </summary>
    /// <param name="device">Device identifier (optional, returns all devices if not specified)</param>
    /// <param name="from">Start time in milliseconds since Unix epoch</param>
    /// <param name="to">End time in milliseconds since Unix epoch</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Battery readings for the specified period</returns>
    [HttpGet("readings")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<BatteryReading>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BatteryReading>>> GetBatteryReadings(
        [FromQuery] string? device = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Battery readings requested for device: {Device}, from: {From}, to: {To}",
            device,
            from,
            to
        );

        try
        {
            var readings = await _batteryService.GetBatteryReadingsAsync(
                device,
                from.HasValue ? new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                to.HasValue ? new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                cancellationToken
            );

            return Ok(readings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery readings");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get battery statistics for a device or all devices
    /// </summary>
    /// <param name="device">Device identifier (optional, returns all devices if not specified)</param>
    /// <param name="from">Start time in milliseconds since Unix epoch (default: 7 days ago)</param>
    /// <param name="to">End time in milliseconds since Unix epoch (default: now)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Battery statistics for the specified period</returns>
    [HttpGet("statistics")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<BatteryStatistics>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BatteryStatistics>>> GetBatteryStatistics(
        [FromQuery] string? device = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Battery statistics requested for device: {Device}, from: {From}, to: {To}",
            device,
            from,
            to
        );

        try
        {
            var statistics = await _batteryService.GetBatteryStatisticsAsync(
                device,
                from.HasValue ? new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                to.HasValue ? new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                cancellationToken
            );

            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery statistics");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get charge cycle history for a device
    /// </summary>
    /// <param name="device">Device identifier (optional, returns all devices if not specified)</param>
    /// <param name="from">Start time in milliseconds since Unix epoch</param>
    /// <param name="to">End time in milliseconds since Unix epoch</param>
    /// <param name="limit">Maximum number of cycles to return (default: 100)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Charge cycles for the specified period</returns>
    [HttpGet("cycles")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<ChargeCycle>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<ChargeCycle>>> GetChargeCycles(
        [FromQuery] string? device = null,
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug(
            "Charge cycles requested for device: {Device}, from: {From}, to: {To}, limit: {Limit}",
            device,
            from,
            to,
            limit
        );

        try
        {
            var cycles = await _batteryService.GetChargeCyclesAsync(
                device,
                from.HasValue ? new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                to.HasValue ? new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds() : null,
                limit,
                cancellationToken
            );

            return Ok(cycles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting charge cycles");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }

    /// <summary>
    /// Get list of all known devices with battery data
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of device identifiers</returns>
    [HttpGet("devices")]
    [RemoteQuery]
    [ProducesResponseType(typeof(IEnumerable<string>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<string>>> GetKnownDevices(
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogDebug("Known devices requested");

        try
        {
            var devices = await _batteryService.GetKnownDevicesAsync(cancellationToken);
            return Ok(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting known devices");
            return Problem(detail: "Internal server error", statusCode: 500, title: "Internal Server Error");
        }
    }
}
