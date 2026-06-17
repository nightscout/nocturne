using Nocturne.API.Controllers.V4.Analytics;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;

namespace Nocturne.API.Services.Glucose;

/// <summary>
/// Builds the pre-resolved therapy profile that the Prelude (Android) device runs its
/// on-device <c>oref</c> prediction engine against: the next 24h of therapy flattened into
/// contiguous, absolute-time segments where every emitted scalar is constant.
/// </summary>
/// <remarks>
/// This mirrors <see cref="IPredictionService"/>'s per-instant oref profile
/// (<c>PredictionService.GetProfileAsync</c>) but resolved across the whole window rather than
/// at a single anchor. It reuses existing machinery only — <see cref="ITherapyTimelineResolver"/>
/// for profile/CCP segmentation, the snapshot evaluators for schedule lookup, and
/// <see cref="ITargetRangeResolver"/> for targets — and introduces no new resolution logic.
/// </remarks>
public interface IProfileSnapshotService
{
    /// <summary>
    /// Builds the snapshot covering <c>[now, now + 24h)</c>.
    /// </summary>
    /// <param name="profileId">Optional explicit profile name. The device omits it; when null the
    /// active profile is resolved (and honors profile switches inside the window).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProfileSnapshotResponse> BuildAsync(string? profileId, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ProfileSnapshotService : IProfileSnapshotService
{
    private const long WindowMillis = 24L * 60 * 60 * 1000;
    private const long StepMillis = 60_000;

    private const double DefaultDia = 3.0;
    private const double DefaultBasal = 1.0;
    private const double DefaultSens = 50.0;
    private const double DefaultCarbRatio = 10.0;
    private const double DefaultMinBg = 100.0;
    private const double DefaultMaxBg = 120.0;
    private const int DefaultPeak = 75;
    private const string DefaultCurve = "rapid-acting";

    private readonly ITherapyTimelineResolver _timeline;
    private readonly ITargetRangeResolver _targetRange;
    private readonly ITherapySettingsResolver _therapySettings;
    private readonly IPatientInsulinRepository _insulins;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProfileSnapshotService> _logger;

    public ProfileSnapshotService(
        ITherapyTimelineResolver timeline,
        ITargetRangeResolver targetRange,
        ITherapySettingsResolver therapySettings,
        IPatientInsulinRepository insulins,
        TimeProvider timeProvider,
        ILogger<ProfileSnapshotService> logger)
    {
        _timeline = timeline;
        _targetRange = targetRange;
        _therapySettings = therapySettings;
        _insulins = insulins;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<ProfileSnapshotResponse> BuildAsync(string? profileId, CancellationToken ct = default)
    {
        var nowMills = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowEnd = nowMills + WindowMillis;

        var bolus = await _insulins.GetPrimaryBolusInsulinAsync(ct);
        var peak = bolus?.Peak ?? DefaultPeak;
        var curve = bolus?.Curve ?? DefaultCurve;

        var response = new ProfileSnapshotResponse { FetchedAtMills = nowMills };

        if (!await _therapySettings.HasDataAsync(ct))
        {
            response.Segments.Add(new ProfileSnapshotSegment
            {
                StartMills = nowMills,
                EndMills = windowEnd,
                Dia = bolus?.Dia ?? DefaultDia,
                Basal = DefaultBasal,
                Sens = DefaultSens,
                CarbRatio = DefaultCarbRatio,
                MinBg = DefaultMinBg,
                MaxBg = DefaultMaxBg,
                Peak = peak,
                Curve = curve,
            });
            return response;
        }

        var timeline = await _timeline.BuildAsync(nowMills, windowEnd, profileId, ct);

        foreach (var segment in timeline.Segments)
        {
            var snap = segment.Snapshot;
            var targetCache = new Dictionary<int, (double Low, double High)>();

            var t = segment.StartMills;
            while (t < segment.EndMills)
            {
                var scalars = await ScalarsAtAsync(snap, t, profileId, targetCache, ct);

                var tNext = segment.EndMills;
                for (var c = t + StepMillis; c < segment.EndMills; c += StepMillis)
                {
                    var probe = await ScalarsAtAsync(snap, c, profileId, targetCache, ct);
                    if (!probe.Equals(scalars))
                    {
                        tNext = c;
                        break;
                    }
                }

                response.Segments.Add(new ProfileSnapshotSegment
                {
                    StartMills = t,
                    EndMills = tNext,
                    Dia = snap.Dia,
                    Basal = scalars.Basal,
                    Sens = scalars.Sens,
                    CarbRatio = scalars.CarbRatio,
                    MinBg = scalars.MinBg,
                    MaxBg = scalars.MaxBg,
                    Peak = peak,
                    Curve = curve,
                });

                t = tNext;
            }
        }

        EnsureContiguous(response.Segments, nowMills, windowEnd);
        return response;
    }

    private readonly record struct Scalars(double Sens, double CarbRatio, double Basal, double MinBg, double MaxBg);

    private async Task<Scalars> ScalarsAtAsync(
        TherapySnapshot snap,
        long t,
        string? profileId,
        Dictionary<int, (double Low, double High)> targetCache,
        CancellationToken ct)
    {
        var bucket = LocalSecondsOfDay(snap.Timezone, t + snap.CcpTimeshiftMs);
        if (!targetCache.TryGetValue(bucket, out var target))
        {
            var low = await _targetRange.GetLowBGTargetAsync(t, profileId, ct);
            var high = await _targetRange.GetHighBGTargetAsync(t, profileId, ct);
            target = (low, high);
            targetCache[bucket] = target;
        }

        return new Scalars(
            snap.SensitivityAt(t),
            snap.CarbRatioAt(t),
            snap.BasalRateAt(t),
            target.Low,
            target.High);
    }

    private static int LocalSecondsOfDay(TimeZoneInfo? tz, long mills)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(mills);
        if (tz is not null)
            dto = TimeZoneInfo.ConvertTime(dto, tz);
        return (int)dto.TimeOfDay.TotalSeconds;
    }

    private static void EnsureContiguous(IReadOnlyList<ProfileSnapshotSegment> segments, long start, long end)
    {
        if (segments.Count == 0)
            throw new InvalidOperationException("Profile snapshot produced no segments.");
        if (segments[0].StartMills != start)
            throw new InvalidOperationException(
                $"Profile snapshot must start at {start}, but started at {segments[0].StartMills}.");
        if (segments[^1].EndMills != end)
            throw new InvalidOperationException(
                $"Profile snapshot must end at {end}, but ended at {segments[^1].EndMills}.");

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].EndMills <= segments[i].StartMills)
                throw new InvalidOperationException(
                    $"Profile snapshot segment {i} is non-positive width ({segments[i].StartMills}..{segments[i].EndMills}).");
            if (i > 0 && segments[i].StartMills != segments[i - 1].EndMills)
                throw new InvalidOperationException(
                    $"Profile snapshot segments are not contiguous at index {i}: " +
                    $"{segments[i - 1].EndMills} != {segments[i].StartMills}.");
        }
    }
}
