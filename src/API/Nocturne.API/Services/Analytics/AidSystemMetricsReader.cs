using Nocturne.API.Services.Devices;
using Nocturne.Core.Clinical.Devices;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// The records <see cref="AidSystemMetrics"/> is assembled from, read for one window; the glucose
/// readings are the canonical ones (<see cref="ICanonicalGlucoseService"/>).
/// </summary>
public sealed record AidSystemMetricsRecords(
    IReadOnlyList<PatientDevice> Devices,
    IReadOnlyList<ApsLoopCycle> LoopCycles,
    IReadOnlyList<TempBasal> TempBasals,
    IReadOnlyList<DeviceEvent> DeviceEvents,
    IReadOnlyList<SensorGlucose> Glucose,
    double? TargetLow,
    double? TargetHigh);

/// <summary>
/// Reads and assembles the inputs of <see cref="IAidMetricsService"/>: the pumps' algorithm
/// segments, the APS loop cycles and temp basals, the site changes, the CGM active time and the
/// device names, for a reader that has already fetched them (<see cref="Calculate"/>) or one that
/// has not (<see cref="ReadAsync"/>).
/// </summary>
/// <remarks>
/// A registered device's dates are placed by <see cref="DeviceDateClock"/>: it starts at local
/// midnight of its first day and ends, exclusive, at local midnight after its last. With no zone
/// they are read as UTC days and the report says so in
/// <see cref="AidSystemMetrics.TimeZoneUnavailableReason"/>.
/// </remarks>
public sealed class AidSystemMetricsReader(
    IPatientDeviceRepository patientDeviceRepository,
    IApsSnapshotRepository apsSnapshotRepository,
    ITempBasalRepository tempBasalRepository,
    IDeviceEventRepository deviceEventRepository,
    ISensorGlucoseRepository sensorGlucoseRepository,
    ICanonicalGlucoseService canonicalGlucose,
    ITargetRangeScheduleRepository targetRangeScheduleRepository,
    IActiveProfileResolver activeProfileResolver,
    IStatisticsService statisticsService,
    IAidMetricsService aidMetricsService,
    DeviceDateClockResolver deviceDates,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Every collection is read uncapped, for the reason given on
    /// <c>StatisticsController.FetchInsulinRecordsAsync</c>. The target range is the active
    /// profile's now (<see cref="ActiveTargetRange"/>) and is left out if it cannot be read.
    /// </summary>
    public async Task<AidSystemMetrics> ReadAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var clock = await deviceDates.ResolveAsync(ct);
        var devices = await patientDeviceRepository.GetByDateRangeAsync(start, end, ct: ct);

        var apsTask = apsSnapshotRepository.GetLoopCyclesAsync(start, end, ct);
        var basalTask = tempBasalRepository.GetAsync(StatisticsService.TempBasalReadStart(start), end, null, null, int.MaxValue, descending: false, ct: ct);
        var eventTask = deviceEventRepository.GetAsync(start, end, null, null, int.MaxValue, descending: false, ct: ct);
        var glucoseTask = sensorGlucoseRepository.GetForAnalyticsAsync(start, end, null, null, int.MaxValue, descending: false, ct: ct);

        await Task.WhenAll(apsTask, basalTask, eventTask, glucoseTask);

        var glucose = await canonicalGlucose.SelectAsync((await glucoseTask).ToList(), ct);

        double? targetLow = null;
        double? targetHigh = null;
        try
        {
            var activeSchedule = await ActiveTargetRange.ScheduleAsync(activeProfileResolver, targetRangeScheduleRepository, timeProvider, ct);
            if (activeSchedule?.Entries.Count > 0)
            {
                targetLow = activeSchedule.Entries.Min(e => e.Low);
                targetHigh = activeSchedule.Entries.Max(e => e.High);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        var metrics = Calculate(
            new AidSystemMetricsRecords(
                [.. devices],
                [.. await apsTask],
                [.. await basalTask],
                [.. await eventTask],
                glucose,
                targetLow,
                targetHigh),
            start,
            end,
            clock);
        metrics.TimeZoneUnavailableReason = clock.Reason;
        return metrics;
    }

    public AidSystemMetrics Calculate(
        AidSystemMetricsRecords records, DateTime start, DateTime end, DeviceDateClock deviceDates)
    {
        var deviceSegments = records.Devices
            .Where(d => d.DeviceCategory == DeviceCategory.InsulinPump && d.AidAlgorithm.HasValue)
            .Select(d =>
            {
                var (from, to) = DeviceBounds(d, deviceDates);
                return new DeviceSegmentInput
                {
                    Algorithm = d.AidAlgorithm!.Value,
                    StartDate = from ?? start,
                    EndDate = to ?? end,
                };
            })
            .ToList();

        var siteChangeCount = records.DeviceEvents.Count(e => e.EventType == DeviceEventType.SiteChange);

        var cgmDevices = records.Devices.Where(d => d.DeviceCategory == DeviceCategory.CGM).ToList();
        var cgmDeviceNames = cgmDevices.Count > 0
            ? string.Join(", ", cgmDevices
                .Select(d => d.CatalogId != null ? DeviceCatalog.GetById(d.CatalogId)?.Name : null)
                .Where(n => n != null)
                .DefaultIfEmpty(cgmDevices.First().Model ?? cgmDevices.First().Manufacturer))
            : null;

        var pumpDevices = records.Devices.Where(d => d.DeviceCategory == DeviceCategory.InsulinPump).ToList();
        var pumpCatalogNames = pumpDevices
            .Select(d => d.CatalogId != null ? DeviceCatalog.GetById(d.CatalogId)?.Name : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();
        var pumpDeviceNames = pumpCatalogNames.Count > 0
            ? string.Join(", ", pumpCatalogNames)
            : pumpDevices
                .Select(d => !string.IsNullOrWhiteSpace(d.Model) ? d.Model : d.Manufacturer)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        var cgmActivePercent = statisticsService.CalculateCgmActivePercent(
            records.Glucose,
            start,
            end,
            cgmDevices
                .Select(d =>
                {
                    var (from, to) = DeviceBounds(d, deviceDates);
                    return new CgmDeviceWindow(
                        d.Id,
                        from,
                        to,
                        d.CatalogId != null ? DeviceCatalog.GetById(d.CatalogId)?.Cgm?.UpdateIntervalMinutes : null);
                })
                .ToList());

        return aidMetricsService.Calculate(
            deviceSegments,
            records.LoopCycles,
            records.TempBasals,
            siteChangeCount,
            cgmDeviceNames,
            pumpDeviceNames,
            cgmActivePercent,
            records.TargetLow,
            records.TargetHigh,
            start,
            end);
    }

    private static (DateTime? From, DateTime? To) DeviceBounds(PatientDevice device, DeviceDateClock deviceDates)
    {
        var window = deviceDates.Window(device.StartDate, device.EndDate, openEnd: 0);
        return (
            device.StartDate.HasValue ? Utc(window.Start) : null,
            device.EndDate.HasValue ? Utc(window.End) : null);
    }

    private static DateTime Utc(long mills) => DateTimeOffset.FromUnixTimeMilliseconds(mills).UtcDateTime;
}
