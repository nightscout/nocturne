using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

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

        // Pump mode only: the zero-rate delivery is a TempBasal from GlookoTempBasalMapper.
        if (series.SuspendBasal != null)
            foreach (var suspend in ConnectorFaults.NonNullRecords(series.SuspendBasal, _logger, _connectorSource, "V3 suspendBasal"))
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

        // Pump mode only: the zero-rate delivery is a TempBasal from GlookoTempBasalMapper.
        if (series.LgsPlgs != null)
            foreach (var lgsEvent in ConnectorFaults.NonNullRecords(series.LgsPlgs, _logger, _connectorSource, "V3 lgsPlgs"))
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

        _logger.LogInformation(
            "[{ConnectorSource}] Transformed {Count} state spans from v3 data",
            _connectorSource,
            stateSpans.Count
        );

        return stateSpans;
    }

    /// <summary>
    ///     The v3 <c>profileChange</c> series as the profile the pump ran from each change until the next.
    /// </summary>
    public List<ProfileActivation> TransformV3ToProfileActivations(GlookoV3GraphResponse graphData)
    {
        var activations = new List<ProfileActivation>();
        if (graphData?.Series?.ProfileChange == null)
            return activations;

        var profileChanges = ConnectorFaults
            .NonNullRecords(graphData.Series.ProfileChange, _logger, _connectorSource, "V3 profileChange")
            .OrderBy(p => p.X)
            .ToList();
        for (var i = 0; i < profileChanges.Count; i++)
        {
            var change = profileChanges[i];
            activations.Add(new ProfileActivation
            {
                SyncIdentifier = $"glooko_profile_{change.X}",
                StartTimestamp = _timeMapper.GetCorrectedGlookoTime(change.X),
                EndTimestamp = i < profileChanges.Count - 1
                    ? _timeMapper.GetCorrectedGlookoTime(profileChanges[i + 1].X)
                    : null,
                Phase = ProfileActivationPhase.Effective,
                Origin = ProfileActivationOrigin.Connector,
                BaseProfileName = change.ProfileName ?? change.Label ?? "Unknown",
                DataSource = _connectorSource,
            });
        }

        return activations;
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

        foreach (var point in ConnectorFaults.NonNullRecords(dataPoints, _logger, _connectorSource, $"V3 {glookoType}"))
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

        foreach (var point in ConnectorFaults.NonNullRecords(dataPoints, _logger, _connectorSource, $"V3 {glookoType}"))
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

        // Pump mode only: the zero-rate delivery is a TempBasal from GlookoTempBasalMapper.
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
