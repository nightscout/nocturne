using Nocturne.API.Services.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Devices;

/// <summary>
/// A reservoir level resolved from stored pump snapshots, depleted by insulin
/// delivered since the underlying reading.
/// </summary>
/// <param name="Units">Remaining insulin in units.</param>
/// <param name="IsLowerBound">True when the value only proves the reservoir holds at
/// least this much (capped reading like "50+", or a bound overriding a lower estimate).</param>
/// <param name="IsEstimated">True when the value was projected from an older reading
/// rather than taken directly from the newest snapshot.</param>
public sealed record ReservoirEstimate(decimal Units, bool IsLowerBound, bool IsEstimated);

/// <summary>
/// Resolves the current reservoir level from pump snapshots, estimating between
/// exact readings.
/// </summary>
public interface IReservoirEstimationService
{
    /// <summary>
    /// Resolve the reservoir level as of <paramref name="asOf"/> (null = now).
    /// Returns null when no snapshot carries a reservoir value.
    /// </summary>
    Task<ReservoirEstimate?> GetEstimateAsync(DateTime? asOf, CancellationToken ct = default);
}

/// <summary>
/// Anchors on the newest exact reservoir reading (an uploaded level or a manual
/// reservoir report) and subtracts insulin delivered since: boluses plus temp-basal
/// segments (clipped per <see cref="StatisticsService.ClipOverlappingTempBasals"/>),
/// falling back to scheduled basal segments when the window has no temp basals or
/// algorithm boluses (non-AID pumps). A newer capped reading ("50+") acts as a floor:
/// the true level is at least the bound, so a lower estimate is raised to it and
/// flagged as a lower bound.
/// </summary>
/// <remarks>
/// Pumps that report exact levels continuously (e.g. Omnipod Dash below 50 units)
/// anchor on their newest snapshot with nothing delivered since, so estimation
/// degrades to the raw reading. Anchors older than <see cref="MaxAnchorAge"/> are
/// ignored — depletion drift over longer windows makes the projection meaningless.
/// </remarks>
internal sealed class ReservoirEstimationService(
    IPumpSnapshotRepository pumpSnapshots,
    IBolusRepository boluses,
    ITempBasalRepository tempBasals,
    IBasalSegmentService basalSegments,
    TimeProvider timeProvider,
    ILogger<ReservoirEstimationService> logger) : IReservoirEstimationService
{
    private static readonly TimeSpan MaxAnchorAge = TimeSpan.FromDays(14);

    /// <summary>Snapshots scanned for an exact anchor; Ypso-style pumps never carry a
    /// level, so the scan must skip arbitrarily many level-less snapshots cheaply.</summary>
    private const int SnapshotScanLimit = 200;

    private const int RecordFetchLimit = 5000;

    /// <summary>Deliveries below this are noise (rounding of clipped segments), not
    /// evidence that the value diverged from the anchor reading.</summary>
    private const double EstimationEpsilon = 0.05;

    public async Task<ReservoirEstimate?> GetEstimateAsync(DateTime? asOf, CancellationToken ct = default)
    {
        var now = asOf ?? timeProvider.GetUtcNow().UtcDateTime;
        var windowStart = now - MaxAnchorAge;

        var snapshots = (await pumpSnapshots.GetAsync(
            from: windowStart, to: asOf, device: null, source: null,
            limit: SnapshotScanLimit, offset: 0, descending: true, ct: ct)).ToList();
        if (snapshots.Count == SnapshotScanLimit)
        {
            logger.LogWarning(
                "Pump snapshot fetch returned exactly the scan limit ({Limit}) for window {From:o}..{To:o}; " +
                "older snapshots were likely truncated and the reservoir estimate may be skewed",
                SnapshotScanLimit, windowStart, now);
        }

        PumpSnapshot? anchor = null;
        PumpSnapshot? newestBound = null;
        foreach (var snapshot in snapshots.Where(s => s.Reservoir is not null))
        {
            if (ReservoirDisplayFormat.IsLowerBound(snapshot.ReservoirDisplay))
            {
                newestBound ??= snapshot;
                continue;
            }
            anchor = snapshot;
            break;
        }

        if (anchor is null)
        {
            return newestBound is null
                ? null
                : new ReservoirEstimate((decimal)newestBound.Reservoir!.Value, IsLowerBound: true, IsEstimated: false);
        }

        var delivered = await GetInsulinDeliveredAsync(anchor.Timestamp, now, ct);
        var estimate = Math.Max(anchor.Reservoir!.Value - delivered, 0);

        // A capped reading newer than the anchor proves the level is still at least
        // the bound; an estimate below it is stale bolus/basal accounting, not signal.
        if (newestBound is { } bound && bound.Timestamp > anchor.Timestamp && bound.Reservoir!.Value > estimate)
            return new ReservoirEstimate((decimal)bound.Reservoir.Value, IsLowerBound: true, IsEstimated: false);

        return new ReservoirEstimate(
            Math.Round((decimal)estimate, 1),
            IsLowerBound: false,
            IsEstimated: delivered >= EstimationEpsilon);
    }

    /// <summary>How far before the window a temp basal may start and still deliver into
    /// it; fetches reach back this far so straddling records aren't missed.</summary>
    private static readonly TimeSpan TempBasalStraddleLookback = TimeSpan.FromHours(24);

    private async Task<double> GetInsulinDeliveredAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        // The anchor reading already reflects a bolus stamped at the same instant
        // (uploaders write device status after treatments), so the window opens
        // just past it.
        var bolusRecords = (await boluses.GetAsync(
            from: from.AddMilliseconds(1), to: to, device: null, source: null,
            limit: RecordFetchLimit, offset: 0, descending: false, ct: ct)).ToList();
        if (bolusRecords.Count == RecordFetchLimit)
        {
            logger.LogWarning(
                "Bolus fetch returned exactly the fetch limit ({Limit}) for window {From:o}..{To:o}; " +
                "records were likely truncated and the reservoir estimate may be skewed",
                RecordFetchLimit, from, to);
        }
        var tempBasalRecords = (await tempBasals.GetAsync(
            from: from - TempBasalStraddleLookback, to: to, device: null, source: null,
            limit: RecordFetchLimit, offset: 0, descending: false, ct: ct)).ToList();
        if (tempBasalRecords.Count == RecordFetchLimit)
        {
            logger.LogWarning(
                "Temp basal fetch returned exactly the fetch limit ({Limit}) for window {From:o}..{To:o}; " +
                "records were likely truncated and the reservoir estimate may be skewed",
                RecordFetchLimit, from - TempBasalStraddleLookback, to);
        }

        var total = bolusRecords.Sum(b => b.Insulin);

        var fromMs = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

        // Count only the delivery that falls inside [from, to]: records fetched from
        // the lookback may straddle the window start, and the newest record's declared
        // end usually lies in the future — insulin not yet delivered.
        var anyTempBasalDelivery = false;
        foreach (var (tb, effectiveEndMills) in StatisticsService.ClipOverlappingTempBasals(tempBasalRecords))
        {
            var start = Math.Max(tb.StartMills, fromMs);
            var end = Math.Min(effectiveEndMills, toMs);
            if (end <= start)
                continue;
            anyTempBasalDelivery = true;
            total += tb.Rate * ((end - start) / (1000.0 * 60 * 60));
        }

        // No temp basal delivery and no algorithm boluses means basal isn't recorded
        // as discrete deliveries (non-AID pump) — count the scheduled profile instead.
        if (!anyTempBasalDelivery && !bolusRecords.Any(b => b.Kind == BolusKind.Algorithm))
        {
            total += await basalSegments.GetSegmentsAsync(fromMs, toMs, ct).SumUnitsAsync(ct);
        }

        return total;
    }
}
