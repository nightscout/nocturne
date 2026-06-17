using Nocturne.Core.Contracts.Analytics;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Analytics;
using Nocturne.Core.Models.V4;

namespace Nocturne.API.Services.Analytics;

/// <summary>
/// Default <see cref="ISensorIntegrityService"/>: reads tenant CGM and insulin data from the V4
/// repositories and runs the pure <see cref="SensorIntegrityDetector"/> over it.
/// </summary>
/// <remarks>
/// Detection runs on the deduplicated canonical glucose stream (the repository already filters
/// cross-connector duplicate links). Readings outside a plausible physiologic range are dropped at
/// this boundary — not inside the detector, which stays a faithful numeric port.
/// </remarks>
public class SensorIntegrityService : ISensorIntegrityService
{
    // Plausible glucose bounds (mg/dL), matching StatisticsService glucose extraction.
    private const double MinPlausibleMgdl = 0.0;
    private const double MaxPlausibleMgdl = 600.0;

    // Nocturnal window (local hours): a nadir at or after 22:00 or before 07:00 is nocturnal.
    private const int NocturnalStartHour = 22;
    private const int NocturnalEndHour = 7;

    private readonly ISensorGlucoseRepository _sensorGlucoseRepository;
    private readonly IBolusRepository _bolusRepository;

    public SensorIntegrityService(
        ISensorGlucoseRepository sensorGlucoseRepository,
        IBolusRepository bolusRepository)
    {
        _sensorGlucoseRepository = sensorGlucoseRepository;
        _bolusRepository = bolusRepository;
    }

    public async Task<SensorIntegrityReport> AnalyzeAsync(
        DateTime from,
        DateTime to,
        string? source = null,
        bool bySource = false,
        HypoEventOptions? hypoOptions = null,
        DetectorConfig? config = null,
        CancellationToken ct = default)
    {
        var fromUtc = AsUtc(from);
        var toUtc = AsUtc(to);

        var glucoseTask = _sensorGlucoseRepository.GetAsync(
            fromUtc, toUtc, device: null, source: source, limit: int.MaxValue, descending: false, ct: ct);
        // All bolus kinds (manual + APS micro-boluses) count toward insulin-during-cluster.
        var bolusTask = _bolusRepository.GetAsync(
            fromUtc, toUtc, device: null, source: null, limit: int.MaxValue, descending: false, ct: ct);

        await Task.WhenAll(glucoseTask, bolusTask);

        var readings = (await glucoseTask)
            .Where(r => r.Mgdl > MinPlausibleMgdl && r.Mgdl < MaxPlausibleMgdl)
            .OrderBy(r => r.Timestamp)
            .ToList();

        var insulin = (await bolusTask)
            .Select(b => new InsulinDose { Time = b.Timestamp, Units = b.Insulin })
            .OrderBy(d => d.Time)
            .ToList();

        var (clusters, hypoEvents) = Analyze(readings, insulin, hypoOptions, config);

        var perSource = bySource ? BuildPerSource(readings, insulin, hypoOptions, config) : null;

        return new SensorIntegrityReport
        {
            From = fromUtc,
            To = toUtc,
            Source = source,
            Clusters = clusters,
            HypoEvents = hypoEvents,
            Summary = BuildSummary(readings, clusters, hypoEvents),
            PerSource = perSource,
        };
    }

    private static (IReadOnlyList<GlucoseCluster> Clusters, IReadOnlyList<SensorIntegrityHypoEvent> Events)
        Analyze(
            IReadOnlyList<SensorGlucose> readings,
            IReadOnlyList<InsulinDose> insulin,
            HypoEventOptions? hypoOptions,
            DetectorConfig? config)
    {
        var timestamps = readings.Select(r => r.Timestamp).ToList();
        var glucose = readings.Select(r => r.Mgdl).ToList();

        // Detect once and reuse the cluster list for hypo-event correlation (the parameterless
        // FindHypoEvents would otherwise re-run detection over the same series).
        var clusters = SensorIntegrityDetector.DetectClusters(timestamps, glucose, config);
        var rawEvents = SensorIntegrityDetector.FindHypoEvents(
            clusters, timestamps, glucose, hypoOptions, insulin);

        // Offset lookup for nocturnal classification: the nadir timestamp is one of the readings.
        var offsetByTimestamp = new Dictionary<DateTime, int?>();
        foreach (var r in readings)
        {
            offsetByTimestamp.TryAdd(r.Timestamp, r.UtcOffset);
        }

        var events = rawEvents
            .Select(e => new SensorIntegrityHypoEvent
            {
                Event = e,
                IsNocturnal = IsNocturnal(e.NadirTime, offsetByTimestamp.GetValueOrDefault(e.NadirTime)),
            })
            .ToList();

        return (clusters, events);
    }

    private static IReadOnlyList<SensorIntegritySourceMetrics> BuildPerSource(
        IReadOnlyList<SensorGlucose> readings,
        IReadOnlyList<InsulinDose> insulin,
        HypoEventOptions? hypoOptions,
        DetectorConfig? config)
    {
        var metrics = new List<SensorIntegritySourceMetrics>();
        foreach (var group in readings
            .GroupBy(r => r.DataSource ?? string.Empty)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var subset = group.OrderBy(r => r.Timestamp).ToList();
            var (clusters, events) = Analyze(subset, insulin, hypoOptions, config);
            metrics.Add(new SensorIntegritySourceMetrics
            {
                Source = group.Key,
                Summary = BuildSummary(subset, clusters, events),
            });
        }

        return metrics;
    }

    private static SensorIntegritySummary BuildSummary(
        IReadOnlyList<SensorGlucose> readings,
        IReadOnlyList<GlucoseCluster> clusters,
        IReadOnlyList<SensorIntegrityHypoEvent> events) => new()
        {
            // Count distinct local days, consistent with the local-time framing used elsewhere
            // (nocturnal classification). Readings without an offset fall back to UTC.
            Days = readings.Select(r => r.Timestamp.AddMinutes(r.UtcOffset ?? 0).Date).Distinct().Count(),
            Clusters = clusters.Count,
            MediumClusters = clusters.Count(c => c.Confidence == ClusterConfidence.Medium),
            HighClusters = clusters.Count(c => c.Confidence == ClusterConfidence.High),
            Events = events.Count,
            NocturnalEvents = events.Count(e => e.IsNocturnal),
        };

    // Treat the bound as a UTC instant: relabel an unspecified kind (the common bound-from-query
    // case), but genuinely convert a local kind rather than reinterpreting its wall-clock.
    private static DateTime AsUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
    };

    private static bool IsNocturnal(DateTime nadirUtc, int? utcOffsetMinutes)
    {
        // Convert to local wall-clock for the nocturnal-window test. When the per-reading offset is
        // unknown we fall back to UTC hour; this can misclassify around the window edges, hence the
        // offset is preferred when present.
        var local = utcOffsetMinutes.HasValue
            ? nadirUtc.AddMinutes(utcOffsetMinutes.Value)
            : nadirUtc;
        var hour = local.Hour;
        return hour >= NocturnalStartHour || hour < NocturnalEndHour;
    }
}
