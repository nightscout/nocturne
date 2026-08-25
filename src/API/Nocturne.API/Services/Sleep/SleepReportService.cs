using Microsoft.Extensions.Logging;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Sleep.Report;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Sleep;

/// <summary>
/// Orchestrates sleep report data by combining session records with CGM readings
/// and delegating all computation to <see cref="SleepReportCalculator"/>.
/// </summary>
/// <remarks>
/// Glycemic thresholds mirror <c>ProfileLoadStage</c>: very-low (54 mg/dL) and
/// very-high (250 mg/dL) are fixed; low/target-bottom and high/target-top come from
/// the active profile's target range, falling back to the consensus in-range band
/// when no therapy settings exist.
/// </remarks>
public class SleepReportService : ISleepReportService
{
    private const double DefaultVeryLow  = 54;
    private const double DefaultLow      = GlucoseConstants.TargetBottomMgdl;
    private const double DefaultHigh     = GlucoseConstants.TargetTopMgdl;
    private const double DefaultVeryHigh = 250;

    private readonly ISleepSessionRepository _sessions;
    private readonly ISensorGlucoseRepository _glucose;
    private readonly ITherapySettingsResolver _therapySettingsResolver;
    private readonly ITargetRangeResolver _targetRangeResolver;
    private readonly IPatientRecordRepository _patientRecord;
    private readonly ILogger<SleepReportService> _logger;

    public SleepReportService(
        ISleepSessionRepository sessions,
        ISensorGlucoseRepository glucose,
        ITherapySettingsResolver therapySettingsResolver,
        ITargetRangeResolver targetRangeResolver,
        IPatientRecordRepository patientRecord,
        ILogger<SleepReportService> logger)
    {
        _sessions = sessions;
        _glucose  = glucose;
        _therapySettingsResolver = therapySettingsResolver;
        _targetRangeResolver     = targetRangeResolver;
        _patientRecord = patientRecord;
        _logger   = logger;
    }

    /// <summary>
    /// Resolves the normative stage reference ranges for the current tenant's patient, using their
    /// date of birth (age) and biological sex. Falls back to adult-female norms when the record is
    /// absent or those fields are unset.
    /// </summary>
    private async Task<SleepStageReferenceRangeSet> ResolveReferenceRangesAsync(CancellationToken ct)
    {
        var record = await _patientRecord.GetAsync(ct);
        var age = record?.DateOfBirth is { } dob ? AgeInYears(dob) : (int?)null;
        return SleepStageReferenceRangeSet.Resolve(age, record?.Sex);
    }

