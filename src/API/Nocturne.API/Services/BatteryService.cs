using Nocturne.Core.Contracts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Battery;
using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services;

/// <summary>
/// Service for tracking and analyzing device battery status from DeviceStatus entries
/// </summary>
public class BatteryService : IBatteryService
{
    private readonly IPostgreSqlService _postgreSqlService;
    private readonly ILogger<BatteryService> _logger;

    private const int DefaultWarnThreshold = 30;
    private const int DefaultUrgentThreshold = 20;

    public BatteryService(IPostgreSqlService postgreSqlService, ILogger<BatteryService> logger)
    {
        _postgreSqlService = postgreSqlService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CurrentBatteryStatus> GetCurrentBatteryStatusAsync(
        int recentMinutes = 30,
        CancellationToken cancellationToken = default
    )
    {
        var result = new CurrentBatteryStatus();

        try
        {
            var recentMills =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (recentMinutes * 60 * 1000);

            // Get recent device status entries
            var deviceStatuses = await _postgreSqlService.GetDeviceStatusWithAdvancedFilterAsync(
                count: 100,
                skip: 0,
                findQuery: $"{{\"mills\":{{\"$gte\":{recentMills}}}}}",
                cancellationToken: cancellationToken
            );

            // Filter to only those with uploader battery data
            var statusesWithBattery = deviceStatuses
                .Where(s => s.Uploader?.Battery != null || s.Uploader?.BatteryVoltage != null)
                .ToList();

            if (!statusesWithBattery.Any())
            {
                return result;
            }

            // Group by device
            foreach (var status in statusesWithBattery)
            {
                var deviceUri = status.Device ?? "uploader";
                var deviceName = ExtractDeviceName(deviceUri);

                if (!result.Devices.ContainsKey(deviceUri))
                {
                    result.Devices[deviceUri] = new DeviceBatteryStatus
                    {
                        Uri = deviceUri,
                        Name = deviceName,
                        Statuses = new List<BatteryReading>(),
                    };
                }

                var reading = ConvertToBatteryReading(status);
                if (reading != null)
                {
                    result.Devices[deviceUri].Statuses.Add(reading);
                }
            }

            // For each device, find the minimum battery in the last 10 minutes
            var recentLowests = new List<BatteryReading>();

            foreach (var device in result.Devices.Values)
            {
                // Sort by time descending
                device.Statuses = device.Statuses.OrderByDescending(s => s.Mills).ToList();

                if (device.Statuses.Any())
                {
                    var first = device.Statuses.First();
                    var tenMinutesAgo = first.Mills - (10 * 60 * 1000);

                    var recentStatuses = device
                        .Statuses.Where(s => s.Mills > tenMinutesAgo)
                        .ToList();

                    device.Min = recentStatuses.OrderBy(s => s.Battery ?? 100).FirstOrDefault();

                    if (device.Min != null)
                    {
                        recentLowests.Add(device.Min);
                    }
                }
            }

            // Find overall minimum
            var overallMin = recentLowests.OrderBy(r => r.Battery ?? 100).FirstOrDefault();

            if (overallMin != null)
            {
                result.Min = overallMin;
                result.Level = overallMin.Level;
                result.Display = overallMin.Display;
                result.Status = GetNotificationStatus(overallMin.Battery) ?? "ok";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting current battery status");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BatteryReading>> GetBatteryReadingsAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        CancellationToken cancellationToken = default
    )
    {
        var readings = new List<BatteryReading>();

        try
        {
            // Build query filter
            var filters = new List<string>();

            if (!string.IsNullOrEmpty(device))
            {
                filters.Add($"\"device\":\"{device}\"");
            }

            if (fromMills.HasValue)
            {
                filters.Add($"\"mills\":{{\"$gte\":{fromMills.Value}}}");
            }

            if (toMills.HasValue)
            {
                filters.Add($"\"mills\":{{\"$lte\":{toMills.Value}}}");
            }

            var findQuery = filters.Any() ? "{" + string.Join(",", filters) + "}" : null;

            var deviceStatuses = await _postgreSqlService.GetDeviceStatusWithAdvancedFilterAsync(
                count: 10000,
                skip: 0,
                findQuery: findQuery,
                cancellationToken: cancellationToken
            );

            foreach (var status in deviceStatuses)
            {
                var reading = ConvertToBatteryReading(status);
                if (reading != null)
                {
                    readings.Add(reading);
                }
            }

            // Sort by time
            return readings.OrderBy(r => r.Mills);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting battery readings");
        }

        return readings;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BatteryStatistics>> GetBatteryStatisticsAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        CancellationToken cancellationToken = default
    )
    {
        var statistics = new List<BatteryStatistics>();

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var from = fromMills ?? now - (7 * 24 * 60 * 60 * 1000); // Default 7 days
            var to = toMills ?? now;

            var readings = await GetBatteryReadingsAsync(device, from, to, cancellationToken);
            var cycles = await GetChargeCyclesAsync(device, from, to, 1000, cancellationToken);

            // Group readings by device
            var deviceGroups = readings.GroupBy(r => r.Device);

            foreach (var group in deviceGroups)
            {
                var deviceReadings = group.ToList();
                var deviceCycles = cycles.Where(c => c.Device == group.Key).ToList();

                var stats = CalculateStatistics(group.Key, deviceReadings, deviceCycles, from, to);
                statistics.Add(stats);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating battery statistics");
        }

        return statistics;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChargeCycle>> GetChargeCyclesAsync(
        string? device = null,
        long? fromMills = null,
        long? toMills = null,
        int limit = 100,
        CancellationToken cancellationToken = default
    )
    {
        var cycles = new List<ChargeCycle>();

        try
        {
            var readings = (
                await GetBatteryReadingsAsync(device, fromMills, toMills, cancellationToken)
            )
                .OrderBy(r => r.Mills)
                .ToList();

            if (readings.Count < 2)
            {
                return cycles;
            }

            // Group readings by device and detect charge cycles
            var deviceGroups = readings.GroupBy(r => r.Device);

            foreach (var group in deviceGroups)
            {
                var deviceReadings = group.OrderBy(r => r.Mills).ToList();
                var deviceCycles = DetectChargeCycles(group.Key, deviceReadings);
                cycles.AddRange(deviceCycles.Take(limit));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting charge cycles");
        }

        return cycles.Take(limit);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetKnownDevicesAsync(
        CancellationToken cancellationToken = default
    )
    {
        var devices = new HashSet<string>();

        try
        {
            // Get recent device statuses to find all devices with battery data
            var deviceStatuses = await _postgreSqlService.GetDeviceStatusAsync(
                count: 1000,
                cancellationToken: cancellationToken
            );

            foreach (var status in deviceStatuses)
            {
                if (
                    (status.Uploader?.Battery != null || status.Uploader?.BatteryVoltage != null)
                    && !string.IsNullOrEmpty(status.Device)
                )
                {
                    devices.Add(status.Device);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting known devices");
        }

        return devices;
    }

    private BatteryReading? ConvertToBatteryReading(DeviceStatus status)
    {
        if (status.Uploader?.Battery == null && status.Uploader?.BatteryVoltage == null)
        {
            return null;
        }

        var reading = new BatteryReading
        {
            Id = status.Id,
            Device = status.Device ?? "uploader",
            Battery = status.Uploader?.Battery,
            Voltage = status.Uploader?.BatteryVoltage,
            IsCharging = status.IsCharging ?? false,
            Temperature = status.Uploader?.Temperature,
            Mills = status.Mills,
            Timestamp = status.CreatedAt,
            Notification = GetNotificationStatus(status.Uploader?.Battery),
        };

        return reading;
    }

    private string? GetNotificationStatus(int? battery)
    {
        if (!battery.HasValue)
            return null;

        return battery.Value switch
        {
            <= DefaultUrgentThreshold => "urgent",
            <= DefaultWarnThreshold => "warn",
            _ => null,
        };
    }

    private static string ExtractDeviceName(string uri)
    {
        // Handle URIs like "openaps://phone" or "xdrip://device"
        if (uri.Contains("://"))
        {
            var parts = uri.Split("://");
            return parts.Length > 1 ? parts[1] : parts[0];
        }
        return uri;
    }

    private BatteryStatistics CalculateStatistics(
        string device,
        List<BatteryReading> readings,
        List<ChargeCycle> cycles,
        long periodStart,
        long periodEnd
    )
    {
        var stats = new BatteryStatistics
        {
            Device = device,
            DisplayName = ExtractDeviceName(device),
            PeriodStartMills = periodStart,
            PeriodEndMills = periodEnd,
            ReadingCount = readings.Count,
        };

        if (!readings.Any())
        {
            return stats;
        }

        // Basic statistics
        var batteryValues = readings
            .Where(r => r.Battery.HasValue)
            .Select(r => r.Battery!.Value)
            .ToList();

        if (batteryValues.Any())
        {
            stats.AverageLevel = batteryValues.Average();
            stats.MinLevel = batteryValues.Min();
            stats.MaxLevel = batteryValues.Max();
        }

        // Current status
        var latest = readings.OrderByDescending(r => r.Mills).FirstOrDefault();
        if (latest != null)
        {
            stats.CurrentLevel = latest.Battery;
            stats.IsCharging = latest.IsCharging;
            stats.LastReadingMills = latest.Mills;
        }

        // Charge cycle statistics
        var completeCycles = cycles.Where(c => c.DischargeDurationMinutes.HasValue).ToList();
        stats.ChargeCycleCount = completeCycles.Count;

        if (completeCycles.Any())
        {
            var dischargeDurations = completeCycles
                .Where(c => c.DischargeDurationMinutes.HasValue)
                .Select(c => c.DischargeDurationMinutes!.Value)
                .ToList();

            if (dischargeDurations.Any())
            {
                stats.AverageDischargeDurationMinutes = dischargeDurations.Average();
                stats.LongestDischargeDurationMinutes = dischargeDurations.Max();
                stats.ShortestDischargeDurationMinutes = dischargeDurations.Min();
            }

            var chargeDurations = completeCycles
                .Where(c => c.ChargeDurationMinutes.HasValue)
                .Select(c => c.ChargeDurationMinutes!.Value)
                .ToList();

            if (chargeDurations.Any())
            {
                stats.AverageChargeDurationMinutes = chargeDurations.Average();
            }
        }

        // Time in range - calculate based on readings
        if (batteryValues.Any())
        {
            var total = batteryValues.Count;
            stats.TimeAbove80Percent = (double)batteryValues.Count(v => v > 80) / total * 100;
            stats.TimeBetween30And80Percent =
                (double)batteryValues.Count(v => v >= 30 && v <= 80) / total * 100;
            stats.TimeBelow30Percent = (double)batteryValues.Count(v => v < 30) / total * 100;
            stats.TimeBelow20Percent = (double)batteryValues.Count(v => v < 20) / total * 100;
        }

        // Warning/urgent event counting
        stats.WarningEventCount = CountThresholdCrossings(readings, 30);
        stats.UrgentEventCount = CountThresholdCrossings(readings, 20);

        return stats;
    }

    private List<ChargeCycle> DetectChargeCycles(string device, List<BatteryReading> readings)
    {
        var cycles = new List<ChargeCycle>();

        if (readings.Count < 2)
            return cycles;

        ChargeCycle? currentCycle = null;
        bool wasCharging = false;

        foreach (var reading in readings)
        {
            if (reading.IsCharging && !wasCharging)
            {
                // Started charging - new cycle begins
                currentCycle = new ChargeCycle
                {
                    Id = Guid.NewGuid().ToString(),
                    Device = device,
                    ChargeStartMills = reading.Mills,
                    ChargeStartLevel = reading.Battery,
                };
            }
            else if (!reading.IsCharging && wasCharging && currentCycle != null)
            {
                // Stopped charging - end of charge phase
                currentCycle.ChargeEndMills = reading.Mills;
                currentCycle.ChargeEndLevel = reading.Battery;
                currentCycle.DischargeStartMills = reading.Mills;
                currentCycle.DischargeStartLevel = reading.Battery;
            }
            else if (
                reading.IsCharging
                && currentCycle != null
                && currentCycle.DischargeStartMills.HasValue
            )
            {
                // Started charging again - end of discharge phase
                currentCycle.DischargeEndMills = reading.Mills;
                currentCycle.DischargeEndLevel = reading.Battery;

                // Cycle is complete, add it
                cycles.Add(currentCycle);

                // Start a new cycle
                currentCycle = new ChargeCycle
                {
                    Id = Guid.NewGuid().ToString(),
                    Device = device,
                    ChargeStartMills = reading.Mills,
                    ChargeStartLevel = reading.Battery,
                };
            }

            wasCharging = reading.IsCharging;
        }

        return cycles;
    }

    private static int CountThresholdCrossings(List<BatteryReading> readings, int threshold)
    {
        var count = 0;
        bool wasAbove = true;

        foreach (var reading in readings.OrderBy(r => r.Mills))
        {
            if (reading.Battery.HasValue)
            {
                var isAbove = reading.Battery.Value >= threshold;
                if (wasAbove && !isAbove)
                {
                    count++;
                }
                wasAbove = isAbove;
            }
        }

        return count;
    }
}
