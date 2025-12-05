using Nocturne.Core.Models.Battery;

namespace Nocturne.Core.Contracts;

/// <summary>
/// Service for tracking and analyzing device battery status
/// </summary>
public interface IBatteryService
{
    /// <summary>
    /// Get the current battery status for all tracked devices
    /// </summary>
    /// <param name="recentMinutes">How many minutes back to consider for "recent" readings (default 30)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current battery status for all devices</returns>
    Task<CurrentBatteryStatus> GetCurrentBatteryStatusAsync(
        int recentMinutes = 30,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get battery readings for a specific device over a time period
    /// </summary>
    /// <param name="device">Device identifier (null for all devices)</param>
    /// <param name="fromMills">Start time in milliseconds</param>
    /// <param name="toMills">End time in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Battery readings in the period</returns>
    Task<IEnumerable<BatteryReading>> GetBatteryReadingsAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get battery statistics for a specific device or all devices
    /// </summary>
    /// <param name="device">Device identifier (null for all devices)</param>
    /// <param name="fromMills">Start time in milliseconds</param>
    /// <param name="toMills">End time in milliseconds</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Battery statistics for the period</returns>
    Task<IEnumerable<BatteryStatistics>> GetBatteryStatisticsAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get charge cycle history for a device
    /// </summary>
    /// <param name="device">Device identifier (null for all devices)</param>
    /// <param name="fromMills">Start time in milliseconds</param>
    /// <param name="toMills">End time in milliseconds</param>
    /// <param name="limit">Maximum number of cycles to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Charge cycles in the period</returns>
    Task<IEnumerable<ChargeCycle>> GetChargeCyclesAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        int limit = 100,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get a list of all known devices with battery data
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of device identifiers</returns>
    Task<IEnumerable<string>> GetKnownDevicesAsync(CancellationToken cancellationToken = default);
}
