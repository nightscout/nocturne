using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Glooko.Mappers;

public class GlookoStateSpanMapper
{
    private readonly string _connectorSource;
    private readonly ILogger _logger;
    private readonly GlookoTimeMapper _timeMapper;

    public GlookoStateSpanMapper(
        string connectorSource,
        GlookoTimeMapper timeMapper,
        ILogger logger)
    {
        _connectorSource = connectorSource ?? throw new ArgumentNullException(nameof(connectorSource));
        _timeMapper = timeMapper ?? throw new ArgumentNullException(nameof(timeMapper));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<StateSpan> TransformV3ToStateSpans(GlookoV3GraphResponse graphData)
    {
        var stateSpans = new List<StateSpan>();

        if (graphData?.Series == null)
            return stateSpans;

        var series = graphData.Series;

        if (series.SuspendBasal != null)
            foreach (var suspend in series.SuspendBasal)
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(suspend.X);
                var durationSeconds = suspend.Duration ?? 0;
                var startMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds();
                var endMills =
                    durationSeconds > 0
                        ? startMills + durationSeconds * 1000
                        : (long?)null;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_suspend_{suspend.X}",
                        Category = StateSpanCategory.PumpMode,
                        State = PumpModeState.Suspended.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "label", suspend.Label ?? "Suspended" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_suspend_basal_{suspend.X}",
                        Category = StateSpanCategory.BasalDelivery,
                        State = BasalDeliveryState.Active.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "rate", 0.0 },
                            { "origin", BasalDeliveryOrigin.Suspended.ToString() },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        if (series.LgsPlgs != null)
            foreach (var lgsEvent in series.LgsPlgs)
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(lgsEvent.X);
                var durationSeconds = lgsEvent.Duration ?? 0;
                var startMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds();
                var endMills =
                    durationSeconds > 0
                        ? startMills + durationSeconds * 1000
                        : (long?)null;

                var stateValue = lgsEvent.EventType?.ToUpperInvariant() switch
                {
                    "LGS" => PumpModeState.Limited.ToString(),
                    "PLGS" => PumpModeState.Limited.ToString(),
                    "SUSPEND" => PumpModeState.Suspended.ToString(),
                    _ => PumpModeState.Limited.ToString()
                };

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_lgsplgs_{lgsEvent.X}",
                        Category = StateSpanCategory.PumpMode,
                        State = stateValue,
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "label", lgsEvent.Label ?? lgsEvent.EventType ?? "LGS/PLGS" },
                            { "eventType", lgsEvent.EventType ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );

                var basalOrigin = lgsEvent.EventType?.ToUpperInvariant() == "SUSPEND"
                    ? BasalDeliveryOrigin.Suspended
                    : BasalDeliveryOrigin.Algorithm;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_lgsplgs_basal_{lgsEvent.X}",
                        Category = StateSpanCategory.BasalDelivery,
                        State = BasalDeliveryState.Active.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "rate", 0.0 },
                            { "origin", basalOrigin.ToString() },
                            { "eventType", lgsEvent.EventType ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        if (series.ProfileChange != null)
        {
            var profileChanges = series.ProfileChange.OrderBy(p => p.X).ToList();
            for (var i = 0; i < profileChanges.Count; i++)
            {
                var change = profileChanges[i];
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);

                long? endMills = null;
                if (i < profileChanges.Count - 1)
                    endMills = new DateTimeOffset(
                        _timeMapper.GetCorrectedGlookoTime(profileChanges[i + 1].X)
                    ).ToUnixTimeMilliseconds();

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_profile_{change.X}",
                        Category = StateSpanCategory.Profile,
                        State = ProfileState.Active.ToString(),
                        StartMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds(),
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "profileName", change.ProfileName ?? change.Label ?? "Unknown" }
                        }
                    }
                );
            }
        }

        if (series.TemporaryBasal != null)
            foreach (var tempBasal in series.TemporaryBasal)
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(tempBasal.X);
                var durationSeconds = tempBasal.Duration ?? 0;
                var startMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds();
                var endMills =
                    durationSeconds > 0
                        ? startMills + durationSeconds * 1000
                        : (long?)null;

                var rate = tempBasal.Y ?? 0;
                var calculatedInsulin = rate * durationSeconds / 3600.0;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_tempbasal_{tempBasal.X}",
                        Category = StateSpanCategory.BasalDelivery,
                        State = BasalDeliveryState.Active.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "rate", rate },
                            { "origin", BasalDeliveryOrigin.Manual.ToString() },
                            { "durationSeconds", durationSeconds },
                            { "calculatedInsulin", calculatedInsulin }
                        }
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} state spans from v3 data",
            _connectorSource,
            stateSpans.Count
        );

        return stateSpans;
    }

    public List<StateSpan> TransformV2ToStateSpans(GlookoBatchData batchData)
    {
        var stateSpans = new List<StateSpan>();

        if (batchData == null)
            return stateSpans;

        if (batchData.TempBasals != null)
            foreach (var tempBasal in batchData.TempBasals)
            {
                var rawTimestamp = DateTime.Parse(
                    tempBasal.Timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                );
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var durationSeconds = tempBasal.Duration;
                var startMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds();
                var endMills =
                    durationSeconds > 0
                        ? startMills + durationSeconds * 1000
                        : (long?)null;

                var rate = tempBasal.Rate;
                var calculatedInsulin = rate * durationSeconds / 3600.0;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_v2_tempbasal_{rawTimestamp.Ticks}",
                        Category = StateSpanCategory.BasalDelivery,
                        State = BasalDeliveryState.Active.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "rate", rate },
                            { "origin", BasalDeliveryOrigin.Manual.ToString() },
                            { "durationSeconds", durationSeconds },
                            { "calculatedInsulin", calculatedInsulin }
                        }
                    }
                );
            }

        if (batchData.SuspendBasals != null)
            foreach (var suspend in batchData.SuspendBasals)
            {
                var rawTimestamp = DateTime.Parse(
                    suspend.Timestamp,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                );
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(rawTimestamp);
                var durationSeconds = suspend.Duration;
                var startMills = new DateTimeOffset(startTimestamp).ToUnixTimeMilliseconds();
                var endMills =
                    durationSeconds > 0
                        ? startMills + durationSeconds * 1000
                        : (long?)null;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_v2_suspend_{rawTimestamp.Ticks}",
                        Category = StateSpanCategory.PumpMode,
                        State = PumpModeState.Suspended.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "suspendReason", suspend.SuspendReason ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_v2_suspend_basal_{rawTimestamp.Ticks}",
                        Category = StateSpanCategory.BasalDelivery,
                        State = BasalDeliveryState.Active.ToString(),
                        StartMills = startMills,
                        EndMills = endMills,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "rate", 0.0 },
                            { "origin", BasalDeliveryOrigin.Suspended.ToString() },
                            { "suspendReason", suspend.SuspendReason ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} state spans from v2 data (TempBasals={TempBasalCount}, Suspends={SuspendCount})",
            _connectorSource,
            stateSpans.Count,
            batchData.TempBasals?.Length ?? 0,
            batchData.SuspendBasals?.Length ?? 0
        );

        return stateSpans;
    }
}