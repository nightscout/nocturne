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

        // SuspendBasal -> PumpMode StateSpan only (BasalDelivery now handled by GlookoTempBasalMapper)
        if (series.SuspendBasal != null)
            foreach (var suspend in series.SuspendBasal)
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(suspend.X);
                var durationSeconds = suspend.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_suspend_{suspend.X}",
                        Category = StateSpanCategory.PumpMode,
                        State = PumpModeState.Suspended.ToString(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "label", suspend.Label ?? "Suspended" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        // LgsPlgs -> PumpMode StateSpan only (BasalDelivery now handled by GlookoTempBasalMapper)
        if (series.LgsPlgs != null)
            foreach (var lgsEvent in series.LgsPlgs)
            {
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(lgsEvent.X);
                var durationSeconds = lgsEvent.Duration ?? 0;
                var endTimestamp =
                    durationSeconds > 0
                        ? startTimestamp.AddSeconds(durationSeconds)
                        : (DateTime?)null;

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
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "label", lgsEvent.Label ?? lgsEvent.EventType ?? "LGS/PLGS" },
                            { "eventType", lgsEvent.EventType ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        // ProfileChange -> Profile StateSpan (unchanged)
        if (series.ProfileChange != null)
        {
            var profileChanges = series.ProfileChange.OrderBy(p => p.X).ToList();
            for (var i = 0; i < profileChanges.Count; i++)
            {
                var change = profileChanges[i];
                var startTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X);

                DateTime? endTimestamp = null;
                if (i < profileChanges.Count - 1)
                    endTimestamp = _timeMapper.GetCorrectedGlookoTime(profileChanges[i + 1].X);

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_profile_{change.X}",
                        Category = StateSpanCategory.Profile,
                        State = ProfileState.Active.ToString(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "profileName", change.ProfileName ?? change.Label ?? "Unknown" }
                        }
                    }
                );
            }
        }

        // TemporaryBasal and ScheduledBasal BasalDelivery spans removed -
        // now produced as TempBasal records by GlookoTempBasalMapper

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} state spans from v3 data",
            _connectorSource,
            stateSpans.Count
        );

        return stateSpans;
    }

    public List<StateSpan> TransformV3PumpModeToStateSpans(GlookoV3GraphResponse graphData)
    {
        var stateSpans = new List<StateSpan>();

        if (graphData?.Series == null)
            return stateSpans;

        var series = graphData.Series;

        // PumpMode mappings
        MapPumpModeSeries(stateSpans, series.PumpCamapsAutomaticMode, "camaps_automatic", PumpModeState.Automatic);
        MapPumpModeSeries(stateSpans, series.PumpCamapsManualMode, "camaps_manual", PumpModeState.Manual);
        MapPumpModeSeries(stateSpans, series.PumpCamapsBoostMode, "camaps_boost", PumpModeState.Boost);
        MapPumpModeSeries(stateSpans, series.PumpCamapsEaseOffMode, "camaps_easeoff", PumpModeState.EaseOff);
        MapPumpModeSeries(stateSpans, series.PumpCamapsLibertyMode, "camaps_liberty", PumpModeState.Liberty);
        MapPumpModeSeries(stateSpans, series.PumpCamapsPumpDeliverySuspendedMode, "camaps_suspended", PumpModeState.Suspended);
        MapPumpModeSeries(stateSpans, series.PumpControliqAutomaticMode, "controliq_automatic", PumpModeState.Automatic);
        MapPumpModeSeries(stateSpans, series.PumpControliqManualMode, "controliq_manual", PumpModeState.Manual);
        MapPumpModeSeries(stateSpans, series.PumpControliqSleepMode, "controliq_sleep", PumpModeState.Sleep);
        MapPumpModeSeries(stateSpans, series.PumpControliqExerciseMode, "controliq_exercise", PumpModeState.Exercise);
        MapPumpModeSeries(stateSpans, series.PumpOp5AutomaticMode, "op5_automatic", PumpModeState.Automatic);
        MapPumpModeSeries(stateSpans, series.PumpOp5ManualMode, "op5_manual", PumpModeState.Manual);
        MapPumpModeSeries(stateSpans, series.PumpOp5LimitedMode, "op5_limited", PumpModeState.Limited);
        MapPumpModeSeries(stateSpans, series.PumpOp5HypoprotectMode, "op5_hypoprotect", PumpModeState.Limited);
        MapPumpModeSeries(stateSpans, series.PumpBasaliqAutomaticMode, "basaliq_automatic", PumpModeState.Automatic);
        MapPumpModeSeries(stateSpans, series.PumpBasaliqManualMode, "basaliq_manual", PumpModeState.Manual);
        MapPumpModeSeries(stateSpans, series.PumpGenericAutomaticMode, "generic_automatic", PumpModeState.Automatic);
        MapPumpModeSeries(stateSpans, series.PumpGenericManualMode, "generic_manual", PumpModeState.Manual);

        // PumpConnectivity mappings
        MapPumpConnectivitySeries(stateSpans, series.PumpCamapsNoPumpConnectivityMode, "camaps_no_pump", PumpConnectivityState.Disconnected);
        MapPumpConnectivitySeries(stateSpans, series.PumpCamapsBluetoothTurnedOffMode, "camaps_bt_off", PumpConnectivityState.BluetoothOff);
        MapPumpConnectivitySeries(stateSpans, series.PumpCamapsNoCgmMode, "camaps_no_cgm", PumpConnectivityState.Disconnected);

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} pump mode/connectivity state spans from v3 data",
            _connectorSource,
            stateSpans.Count
        );

        return stateSpans;
    }

    private void MapPumpModeSeries(
        List<StateSpan> stateSpans,
        GlookoV3PumpModeDataPoint[]? dataPoints,
        string glookoType,
        PumpModeState state)
    {
        if (dataPoints == null)
            return;

        foreach (var point in dataPoints)
        {
            if (point.Interpolated)
                continue;

            var startTimestamp = _timeMapper.GetCorrectedGlookoTime(point.X);
            var durationSeconds = point.Duration ?? 0;
            var endTimestamp =
                durationSeconds > 0
                    ? startTimestamp.AddSeconds(durationSeconds)
                    : (DateTime?)null;

            stateSpans.Add(
                new StateSpan
                {
                    OriginalId = $"glooko_pumpmode_{glookoType}_{point.X}",
                    Category = StateSpanCategory.PumpMode,
                    State = state.ToString(),
                    StartTimestamp = startTimestamp,
                    EndTimestamp = endTimestamp,
                    Source = _connectorSource,
                    Metadata = new Dictionary<string, object>
                    {
                        { "label", point.Type ?? glookoType },
                        { "glookoType", glookoType },
                        { "durationSeconds", durationSeconds }
                    }
                }
            );
        }
    }

    private void MapPumpConnectivitySeries(
        List<StateSpan> stateSpans,
        GlookoV3PumpModeDataPoint[]? dataPoints,
        string glookoType,
        PumpConnectivityState state)
    {
        if (dataPoints == null)
            return;

        foreach (var point in dataPoints)
        {
            if (point.Interpolated)
                continue;

            var startTimestamp = _timeMapper.GetCorrectedGlookoTime(point.X);
            var durationSeconds = point.Duration ?? 0;
            var endTimestamp =
                durationSeconds > 0
                    ? startTimestamp.AddSeconds(durationSeconds)
                    : (DateTime?)null;

            stateSpans.Add(
                new StateSpan
                {
                    OriginalId = $"glooko_pumpconn_{glookoType}_{point.X}",
                    Category = StateSpanCategory.PumpConnectivity,
                    State = state.ToString(),
                    StartTimestamp = startTimestamp,
                    EndTimestamp = endTimestamp,
                    Source = _connectorSource,
                    Metadata = new Dictionary<string, object>
                    {
                        { "label", point.Type ?? glookoType },
                        { "glookoType", glookoType },
                        { "durationSeconds", durationSeconds }
                    }
                }
            );
        }
    }

    public List<StateSpan> TransformV2ToStateSpans(GlookoBatchData batchData)
    {
        var stateSpans = new List<StateSpan>();

        if (batchData == null)
            return stateSpans;

        // TempBasals and ScheduledBasals BasalDelivery spans removed -
        // now produced as TempBasal records by GlookoTempBasalMapper

        // SuspendBasals -> PumpMode StateSpan only (BasalDelivery now handled by GlookoTempBasalMapper)
        if (batchData.SuspendBasals != null)
            foreach (var suspend in batchData.SuspendBasals)
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

                stateSpans.Add(
                    new StateSpan
                    {
                        OriginalId = $"glooko_v2_suspend_{rawTimestamp.Ticks}",
                        Category = StateSpanCategory.PumpMode,
                        State = PumpModeState.Suspended.ToString(),
                        StartTimestamp = startTimestamp,
                        EndTimestamp = endTimestamp,
                        Source = _connectorSource,
                        Metadata = new Dictionary<string, object>
                        {
                            { "suspendReason", suspend.SuspendReason ?? "unknown" },
                            { "durationSeconds", durationSeconds }
                        }
                    }
                );
            }

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} state spans from v2 data (Suspends={SuspendCount})",
            _connectorSource,
            stateSpans.Count,
            batchData.SuspendBasals?.Length ?? 0
        );

        return stateSpans;
    }
}
