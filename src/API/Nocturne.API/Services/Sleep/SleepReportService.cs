using Microsoft.Extensions.Logging;
using Nocturne.Core.Contracts.Glucose;
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
/// Overnight TIR and hypo events use the clinical consensus bands, not the profile's
/// personal target, so they agree with every other report. Glucose is the canonical stream
/// (<see cref="ICanonicalGlucoseService.SelectAsync"/>), as the other reports read it, so a
/// reading posted by two uploaders counts once.
/// </remarks>
public class SleepReportService : ISleepReportService
{
    private readonly ISleepSessionRepository _sessions;
    private readonly ISensorGlucoseRepository _glucose;
    private readonly ICanonicalGlucoseService _canonicalGlucose;
    private readonly IPatientRecordRepository _patientRecord;
    private readonly ILogger<SleepReportService> _logger;

    public SleepReportService(
        ISleepSessionRepository sessions,
        ISensorGlucoseRepository glucose,
        ICanonicalGlucoseService canonicalGlucose,
        IPatientRecordRepository patientRecord,
        ILogger<SleepReportService> logger)
    {
        _sessions = sessions;
        _glucose  = glucose;
        _canonicalGlucose = canonicalGlucose;
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

    private async Task<IReadOnlyList<SensorGlucose>> ReadCanonicalGlucoseAsync(
        DateTime from, DateTime to, CancellationToken ct)
    {
        var raw = await _glucose.GetAsync(
            from:           from,
            to:             to,
            device:         null,
            source:         null,
            limit:          int.MaxValue,
            offset:         0,
            descending:     false,
            nativeOnly:     false,
            afterTimestamp: null,
            afterId:        null,
            ct:             ct);
        return await _canonicalGlucose.SelectAsync(raw.ToList(), ct);
    }

    private async Task<SleepSingleNightReport> BuildSingleNightReportAsync(
        SleepSession session,
        CancellationToken ct)
    {
        var glucoseReadings = await ReadCanonicalGlucoseAsync(session.StartTime, session.EndTime, ct);

        var thresholds = new GlycemicThresholds();
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

        var allGlucose = await ReadCanonicalGlucoseAsync(glucoseFrom, glucoseTo, ct);

        // Slice the (date-range-bounded) glucose set per night so each night's
        // computation scans only its own window, not every reading in the range.
        var thresholds = new GlycemicThresholds();
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