    /// <summary>Completed years between <paramref name="dob"/> and today (UTC).</summary>
    private static int AgeInYears(DateOnly dob)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return age;
    }

    /// <summary>
    /// Resolves glycemic thresholds at <paramref name="timeMills"/> the same way
    /// <c>ProfileLoadStage</c> does: very-low/very-high are fixed; low and high come
    /// from the active profile's target range, falling back to the consensus in-range
    /// band when no therapy settings exist for the tenant.
    /// </summary>
    private async Task<GlycemicThresholds> ResolveThresholdsAsync(long timeMills, CancellationToken ct)
    {
        if (!await _therapySettingsResolver.HasDataAsync(ct))
        {
            return new GlycemicThresholds
            {
                VeryLow      = DefaultVeryLow,
                Low          = DefaultLow,
                TargetBottom = DefaultLow,
                High         = DefaultHigh,
                TargetTop    = DefaultHigh,
                VeryHigh     = DefaultVeryHigh,
            };
        }

        var low  = await _targetRangeResolver.GetLowBGTargetAsync(timeMills, ct: ct);
        var high = await _targetRangeResolver.GetHighBGTargetAsync(timeMills, ct: ct);
        return new GlycemicThresholds
        {
            VeryLow      = DefaultVeryLow,
            Low          = low,
            TargetBottom = low,
            High         = high,
            TargetTop    = high,
            VeryHigh     = DefaultVeryHigh,
        };
    }

    /// <inheritdoc/>
    public async Task<SleepSingleNightReport?> GetSingleNightReportAsync(
        Guid sessionId,
        CancellationToken ct = default)
    {
        var session = await _sessions.GetSessionByIdAsync(sessionId, ct);
        if (session is null)
            return null;

        return await BuildSingleNightReportAsync(session, ct);
    }

    /// <inheritdoc/>
    public async Task<SleepSingleNightReport?> GetSingleNightReportByDateAsync(
        DateOnly displayDate,
        CancellationToken ct = default)
    {
        // A night displayed on `displayDate` starts (in its own timezone) between
        // noon that day and noon the next — so its UTC StartTime can land anywhere
        // from the previous day to two days out once timezone offsets are applied.
        // Query that padded window, then match on the same noon-rule night key the
        // trends report buckets by.
        // Kind=Utc so Npgsql accepts the bounds against timestamptz (same normalization
        // GetTrends applies); the ±1–2 day padding absorbs any timezone offset when the
        // session's own timezone shifts its night key.
        var midnight   = DateTime.SpecifyKind(displayDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var sessions = await _sessions.GetSessionsAsync(
            from:              midnight.AddDays(-1),
            to:                midnight.AddDays(2),
            type:              null,
            source:            null,
            limit:             int.MaxValue,
            offset:            0,
            descending:        false,
            includeStages:     true,
            cancellationToken: ct);

        var session = SleepReportCalculator.DeduplicateToOnePerNight(sessions)
            .FirstOrDefault(s => SleepReportCalculator.NightDate(s) == displayDate);
        if (session is null)
            return null;

        return await BuildSingleNightReportAsync(session, ct);
    }

    private async Task<SleepSingleNightReport> BuildSingleNightReportAsync(
        SleepSession session,
        CancellationToken ct)
    {
        var glucoseReadings = await _glucose.GetAsync(
            from:           session.StartTime,
            to:             session.EndTime,
            device:         null,
            source:         null,
            limit:          int.MaxValue,
            offset:         0,
            descending:     false,
            nativeOnly:     false,
            afterTimestamp: null,
            afterId:        null,
            ct:             ct);

        var thresholds = await ResolveThresholdsAsync(session.EndMills, ct);
        var stages    = session.Stages ?? [];
        var breakdown = SleepReportCalculator.ComputeStageBreakdown(session);
        breakdown.ReferenceRanges = await ResolveReferenceRangesAsync(ct);
        var tir       = SleepReportCalculator.ComputeOvernightTir(session, glucoseReadings, thresholds);
        var hypos     = SleepReportCalculator.ComputeHypoEvents(session, glucoseReadings, stages, thresholds);
        var dawn      = SleepReportCalculator.ComputeDawnPhenomenon(session, glucoseReadings);
        var wakeEvents = SleepReportCalculator.ComputeWakeEvents(session, stages, glucoseReadings);
        var (score, scoreSource) = SleepReportCalculator.ResolveScore(session, hypos.Count, breakdown);

        return new SleepSingleNightReport
        {
            Session        = session,
            Score          = score,
            ScoreSource    = scoreSource ?? SleepScoreSource.Computed,
            StageBreakdown = breakdown,
            OvernightTir   = tir,
            HypoEvents     = hypos,
            DawnPhenomenon = dawn,
            WakeEvents     = wakeEvents,
        };
    }

    /// <inheritdoc/>
    public async Task<SleepTrendsReport> GetTrendsReportAsync(
        DateTime from,
        DateTime to,
        SleepSource? source = null,
        CancellationToken ct = default)
    {
        var allSessions = await _sessions.GetSessionsAsync(
            from:              from,
            to:                to,
            type:              null,
            source:            source,
            limit:             int.MaxValue,
            offset:            0,
            descending:        false,
            cancellationToken: ct);

        var daysInRange = (int)(to.Date - from.Date).TotalDays + 1;
        var referenceRanges = await ResolveReferenceRangesAsync(ct);

        if (!allSessions.Any())
            return new SleepTrendsReport
            {
                Summary = SleepReportCalculator.ComputeTrendsSummary([], daysInRange, referenceRanges),
            };

        IReadOnlyList<SleepSession> sessions = source is null
            ? SleepReportCalculator.DeduplicateToOnePerNight(allSessions)
            : (IReadOnlyList<SleepSession>)allSessions.ToList();

        var glucoseFrom = sessions.Min(s => s.StartTime);
        var glucoseTo   = sessions.Max(s => s.EndTime);

        var allGlucose = await _glucose.GetAsync(
            from:           glucoseFrom,
            to:             glucoseTo,
            device:         null,
            source:         null,
            limit:          int.MaxValue,
            offset:         0,
            descending:     false,
            nativeOnly:     false,
            afterTimestamp: null,
            afterId:        null,
            ct:             ct);

        // Slice the (date-range-bounded) glucose set per night so each night's
        // computation scans only its own window, not every reading in the range.
        var thresholds = await ResolveThresholdsAsync(new DateTimeOffset(glucoseTo, TimeSpan.Zero).ToUnixTimeMilliseconds(), ct);
        var nights = sessions
            .Select(s =>
            {
                var nightGlucose = allGlucose
                    .Where(g => g.Timestamp >= s.StartTime && g.Timestamp <= s.EndTime)
                    .ToList();
                return SleepReportCalculator.ComputeNightSummary(s, nightGlucose, thresholds);
            })
            .ToList();
        var summary = SleepReportCalculator.ComputeTrendsSummary(nights, daysInRange, referenceRanges);
        var weeks   = SleepReportCalculator.ComputeWeekSummaries(nights, from, to);

        return new SleepTrendsReport
        {
            Nights  = nights,
            Weeks   = weeks,
            Summary = summary,
        };
    }
}
