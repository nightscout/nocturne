using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoTempBasalMapper
{
    private readonly string _connectorSource;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly GlookoTimeMapper _timeMapper;

    public GlookoTempBasalMapper(
        string connectorSource,
        GlookoTimeMapper timeMapper,
        ILogger logger,
        TimeProvider clock)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public List<TempBasal> TransformV3ToTempBasals(GlookoV3GraphResponse graphData)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tempBasals = new List<TempBasal>();

        if (graphData?.Series == null)
            return tempBasals;

        var series = graphData.Series;

        // SuspendBasal -> TempBasal with rate=0, origin=Suspended
        if (series.SuspendBasal != null)
            foreach (var suspend in ConnectorFaults.NonNullRecords(series.SuspendBasal, _logger, _connectorSource, "V3 suspendBasal"))
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(suspend.X);
                var durationSeconds = suspend.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = 0.0,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Suspended,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_suspend_basal_{suspend.X}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        // LgsPlgs -> TempBasal with rate=0, origin=Algorithm or Suspended
        if (series.LgsPlgs != null)
            foreach (var lgsEvent in ConnectorFaults.NonNullRecords(series.LgsPlgs, _logger, _connectorSource, "V3 lgsPlgs"))
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(lgsEvent.X);
                var durationSeconds = lgsEvent.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                var origin = lgsEvent.EventType?.ToUpperInvariant() == "SUSPEND"
                    ? TempBasalOrigin.Suspended
                    : TempBasalOrigin.Algorithm;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = 0.0,
                        ScheduledRate = null,
                        Origin = origin,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_lgsplgs_basal_{lgsEvent.X}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        // TemporaryBasal -> TempBasal with actual rate, origin=Manual
        if (series.TemporaryBasal != null)
            foreach (var tempBasal in ConnectorFaults.NonNullRecords(series.TemporaryBasal, _logger, _connectorSource, "V3 temporaryBasal"))
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(tempBasal.X);
                var durationSeconds = tempBasal.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                var rate = tempBasal.Y ?? 0;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = rate,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Manual,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_tempbasal_{tempBasal.X}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        // ScheduledBasal -> TempBasal with actual delivered rate, origin=Scheduled.
        // Tandem Control-IQ (and similar algorithmic pumps) report algorithm-driven adjustments
        // through this same stream — the connector cannot tell algorithm output from a true
        // schedule rate without consulting the user's programmed basal_schedules. The
        // ingest-side publisher reclassifies these to Algorithm and resolves the correct
        // ScheduledRate when the delivered rate diverges from the programmed schedule, so we
        // leave ScheduledRate null here rather than asserting "delivered == scheduled".
        if (series.ScheduledBasal != null)
            foreach (var basal in ConnectorFaults.NonNullRecords(series.ScheduledBasal, _logger, _connectorSource, "V3 scheduledBasal").Where(b => !b.Interpolated))
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(basal.X);
                var durationSeconds = basal.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                var rate = basal.Y ?? 0;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = rate,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Scheduled,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_scheduledbasal_{basal.X}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} temp basals from v3 data",
            _connectorSource,
            tempBasals.Count
        );

        return tempBasals;
    }

    public List<TempBasal> TransformV2ToTempBasals(GlookoBatchData batchData)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var tempBasals = new List<TempBasal>();

        if (batchData == null)
            return tempBasals;

        // TempBasals -> origin=Manual
        if (batchData.TempBasals != null)
            foreach (var tempBasal in ConnectorFaults.NonNullRecords(batchData.TempBasals, _logger, _connectorSource, "V2 temp basal"))
            {
                if (string.IsNullOrWhiteSpace(tempBasal.Timestamp))
                {
                    _logger.LogWarning("Skipping TempBasal with empty timestamp");
                    continue;
                }

                if (!DateTime.TryParse(
                        tempBasal.Timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var rawTimestamp))
                {
                    _logger.LogWarning("Failed to parse TempBasal timestamp: '{Timestamp}'", tempBasal.Timestamp);
                    continue;
                }

                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var durationSeconds = tempBasal.Duration;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                var rate = tempBasal.Rate;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = rate,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Manual,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_v2_tempbasal_{rawTimestamp.Ticks}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        // ScheduledBasals -> origin=Scheduled. See note on the v3 path above:
        // ScheduledRate is left null so the publisher can resolve it from the user's
        // programmed basal_schedules and reclassify algorithm-driven adjustments.
        if (batchData.ScheduledBasals != null)
            foreach (var basal in ConnectorFaults.NonNullRecords(batchData.ScheduledBasals, _logger, _connectorSource, "V2 scheduled basal"))
            {
                if (string.IsNullOrWhiteSpace(basal.Timestamp))
                {
                    _logger.LogWarning("Skipping ScheduledBasal with empty timestamp");
                    continue;
                }

                if (!DateTime.TryParse(
                        basal.Timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var rawTimestamp))
                {
                    _logger.LogWarning("Failed to parse ScheduledBasal timestamp: '{Timestamp}'", basal.Timestamp);
                    continue;
                }

                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var durationSeconds = basal.Duration;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                var rate = basal.Rate;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = rate,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Scheduled,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_v2_scheduledbasal_{rawTimestamp.Ticks}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        // SuspendBasals -> rate=0, origin=Suspended
        if (batchData.SuspendBasals != null)
            foreach (var suspend in ConnectorFaults.NonNullRecords(batchData.SuspendBasals, _logger, _connectorSource, "V2 suspend basal"))
            {
                if (string.IsNullOrWhiteSpace(suspend.Timestamp))
                {
                    _logger.LogWarning("Skipping SuspendBasal with empty timestamp");
                    continue;
                }

                if (!DateTime.TryParse(
                        suspend.Timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var rawTimestamp))
                {
                    _logger.LogWarning("Failed to parse SuspendBasal timestamp: '{Timestamp}'", suspend.Timestamp);
                    continue;
                }

                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var durationSeconds = suspend.Duration;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                tempBasals.Add(
                    new TempBasal
                    {
                        Id = Guid.CreateVersion7(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Rate = 0.0,
                        ScheduledRate = null,
                        Origin = TempBasalOrigin.Suspended,
                        Device = null,
                        App = null,
                        DataSource = _connectorSource,
                        LegacyId = $"glooko_v2_suspend_basal_{rawTimestamp.Ticks}",
                        CreatedAt = now,
                        ModifiedAt = now,
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} temp basals from v2 data (ScheduledBasals={ScheduledBasalCount}, TempBasals={TempBasalCount}, Suspends={SuspendCount})",
            _connectorSource,
            tempBasals.Count,
            batchData.ScheduledBasals?.Length ?? 0,
            batchData.TempBasals?.Length ?? 0,
            batchData.SuspendBasals?.Length ?? 0
        );

        return tempBasals;
    }
}
