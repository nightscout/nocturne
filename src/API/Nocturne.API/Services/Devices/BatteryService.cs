using Nocturne.Core.Contracts.Devices;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Battery;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// Tracks and analyses device battery status from recent <see cref="UploaderSnapshot"/>
/// and <see cref="PumpSnapshot"/> records. Categorises battery levels using configurable
/// warning and urgent thresholds.
/// </summary>
/// <seealso cref="IBatteryService"/>
public class BatteryService : IBatteryService
{
    private readonly IUploaderSnapshotRepository _uploaderSnapshots;
    private readonly IPumpSnapshotRepository _pumpSnapshots;
    private readonly ILogger<BatteryService> _logger;

    private const int DefaultWarnThreshold = 30;
    private const int DefaultUrgentThreshold = 20;

    public BatteryService(
        IUploaderSnapshotRepository uploaderSnapshots,
        IPumpSnapshotRepository pumpSnapshots,
        ILogger<BatteryService> logger)
    {
        _uploaderSnapshots = uploaderSnapshots;
        _pumpSnapshots = pumpSnapshots;
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
            var from = DateTime.UtcNow.AddMinutes(-recentMinutes);

            var uploaderSnapshots = (await _uploaderSnapshots.GetAsync(
                from: from, to: null, device: null, source: null,
                limit: 100, offset: 0, descending: true, ct: cancellationToken)).ToList();

            var pumpSnapshots = (await _pumpSnapshots.GetAsync(
                from: from, to: null, device: null, source: null,
                limit: 100, offset: 0, descending: true, ct: cancellationToken)).ToList();

            var allReadings = new List<BatteryReading>();
            allReadings.AddRange(uploaderSnapshots
                .Where(u => u.Battery != null || u.BatteryVoltage != null)
                .Select(ConvertUploaderToReading));
            allReadings.AddRange(pumpSnapshots
                .Where(p => p.BatteryPercent != null || p.BatteryVoltage != null)
                .Select(ConvertPumpToReading));

            if (allReadings.Count == 0)
            {
                return result;
            }

            // Group by device, extracting battery readings from all available sources
            foreach (var reading in allReadings)
            {
                var deviceUri = reading.Device;
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

                result.Devices[deviceUri].Statuses.Add(reading);
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
            var from = fromMills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(fromMills.Value).UtcDateTime
                : (DateTime?)null;
            var to = toMills.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(toMills.Value).UtcDateTime
                : (DateTime?)null;

            var uploaderSnapshots = await _uploaderSnapshots.GetAsync(
                from: from, to: to, device: device, source: null,
                limit: 10000, offset: 0, descending: false, ct: cancellationToken);

            var pumpSnapshots = await _pumpSnapshots.GetAsync(
                from: from, to: to, device: device, source: null,
                limit: 10000, offset: 0, descending: false, ct: cancellationToken);

            readings.AddRange(uploaderSnapshots.Select(ConvertUploaderToReading));
            readings.AddRange(pumpSnapshots.Select(ConvertPumpToReading));

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
            var uploaderSnapshots = await _uploaderSnapshots.GetAsync(
                from: null, to: null, device: null, source: null,
                limit: 1000, offset: 0, descending: true, ct: cancellationToken);

            var pumpSnapshots = await _pumpSnapshots.GetAsync(
                from: null, to: null, device: null, source: null,
                limit: 1000, offset: 0, descending: true, ct: cancellationToken);

            foreach (var snapshot in uploaderSnapshots)
            {
                var deviceName = snapshot.Device ?? "uploader";
                if (!string.IsNullOrEmpty(deviceName))
                {
                    devices.Add(deviceName);
                }
            }

            foreach (var snapshot in pumpSnapshots)
            {
                var deviceName = snapshot.Device ?? "pump";
                var pumpDevice = deviceName.Contains("pump", StringComparison.OrdinalIgnoreCase)
                    ? deviceName
                    : $"{deviceName}/pump";
                if (!string.IsNullOrEmpty(pumpDevice))
                {
                    devices.Add(pumpDevice);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting known devices");
        }

        return devices;
    }

    /// <summary>
    /// Converts an <see cref="UploaderSnapshot"/> to a <see cref="BatteryReading"/>.
    /// </summary>
    private BatteryReading ConvertUploaderToReading(UploaderSnapshot snapshot)
    {
        return new BatteryReading
        {
            Id = snapshot.Id.ToString(),
            Device = snapshot.Device ?? "uploader",
            Battery = snapshot.Battery,
            Voltage = snapshot.BatteryVoltage,
            IsCharging = snapshot.IsCharging ?? false,
            Temperature = snapshot.Temperature,
            Mills = snapshot.Mills,
            Timestamp = snapshot.CreatedAt.ToString("o"),
            Notification = GetNotificationStatus(snapshot.Battery),
        };
    }

    /// <summary>
    /// Converts a <see cref="PumpSnapshot"/> to a <see cref="BatteryReading"/>.
    /// </summary>
    private BatteryReading ConvertPumpToReading(PumpSnapshot snapshot)
    {
        var deviceName = snapshot.Device ?? "pump";
        var pumpDevice = deviceName.Contains("pump", StringComparison.OrdinalIgnoreCase)
            ? deviceName
            : $"{deviceName}/pump";

        return new BatteryReading
        {
            Id = $"{snapshot.Id}_pump",
            Device = pumpDevice,
            Battery = snapshot.BatteryPercent,
            Voltage = snapshot.BatteryVoltage,
            IsCharging = false, // Pumps typically don't have charging status
            Mills = snapshot.Mills,
            Timestamp = snapshot.CreatedAt.ToString("o"),
            Notification = GetNotificationStatus(snapshot.BatteryPercent),
        };
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
