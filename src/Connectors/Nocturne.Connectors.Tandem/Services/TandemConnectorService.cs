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
    private const int ChunkDays = 7;

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

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.TempBasals,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans,
        SyncDataType.DeviceStatus,
        SyncDataType.Profiles,
    ];

    public override Task<bool> AuthenticateAsync()
    {
        // Authentication runs per-tenant inside PerformSyncInternalAsync, where the config is available.
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TandemConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
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

            var metadata = await _apiClient.GetPumpEventMetadataAsync(region, token, pumperId, cancellationToken);
            var device = ChooseDevice(metadata, config.PumpSerialNumber);
            if (device == null)
            {
                if (metadata.Count > 0 && IsRealSerial(config.PumpSerialNumber))
                {
                    // A configured serial that matches no pump is a misconfiguration, not an empty
                    // account — surface it (with the valid serials) so a typo is diagnosable.
                    var serials = string.Join(", ", metadata.Select(m => m.SerialNumber));
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

            if (enabled.Contains(SyncDataType.Profiles))
                await SyncProfilesAsync(device, time, result, config, cancellationToken);

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
        TandemPumpEventMetadata device, TandemTimeResolver time, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var profile = new TandemProfileMapper(_logger).Map(device.LastUpload?.Settings);
        if (profile == null)
            return;

        var success = await PublishProfileDataAsync([profile], config, cancellationToken);
        result.ItemsSynced[SyncDataType.Profiles] = 1;
        if (!success)
        {
            result.Success = false;
            result.Errors.Add("Profile publish failed");
        }
    }

    private async Task SyncEventsAsync(
        TandemConstants.RegionUrls region, string pumperId, TandemPumpEventMetadata device,
        HashSet<SyncDataType> enabled, TandemTimeResolver time, SyncResult result,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var end = device.MaxDateWithEvents?.UtcDateTime ?? DateTime.UtcNow;
        var start = await ResolveStartAsync(config, device);
        if (start >= end)
        {
            _logger.LogInformation(
                "[{Source}] Nothing to sync for device {Device} (start {Start} >= end {End})",
                ConnectorSource, device.TconnectDeviceId, start, end);
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
        // range in one pass — the connector must not map each window in isolation.
        var allEvents = new List<TandemPumpEvent>();
        foreach (var (windowStart, windowEnd) in Chunk(start, end))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var raw = await FetchWindowAsync(
                region, pumperId, device.TconnectDeviceId, windowStart, windowEnd, eventIdsFilter,
                config, cancellationToken);
            if (raw != null)
                allEvents.AddRange(TandemEventDecoder.Decode(raw, _logger));
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
        if (enabled.Contains(SyncDataType.Glucose) && groups.TryGetValue(TandemEventClass.CgmReading, out var cgmEvents))
            await PublishAsync(SyncDataType.Glucose, cgm.Map(cgmEvents), PublishSensorGlucoseDataAsync, result, config, cancellationToken);

        if (groups.TryGetValue(TandemEventClass.Bolus, out var bolusEvents))
        {
            var decomposed = bolus.Map(bolusEvents);
            if (enabled.Contains(SyncDataType.Boluses))
                await PublishAsync(SyncDataType.Boluses, decomposed.Boluses, PublishBolusDataAsync, result, config, cancellationToken);
            if (enabled.Contains(SyncDataType.CarbIntake))
                await PublishAsync(SyncDataType.CarbIntake, decomposed.CarbIntakes, PublishCarbIntakeDataAsync, result, config, cancellationToken);
            if (enabled.Contains(SyncDataType.BolusCalculations))
                await PublishAsync(SyncDataType.BolusCalculations, decomposed.BolusCalculations, PublishBolusCalculationDataAsync, result, config, cancellationToken);
        }

        if (enabled.Contains(SyncDataType.TempBasals) && groups.TryGetValue(TandemEventClass.Basal, out var basalEvents))
            await PublishAsync(SyncDataType.TempBasals, basal.Map(basalEvents, windowEnd, config.IgnoreZeroUnitBasal), PublishTempBasalDataAsync, result, config, cancellationToken);

        if (enabled.Contains(SyncDataType.DeviceEvents))
        {
            var devEvents = Concat(groups, TandemEventClass.Cartridge, TandemEventClass.CgmStartJoinStop,
                TandemEventClass.BasalSuspension, TandemEventClass.BasalResume);
            if (devEvents.Count > 0)
                await PublishAsync(SyncDataType.DeviceEvents, deviceEvents.Map(devEvents), PublishDeviceEventDataAsync, result, config, cancellationToken);

            var sysEvents = Concat(groups, TandemEventClass.Alarm, TandemEventClass.CgmAlert);
            if (sysEvents.Count > 0)
                // System events (alarms / CGM alerts) are gated and accounted under DeviceEvents —
                // there is no dedicated SyncDataType for them — so a publish failure flips Success.
                await PublishAsync(SyncDataType.DeviceEvents, systemEvents.Map(sysEvents),
                    PublishSystemEventDataAsync, result, config, cancellationToken);
        }

        if (enabled.Contains(SyncDataType.StateSpans) && groups.TryGetValue(TandemEventClass.UserMode, out var userModeEvents))
            await PublishAsync(SyncDataType.StateSpans, userMode.Map(userModeEvents), PublishStateSpanDataAsync, result, config, cancellationToken);

        if (enabled.Contains(SyncDataType.DeviceStatus) && groups.TryGetValue(TandemEventClass.DeviceStatus, out var dailyBasal))
            await PublishAsync(SyncDataType.DeviceStatus, deviceStatus.Map(dailyBasal), PublishDeviceStatusAsync, result, config, cancellationToken);
    }

    private async Task PublishAsync<T>(
        SyncDataType type, List<T> records,
        Func<IEnumerable<T>, TandemConnectorConfiguration, CancellationToken, Task<bool>> publish,
        SyncResult result, TandemConnectorConfiguration config, CancellationToken cancellationToken)
        where T : class
    {
        if (records.Count == 0)
            return;

        var success = await publish(records, config, cancellationToken);
        result.ItemsSynced.TryGetValue(type, out var prev);
        result.ItemsSynced[type] = prev + records.Count;
        if (!success)
        {
            result.Success = false;
            result.Errors.Add($"{type} publish failed");
        }
    }

    private async Task<string?> FetchWindowAsync(
        TandemConstants.RegionUrls region, string pumperId, string tconnectDeviceId,
        DateTime windowStart, DateTime windowEnd, int[]? eventIdsFilter,
        TandemConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token))
            return null;

        return await ExecuteWithRetryAsync(
            () => _apiClient.GetPumpEventsRawAsync(
                region, token!, pumperId, tconnectDeviceId, windowStart, windowEnd, eventIdsFilter, cancellationToken),
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
    private async Task<DateTime> ResolveStartAsync(TandemConnectorConfiguration config, TandemPumpEventMetadata device)
    {
        var glucoseSince = await CalculateSinceTimestampAsync(config);
        var treatmentSince = await CalculateTreatmentSinceTimestampAsync(config);

        var candidates = new[] { glucoseSince, treatmentSince }.Where(d => d.HasValue).Select(d => d!.Value).ToList();
        var start = candidates.Count > 0 ? candidates.Min() : DefaultInitialSyncFloor();

        if (device.MinDateWithEvents?.UtcDateTime is { } min && min > start)
            start = min;

        return start;
    }

    /// <summary>
    /// Selects the pump to follow: the one matching the configured serial number, or — when none is
    /// configured — the pump with the most recent events. Mirrors tconnectsync's ChooseDevice.
    /// </summary>
    internal static TandemPumpEventMetadata? ChooseDevice(
        IReadOnlyList<TandemPumpEventMetadata> metadata, string? serialNumber)
    {
        if (metadata.Count == 0)
            return null;

        if (IsRealSerial(serialNumber))
            return metadata.FirstOrDefault(m =>
                string.Equals(m.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase));

        return metadata
            .OrderByDescending(m => m.MaxDateWithEvents ?? DateTimeOffset.MinValue)
            .First();
    }

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

    private static IEnumerable<(DateTime Start, DateTime End)> Chunk(DateTime start, DateTime end)
    {
        var cursor = start;
        while (cursor < end)
        {
            var next = cursor.AddDays(ChunkDays);
            if (next > end)
                next = end;
            yield return (cursor, next);
            cursor = next;
        }
    }
}
