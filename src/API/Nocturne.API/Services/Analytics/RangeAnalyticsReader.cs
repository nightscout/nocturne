using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Clinical.Statistics;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>A window's <see cref="ReportAnalysisResult"/> and its insulin delivery figures.</summary>
public sealed record RangeReads(ReportAnalysisResult Analytics, InsulinDeliveryStatistics InsulinDelivery);

/// <summary>
/// Reads the glucose, manual boluses, carb intakes and patient devices of a window and runs
/// <see cref="IStatisticsService.AnalyzeGlucoseDataExtended"/> and
/// <see cref="IStatisticsService.CalculateAveragedStats"/> over them.
/// </summary>
public sealed class RangeAnalyticsReader(
    ISensorGlucoseRepository sensorGlucoseRepository,
    IBolusRepository bolusRepository,
    ICarbIntakeRepository carbIntakeRepository,
    IPatientDeviceRepository patientDeviceRepository,
    ICanonicalGlucoseService canonicalGlucose,
    IStatisticsService statisticsService,
    TenantClockResolver tenantClock,
    IActiveProfileResolver activeProfileResolver,
    ITargetRangeScheduleRepository targetRangeScheduleRepository,
    InsulinDeliveryReader insulinDelivery,
    TimeProvider timeProvider)
{
    public Task<ReportAnalysisResult> ReadAsync(
        DateTime startDate,
        DateTime endDate,
        DiabetesPopulation population,
        Guid? patientDeviceId,
        CancellationToken ct) =>
        ReadCoreAsync(startDate, endDate, population, patientDeviceId, treatments: null, ct);

    /// <summary>
    /// The window's analytics and insulin delivery, which both need its manual boluses and carbs:
    /// those are read once and shared.
    /// </summary>
    public async Task<RangeReads> ReadWithInsulinDeliveryAsync(
        DateTime startDate, DateTime endDate, DiabetesPopulation population, CancellationToken ct)
    {
        var treatments = TreatmentRangeReads.Start(
            bolusRepository,
            carbIntakeRepository,
            DateTime.SpecifyKind(startDate, DateTimeKind.Utc),
            DateTime.SpecifyKind(endDate, DateTimeKind.Utc),
            ct);

        var analytics = ReadCoreAsync(startDate, endDate, population, patientDeviceId: null, treatments, ct);
        var delivery = insulinDelivery.ReadAsync(startDate, endDate, ct, treatments);

        await Task.WhenAll(analytics, delivery);

        return new RangeReads(await analytics, (await delivery).Statistics);
    }

    private async Task<ReportAnalysisResult> ReadCoreAsync(
        DateTime startDate,
        DateTime endDate,
        DiabetesPopulation population,
        Guid? patientDeviceId,
        TreatmentRangeReads? treatments,
        CancellationToken ct)
    {
        var startDt = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
        var endDt = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

        treatments ??= TreatmentRangeReads.Start(bolusRepository, carbIntakeRepository, startDt, endDt, ct);

        // int.MaxValue limit mirrors ActogramReportService so dense tenants are never
        // silently truncated (the cause of skewed report stats on high-frequency uploads).
        var glucoseTask = sensorGlucoseRepository.GetForAnalyticsAsync(startDt, endDt, null, null, int.MaxValue, descending: false, patientDeviceId: patientDeviceId, ct: ct);
        var devicesTask = patientDeviceRepository.GetByDateRangeAsync(startDt, endDt, ct: ct);

        await Task.WhenAll(glucoseTask, treatments.ManualBoluses, treatments.Carbs, devicesTask);

        var rawGlucose = (await glucoseTask).ToList();

        // Unfiltered statistics compute over the canonical stream, never blended CGMs.
        // A patientDeviceId filter already restricts the fetch to one device raw, so there
        // is nothing left to canonicalize/blend.
        var entries = patientDeviceId is null
            ? (await canonicalGlucose.SelectAsync(rawGlucose, ct)).ToList()
            : rawGlucose;
        var boluses = await treatments.ManualBoluses;
        var carbs = await treatments.Carbs;
        var devices = await devicesTask;

        var clock = await tenantClock.ResolveAsync(ct);

        return new ReportAnalysisResult
        {
            Analysis = clock.Annotate(
                statisticsService.AnalyzeGlucoseDataExtended(
                    entries,
                    boluses,
                    carbs,
                    StatisticsService.WindowDays(startDt, endDt),
                    population,
                    timeZone: clock.Zone
                ),
                StatisticsService.WindowDays(startDt, endDt, clock.Zone)),
            AveragedStats = statisticsService.CalculateAveragedStats(entries, clock.Zone).ToList(),
            HourlyBandThresholds = statisticsService.HourlyBandThresholds,
            ContributingDevices = statisticsService.CalculateContributingDevices(rawGlucose, devices).ToList(),
            PersonalRange = await CalculatePersonalRangeAsync(entries, clock.Zone, ct),
            TimeZoneUnavailableReason = clock.Reason,
        };
    }

    /// <summary>
    /// Computes time in the tenant's personal target range for the report window. Failure-isolated:
    /// the personal range is optional garnish on the report, so a missing schedule or a failed
    /// fetch returns null rather than failing the base analytics.
    /// </summary>
    private async Task<PersonalRangeTimeInRange?> CalculatePersonalRangeAsync(
        List<SensorGlucose> entries,
        TimeZoneInfo? tenantTimeZone,
        CancellationToken ct
    )
    {
        try
        {
            var schedule = await ActiveTargetRange.ScheduleAsync(activeProfileResolver, targetRangeScheduleRepository, timeProvider, ct);
            if (schedule is null || schedule.Entries.Count == 0)
                return null;

            return statisticsService.CalculatePersonalRangeTime(entries, schedule.Entries, tenantTimeZone);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
