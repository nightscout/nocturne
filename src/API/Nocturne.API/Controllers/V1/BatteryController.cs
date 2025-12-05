using Microsoft.AspNetCore.Mvc;
using Nocturne.API.Attributes;
using Nocturne.Core.Contracts;
using Nocturne.Core.Models.Battery;

namespace Nocturne.API.Controllers.V1;

/// <summary>
/// Battery controller for tracking and analyzing device battery status
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
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
    [NightscoutEndpoint("/api/v1/battery/current")]
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
            return StatusCode(500, new { error = "Internal server error" });
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
    [NightscoutEndpoint("/api/v1/battery/readings")]
    [ProducesResponseType(typeof(IEnumerable<BatteryReading>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BatteryReading>>> GetBatteryReadings(
        [FromQuery] string? device = null,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
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
                from,
                to,
                cancellationToken
            );

            return Ok(readings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery readings");
            return StatusCode(500, new { error = "Internal server error" });
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
    [NightscoutEndpoint("/api/v1/battery/statistics")]
    [ProducesResponseType(typeof(IEnumerable<BatteryStatistics>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<BatteryStatistics>>> GetBatteryStatistics(
        [FromQuery] string? device = null,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
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
                from,
                to,
                cancellationToken
            );

            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery statistics");
            return StatusCode(500, new { error = "Internal server error" });
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
    [NightscoutEndpoint("/api/v1/battery/cycles")]
    [ProducesResponseType(typeof(IEnumerable<ChargeCycle>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<ChargeCycle>>> GetChargeCycles(
        [FromQuery] string? device = null,
        [FromQuery] long? from = null,
        [FromQuery] long? to = null,
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
                from,
                to,
                limit,
                cancellationToken
            );

            return Ok(cycles);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting charge cycles");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get list of all known devices with battery data
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of device identifiers</returns>
    [HttpGet("devices")]
    [NightscoutEndpoint("/api/v1/battery/devices")]
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
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}
