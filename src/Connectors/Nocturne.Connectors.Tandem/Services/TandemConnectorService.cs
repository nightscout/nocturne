using System.Globalization;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tandem.Configurations;
using Nocturne.Connectors.Tandem.EventParser;
using Nocturne.Connectors.Tandem.Mappers;
using Nocturne.Connectors.Tandem.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.Tandem.Services;

/// <summary>
/// Connector service for Tandem Source (t:connect). Authenticates, selects a pump, then walks its
/// event history in date-chunked windows, decoding each window's pump events and mapping them to
/// Nocturne V4 records. The data covered mirrors the open-source <c>tconnectsync</c> project:
/// CGM readings, boluses (with carbs and calculations), basal delivery, cartridge/cannula/tubing
/// and CGM-session device events, pump suspend/resume, alarms, CGM alerts, sleep/exercise spans,
/// device status, and profiles.
/// </summary>
public class TandemConnectorService : BaseConnectorService<TandemConnectorConfiguration>
{

    private readonly TandemAuthTokenProvider _tokenProvider;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly TandemSourceApiClient _apiClient;

    public TandemConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TandemConnectorConfiguration> serverResolver,
        ILogger<TandemConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        TandemAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null)
        : base(httpClient, serverResolver, logger, publisher)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _apiClient = new TandemSourceApiClient(httpClient, logger);
    }

    protected override string ConnectorSource => DataSources.TConnectSyncConnector;
    public override string ServiceName => "Tandem Source";


    public override Task<bool> AuthenticateAsync()
    {
        // Authentication runs per-tenant inside PerformSyncInternalAsync, where the config is available.
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TandemConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };
        var enabled = config.GetEnabledDataTypes(SupportedDataTypes).ToHashSet();
        var region = TandemConstants.ForRegion(config.Region);

        try
        {
            var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
            var session = await _tokenProvider.GetCachedSessionAsync();
            var pumperId = session?.Metadata?.GetValueOrDefault(TandemAuthTokenProvider.PumperIdKey);
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(pumperId))
            {
                _logger.LogError("[{Source}] Tandem Source authentication failed", ConnectorSource);
                result.Success = false;
                result.Errors.Add("Authentication failed");
                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var pumper = await _apiClient.GetPumperAsync(region, token, pumperId, cancellationToken);
            var pumps = pumper?.Pumps ?? [];
            var device = ChooseDevice(pumps, config.PumpSerialNumber);
            if (device == null)
            {
                if (pumps.Count > 0 && IsRealSerial(config.PumpSerialNumber))
                {
                    // A configured serial that matches no pump is a misconfiguration, not an empty
                    // account — surface it (with the valid serials) so a typo is diagnosable.
                    var serials = string.Join(", ", pumps.Select(m => m.SerialNumber));
                    _logger.LogError(
                        "[{Source}] Configured pump serial {Serial} not found on account; available: {Serials}",
                        ConnectorSource, config.PumpSerialNumber, serials);
                    result.Success = false;
                    result.Errors.Add(
                        $"Pump serial '{config.PumpSerialNumber}' not found on account (available: {serials})");
                }
                else
                {
                    _logger.LogWarning("[{Source}] No Tandem pumps found on the account", ConnectorSource);
                }

                result.EndTime = DateTimeOffset.UtcNow;
                return result;
            }

            var time = new TandemTimeResolver(config.TimezoneOffset);

            await SyncProfilesAsync(device, enabled, result, config, cancellationToken);

            await SyncEventsAsync(region, pumperId, device, enabled, time, result, config, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[{Source}] Tandem sync canceled", ConnectorSource);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Source}] Error during Tandem sync", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    private async Task SyncProfilesAsync(
        TandemBffPump device, HashSet<SyncDataType> enabled, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var profile = new TandemProfileMapper(_logger).Map(device.Settings?.Details);
        if (profile == null)
            return;

        await PublishRecordTypeAsync<Nocturne.Core.Models.Profile>(
            result, SyncDataType.Profiles, enabled, [profile],
            PublishProfileDataAsync, config, cancellationToken,
            timestampOf: p => TimestampFromMills(p.Mills));
    }

    private async Task SyncEventsAsync(
        TandemConstants.RegionUrls region, string pumperId, TandemBffPump device,
        HashSet<SyncDataType> enabled, TandemTimeResolver time, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var end = ParseWallClockUtc(device.MaxDateOfEvents, time) ?? DateTime.UtcNow;
        var start = await ResolveStartAsync(config, device, time);
        if (start >= end)
        {
            _logger.LogInformation(
                "[{Source}] Nothing to sync for device {Device} (start {Start} >= end {End})",
                ConnectorSource, device.AssignmentId, start, end);
            return;
        }

        // LID_DAILY_BASAL (device status) is not in the backend's default event filter, so the full
        // history log must be requested when device status is enabled — matching tconnectsync.
        var fetchAll = config.FetchAllEventTypes || enabled.Contains(SyncDataType.DeviceStatus);
        var eventIdsFilter = fetchAll ? null : TandemConstants.DefaultEventIds;

        var cgm = new TandemCgmMapper(_logger, time);
        var bolus = new TandemBolusMapper(_logger, time);
        var basal = new TandemBasalMapper(_logger, time);
        var deviceEvents = new TandemDeviceEventMapper(_logger, time);
        var systemEvents = new TandemSystemEventMapper(_logger, time);
        var userMode = new TandemUserModeMapper(_logger, time);
        var deviceStatus = new TandemDeviceStatusMapper(_logger, time);

        // Fetch and decode every window first, then map over the full event set. Bolus
        // reassembly (request messages + completion) and sleep/exercise start/stop pairing can
        // straddle a window boundary, so — like tconnectsync, which processes the whole requested
        // range in one pass — the connector must not map each window in isolation. Events that
        // appear in more than one window are deduplicated by their (sequenceGroup, sequenceNumber)
        // identity, and the separately-returned clockChanges are not consumed (matching upstream).
        var allEvents = new List<TandemPumpEvent>();
        var seen = new HashSet<(long, uint)>();
        foreach (var (windowStart, windowEnd) in Chunk(start, end))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await FetchWindowAsync(
                region, pumperId, device.AssignmentId, windowStart, windowEnd, eventIdsFilter,
                config, cancellationToken);
            foreach (var logEvent in response?.Events ?? [])
                if (seen.Add((logEvent.SequenceGroup, logEvent.SequenceNumber)))
                    allEvents.Add(TandemEventDecoder.Decode(logEvent, _logger));
        }

        if (allEvents.Count == 0)
            return;

        var groups = allEvents
            .Select(e => (Event: e, Class: TandemEventClasses.ForEvent(e)))
            .Where(x => x.Class != null)
            .GroupBy(x => x.Class!.Value, x => x.Event)
            .ToDictionary(g => g.Key, g => g.ToList());

        await PublishEventsAsync(
            groups, enabled, end, cgm, bolus, basal, deviceEvents, systemEvents,
            userMode, deviceStatus, result, config, cancellationToken);
    }

    private async Task PublishEventsAsync(
        IReadOnlyDictionary<TandemEventClass, List<TandemPumpEvent>> groups,
        HashSet<SyncDataType> enabled, DateTime windowEnd,
        TandemCgmMapper cgm, TandemBolusMapper bolus, TandemBasalMapper basal,
        TandemDeviceEventMapper deviceEvents, TandemSystemEventMapper systemEvents,
        TandemUserModeMapper userMode, TandemDeviceStatusMapper deviceStatus,
        SyncResult result, TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        if (groups.TryGetValue(TandemEventClass.CgmReading, out var cgmEvents))
            await PublishRecordTypeAsync(result, SyncDataType.Glucose, enabled,
                cgm.Map(cgmEvents), PublishSensorGlucoseDataAsync, config, cancellationToken,
                timestampOf: s => s.Timestamp);

        if (groups.TryGetValue(TandemEventClass.Bolus, out var bolusEvents))
        {
            var decomposed = bolus.Map(bolusEvents);
            await PublishRecordTypeAsync(result, SyncDataType.Boluses, enabled,
                decomposed.Boluses, PublishBolusDataAsync, config, cancellationToken,
                timestampOf: b => b.Timestamp);
            await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, enabled,
                decomposed.CarbIntakes, PublishCarbIntakeDataAsync, config, cancellationToken,
                timestampOf: c => c.Timestamp);
            await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, enabled,
                decomposed.BolusCalculations, PublishBolusCalculationDataAsync, config, cancellationToken,
                timestampOf: b => b.Timestamp);
        }

        if (groups.TryGetValue(TandemEventClass.Basal, out var basalEvents))
            await PublishRecordTypeAsync(result, SyncDataType.TempBasals, enabled,
                basal.Map(basalEvents, windowEnd, config.IgnoreZeroUnitBasal), PublishTempBasalDataAsync,
                config, cancellationToken, timestampOf: t => t.StartTimestamp);

        var devEvents = Concat(groups, TandemEventClass.Cartridge, TandemEventClass.CgmStartJoinStop,
            TandemEventClass.BasalSuspension, TandemEventClass.BasalResume);
        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, enabled,
            deviceEvents.Map(devEvents), PublishDeviceEventDataAsync, config, cancellationToken,
            timestampOf: d => d.Timestamp);

        // Alarms and CGM alerts are gated and accounted under DeviceEvents — there is no dedicated
        // SyncDataType for them — so a publish failure flips Success.
        var sysEvents = Concat(groups, TandemEventClass.Alarm, TandemEventClass.CgmAlert);
        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, enabled,
            systemEvents.Map(sysEvents), PublishSystemEventDataAsync, config, cancellationToken,
            timestampOf: e => TimestampFromMills(e.Mills));

        if (groups.TryGetValue(TandemEventClass.UserMode, out var userModeEvents))
            await PublishRecordTypeAsync(result, SyncDataType.StateSpans, enabled,
                userMode.Map(userModeEvents), PublishStateSpanDataAsync, config, cancellationToken,
                timestampOf: s => s.StartTimestamp);

        if (groups.TryGetValue(TandemEventClass.DeviceStatus, out var dailyBasal))
            await PublishRecordTypeAsync(result, SyncDataType.DeviceStatus, enabled,
                deviceStatus.Map(dailyBasal), PublishDeviceStatusAsync, config, cancellationToken,
                timestampOf: d => TimestampFromMills(d.Mills));
    }

    private async Task<TandemPumpLogsResponse?> FetchWindowAsync(
        TandemConstants.RegionUrls region, string pumperId, string deviceAssignmentId,
        DateTime windowStart, DateTime windowEnd, int[]? eventIdsFilter,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
            return null;

        return await ExecuteWithRetryAsync(
            () => _apiClient.GetPumpLogsAsync(
                region, token!, pumperId, deviceAssignmentId, windowStart, windowEnd, eventIdsFilter, cancellationToken),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
                return !string.IsNullOrEmpty(token);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchPumpEvents",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Resolves the start of the sync window: the earliest catch-up point across glucose and
    /// treatments (so no enabled data type is missed), never earlier than the pump's first event.
    /// </summary>
    private async Task<DateTime> ResolveStartAsync(
        TandemConnectorConfiguration config, TandemBffPump device, TandemTimeResolver time)
    {
        var glucoseSince = await CalculateSinceTimestampAsync(config);
        var treatmentSince = await CalculateTreatmentSinceTimestampAsync(config);

        var candidates = new[] { glucoseSince, treatmentSince }.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        var start = candidates.Count > 0 ? candidates.Min() : DefaultInitialSyncFloor();

        if (ParseWallClockUtc(device.AvailableDataRange?.Start, time) is { } min && min > start)
            start = min;

        return start;
    }

    /// <summary>
    /// Selects the pump to follow: the one matching the configured serial number, or — when none is
    /// configured — the pump with the most recent events, skipping pumps that have never uploaded
    /// (null <c>maxDateOfEvents</c>) and falling back to the first pump when none has events.
    /// Mirrors tconnectsync's ChooseDevice.
    /// </summary>
    internal static TandemBffPump? ChooseDevice(
        IReadOnlyList<TandemBffPump> pumps, string? serialNumber)
    {
        if (pumps.Count == 0)
            return null;

        if (IsRealSerial(serialNumber))
            return pumps.FirstOrDefault(m =>
                string.Equals(m.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));

        // maxDateOfEvents values share the pump's timezone, so ordering the naive wall-clock
        // values directly is equivalent to ordering their UTC conversions.
        return pumps
            .Where(m => ParseWallClock(m.MaxDateOfEvents) != null)
            .OrderByDescending(m => ParseWallClock(m.MaxDateOfEvents)!.Value)
            .FirstOrDefault() ?? pumps[0];
    }

    /// <summary>Parses a naive pump-local BFF timestamp, or null when absent/unparseable.</summary>
    private static DateTime? ParseWallClock(string? value) =>
        DateTime.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Parses a naive pump-local BFF timestamp and converts it to UTC via the configured offset.</summary>
    private static DateTime? ParseWallClockUtc(string? value, TandemTimeResolver time) =>
        ParseWallClock(value) is { } wallClock ? time.ToUtc(wallClock) : null;

    /// <summary>
    /// Whether a configured serial actually selects a pump. Empty/whitespace means "no preference",
    /// and "11111111" is tconnectsync's sentinel for the same.
    /// </summary>
    private static bool IsRealSerial(string? serial) =>
        !string.IsNullOrWhiteSpace(serial) && serial != "11111111";

    private static List<TandemPumpEvent> Concat(
        IReadOnlyDictionary<TandemEventClass, List<TandemPumpEvent>> groups, params TandemEventClass[] classes) =>
        classes
            .Select(groups.GetValueOrDefault)
            .Where(list => list != null)
            .SelectMany(list => list!)
            .ToList();

    /// <summary>
    /// Splits the range into inclusive day-granular windows no larger than the pump-logs endpoint's
    /// ~4-week cap, mirroring tconnectsync's <c>_pump_log_windows</c>. Bounds are dates: the API
    /// expands them to T00:00:00Z–T23:59:59Z, so adjacent windows do not overlap.
    /// </summary>
    private static IEnumerable<(DateTime Start, DateTime End)> Chunk(DateTime start, DateTime end)
    {
        var cursor = start.Date;
        var last = end.Date;
        while (cursor <= last)
        {
            var windowEnd = cursor.AddDays(TandemConstants.PumpLogsWindowDays - 1);
            if (windowEnd > last)
                windowEnd = last;
            yield return (cursor, windowEnd);
            cursor = windowEnd.AddDays(1);
        }
    }
}
