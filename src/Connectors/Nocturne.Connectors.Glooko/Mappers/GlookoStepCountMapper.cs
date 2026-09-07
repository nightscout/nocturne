using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

/// <summary>
///     Maps the SSV2 <c>validic/routines</c> feed (daily activity summary) to <see cref="StepCount"/> records.
///     <c>steps</c> is the day's total step count (a fractional double, rounded to an int). Each record is an
///     absolute daily total, so <see cref="StepCount.Source"/> is flagged with bit 0 (absolute-total) rather
///     than a delta. Keyed on the stable Glooko guid via a deterministic GUID <see cref="StepCount.Id"/> so
///     re-syncs upsert in place; soft-delete aware; zero/empty step records skipped.
/// </summary>
public class GlookoStepCountMapper(string connectorSource, GlookoTimeMapper timeMapper, ILogger logger)
{
    /// <summary>xDrip source bitmask: bit 0 set ⇒ the metric is an absolute total (not a delta).</summary>
    private const int AbsoluteTotalSource = 1;

    private readonly string _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
    private readonly GlookoTimeMapper _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public List<StepCount> MapSsv2Routines(IReadOnlyList<GlookoSsv2Routine> routines)
    {
        var results = new List<StepCount>();

        foreach (var r in routines)
        {
            try
            {
                if (r.SoftDeleted || r.Steps is not > 0) continue;

                var rawTimestamp = _timeMapper.GetRawGlookoDate(r.Timestamp ?? string.Empty, null);
                var correctedTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);

                var steps = (int)Math.Round(r.Steps.Value, MidpointRounding.AwayFromZero);
                if (steps <= 0) continue;

                var key = !string.IsNullOrEmpty(r.Guid) ? r.Guid! : $"ssv2_routine:{rawTimestamp.Ticks}:{steps}";

                results.Add(new StepCount
                {
                    Id = GlookoHealthIds.Derive("ssv2_routine", key).ToString(),
                    Timestamp = correctedTimestamp,
                    Metric = steps,
                    Source = AbsoluteTotalSource,
                    Device = _connectorSource,
                    EnteredBy = _connectorSource,
                    DataSource = _connectorSource,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{ConnectorSource}] Error mapping SSV2 routine", _connectorSource);
            }
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} step counts from SSV2 validic/routines feed", _connectorSource, results.Count);

        return results;
    }
}
