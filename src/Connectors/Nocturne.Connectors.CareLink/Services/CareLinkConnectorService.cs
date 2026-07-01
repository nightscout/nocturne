using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.CareLink.Configurations;
using Nocturne.Connectors.CareLink.Mappers;
using Nocturne.Connectors.CareLink.Models;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Timezones;

namespace Nocturne.Connectors.CareLink.Services;

/// <summary>
///     Connector service for Medtronic CareLink data.
///     Supports both patient and care-partner roles, tries BLE/SIMPLERA monitor path
///     before falling back to the legacy connect endpoint.
/// </summary>
public class CareLinkConnectorService : BaseConnectorService<CareLinkConnectorConfiguration>
{
    private readonly CareLinkAuthTokenProvider _tokenProvider;
    private readonly IConnectorConfigurationService _configService;
    private readonly ITimezoneTimelineService? _timezoneTimelineService;
    private readonly CareLinkSensorGlucoseMapper _sgMapper;

    private string? _cachedBleVersion;
    private string? _lastAlarmKey;
    private string? _initialRefreshToken;
    private string? _accessToken;

    public CareLinkConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<CareLinkConnectorConfiguration> serverResolver,
        CareLinkAuthTokenProvider tokenProvider,
        IConnectorConfigurationService configService,
        ILogger<CareLinkConnectorService> logger,
        IConnectorPublisher? publisher = null,
        ITimezoneTimelineService? timezoneTimelineService = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _timezoneTimelineService = timezoneTimelineService;
        _sgMapper = new CareLinkSensorGlucoseMapper(logger);
    }

    protected override string ConnectorSource => DataSources.CareLinkConnector;
    public override string ServiceName => ServiceNames.CareLinkConnector;
    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.DeviceStatus,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.TempBasals,
        SyncDataType.StateSpans,
    ];

    /// <inheritdoc />
    public override Task<bool> AuthenticateAsync()
    {
        // Legacy method; actual auth happens per-tenant in PerformSyncInternalAsync
        TrackSuccessfulRequest();
        return Task.FromResult(true);
    }

    private async Task<bool> AuthenticateWithConfigAsync(CareLinkConnectorConfiguration config)
    {
        // Seed the token provider with persisted secrets so refresh is available immediately
        var secrets = await _configService.GetSecretsAsync("CareLink");
        secrets.TryGetValue("refresh_token", out var savedRefreshToken);
        secrets.TryGetValue("client_id", out var savedClientId);
        secrets.TryGetValue("token_url", out var savedTokenUrl);
        secrets.TryGetValue("audience", out var savedAudience);

        _tokenProvider.InitializeFromSecrets(
            savedRefreshToken,
            savedClientId,
            savedTokenUrl,
            savedAudience);
        _initialRefreshToken = _tokenProvider.CurrentRefreshToken;

        var token = await _tokenProvider.GetValidTokenAsync(config);
        if (string.IsNullOrEmpty(token))
        {
            TrackFailedRequest("Failed to obtain CareLink access token");
            return false;
        }

        // Store the token for per-request use; never mutate DefaultRequestHeaders
        _accessToken = token;
        TrackSuccessfulRequest();
        return true;
    }

    /// <inheritdoc />
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        CareLinkConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        // Authenticate with per-tenant config
        if (!await AuthenticateWithConfigAsync(config))
        {
            result.Success = false;
            result.Errors.Add("Authentication failed");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        if (string.IsNullOrEmpty(_accessToken))
        {
            _logger.LogError("[{ConnectorSource}] No access token available — authentication must succeed before sync", ConnectorSource);
            result.Success = false;
            result.Errors.Add("Authentication failed");
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        var userInfo = await FetchUserInfoAsync(config, cancellationToken);
        var role = userInfo?.Role ?? string.Empty;
        var isCarePartner = role.Equals(CareLinkConstants.CarePartnerRoles.CarePartner, StringComparison.OrdinalIgnoreCase)
            || role.Equals(CareLinkConstants.CarePartnerRoles.CarePartnerOus, StringComparison.OrdinalIgnoreCase);

        var data = await TryFetchDataAsync(config, userInfo, isCarePartner, result, cancellationToken);
        if (data == null)
        {
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        // Seed the tenant timezone timeline from the pump's reported zone (idempotent; first sync only).
        await ConfigureCareLinkTimezoneAsync(data, cancellationToken);

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var isStale = IsDataStale(data);

        await PublishSensorGlucoseStepAsync(data, config, enabledTypes, isStale, result, cancellationToken);
        await PublishDeviceStatusStepAsync(data, config, enabledTypes, result, cancellationToken);
        await PublishAlarmStepAsync(data, config, result, cancellationToken);
        await PublishTreatmentsStepAsync(data, config, enabledTypes, result, cancellationToken);

        // Persist refresh token if it changed during sync
        await PersistRefreshTokenIfChangedAsync(cancellationToken);

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    ///     Fetches the authenticated CareLink user for role determination. Returns null on any
    ///     failure — the caller then treats the session as a (non-care-partner) patient.
    /// </summary>
    private async Task<CareLinkUserInfo?> FetchUserInfoAsync(
        CareLinkConnectorConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            var host = GetServerHost(config);
            var response = await GetWithHeadersAsync(
                $"https://{host}{CareLinkConstants.Endpoints.UsersMe}",
                AuthHeaders(),
                cancellationToken);

            if (response.IsSuccessStatusCode)
                return await DeserializeResponseAsync<CareLinkUserInfo>(response, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to fetch user info", ConnectorSource);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to fetch user info", ConnectorSource);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to fetch user info", ConnectorSource);
        }

        return null;
    }

    /// <summary>
    ///     Fetches CareLink data for the resolved role. On a fetch error, records the failure on
    ///     <paramref name="result"/> and returns null; also returns null (without failing the sync)
    ///     when the service has no data to return.
    /// </summary>
    private async Task<CareLinkData?> TryFetchDataAsync(
        CareLinkConnectorConfiguration config,
        CareLinkUserInfo? userInfo,
        bool isCarePartner,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        CareLinkData? data;
        try
        {
            data = isCarePartner
                ? await FetchAsCarePartnerAsync(config, cancellationToken)
                : await FetchAsPatientAsync(config, userInfo, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Failed to fetch CareLink data", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Data fetch failed: {ex.Message}");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Failed to fetch CareLink data", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Data fetch failed: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Failed to fetch CareLink data", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Data fetch failed: {ex.Message}");
            return null;
        }

        if (data == null)
            _logger.LogWarning("[{ConnectorSource}] No data returned from CareLink", ConnectorSource);

        return data;
    }

    /// <summary>
    ///     Publishes sensor glucose, skipping when glucose is disabled or the data is stale.
    /// </summary>
    private async Task PublishSensorGlucoseStepAsync(
        CareLinkData data,
        CareLinkConnectorConfiguration config,
        List<SyncDataType> enabledTypes,
        bool isStale,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        if (!enabledTypes.Contains(SyncDataType.Glucose))
            return;

        if (isStale)
        {
            _logger.LogDebug("[{ConnectorSource}] Skipping SGVs — data is stale (>{Threshold} min)",
                ConnectorSource, CareLinkConstants.StaleDataThresholdMinutes);
            return;
        }

        try
        {
            var sgRecords = _sgMapper.Map(data);
            if (sgRecords.Count > 0)
            {
                var success = await PublishSensorGlucoseDataAsync(sgRecords, config, cancellationToken);
                result.ItemsSynced[SyncDataType.Glucose] = sgRecords.Count;
                if (!success)
                {
                    result.Success = false;
                    result.Errors.Add("SensorGlucose publish failed");
                }
                else
                {
                    _logger.LogInformation(
                        "[{ConnectorSource}] Synced {Count} SensorGlucose records",
                        ConnectorSource, sgRecords.Count);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing SensorGlucose", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"SensorGlucose error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing SensorGlucose", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"SensorGlucose error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Publishes device status (always, even when data is stale).
    /// </summary>
    private async Task PublishDeviceStatusStepAsync(
        CareLinkData data,
        CareLinkConnectorConfiguration config,
        List<SyncDataType> enabledTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        if (!enabledTypes.Contains(SyncDataType.DeviceStatus))
            return;

        try
        {
            var deviceStatus = CareLinkDeviceStatusMapper.Map(data);
            var success = await PublishDeviceStatusAsync([deviceStatus], config, cancellationToken);
            result.ItemsSynced[SyncDataType.DeviceStatus] = 1;
            if (!success)
            {
                result.Success = false;
                result.Errors.Add("DeviceStatus publish failed");
            }
            else
            {
                _logger.LogInformation("[{ConnectorSource}] Synced DeviceStatus", ConnectorSource);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing DeviceStatus", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"DeviceStatus error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing DeviceStatus", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"DeviceStatus error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Publishes the latest alarm as a SystemEvent, deduped by datetime+code. Alarm failures
    ///     are logged but never fail the sync.
    /// </summary>
    private async Task PublishAlarmStepAsync(
        CareLinkData data,
        CareLinkConnectorConfiguration config,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        if (data.LastAlarm == null)
            return;

        try
        {
            var alarmKey = $"{data.LastAlarm.Datetime}_{data.LastAlarm.Code}";
            if (alarmKey != _lastAlarmKey)
            {
                var pumpOffsetMs = Utilities.CareLinkTimestampParser.CalculatePumpOffsetMs(
                    data.MedicalDeviceTime ?? "", data.CurrentServerTime);
                var systemEvent = CareLinkSystemEventMapper.Map(data.LastAlarm, pumpOffsetMs, data.CurrentServerTime);
                if (systemEvent != null)
                {
                    var success = await PublishSystemEventDataAsync([systemEvent], config, cancellationToken);
                    if (success)
                    {
                        _lastAlarmKey = alarmKey;
                        _logger.LogInformation("[{ConnectorSource}] Published alarm event {Code}",
                            ConnectorSource, data.LastAlarm.Code);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Error publishing alarm event", ConnectorSource);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Error publishing alarm event", ConnectorSource);
        }
    }

    /// <summary>
    ///     Publishes treatments (boluses, carbs, temp basals) plus notification events from the
    ///     periodic payload. These are historical markers with their own timestamps, so staleness
    ///     does not apply.
    /// </summary>
    private async Task PublishTreatmentsStepAsync(
        CareLinkData data,
        CareLinkConnectorConfiguration config,
        List<SyncDataType> enabledTypes,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var pumpOffsetMs = Utilities.CareLinkTimestampParser.CalculatePumpOffsetMs(
                data.MedicalDeviceTime ?? "", data.CurrentServerTime);

            if (enabledTypes.Contains(SyncDataType.Boluses))
            {
                var boluses = CareLinkTreatmentMapper.MapBoluses(data, pumpOffsetMs);
                if (boluses.Count > 0 && await PublishBolusDataAsync(boluses, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.Boluses] = boluses.Count;
                    _logger.LogInformation("[{ConnectorSource}] Synced {Count} Bolus records", ConnectorSource, boluses.Count);
                }
            }

            if (enabledTypes.Contains(SyncDataType.CarbIntake))
            {
                var carbs = CareLinkTreatmentMapper.MapCarbIntakes(data, pumpOffsetMs);
                if (carbs.Count > 0 && await PublishCarbIntakeDataAsync(carbs, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.CarbIntake] = carbs.Count;
                    _logger.LogInformation("[{ConnectorSource}] Synced {Count} CarbIntake records", ConnectorSource, carbs.Count);
                }
            }

            if (enabledTypes.Contains(SyncDataType.TempBasals))
            {
                var tempBasals = CareLinkTreatmentMapper.MapTempBasals(data, pumpOffsetMs);
                if (tempBasals.Count > 0 && await PublishTempBasalDataAsync(tempBasals, config, cancellationToken))
                {
                    result.ItemsSynced[SyncDataType.TempBasals] = tempBasals.Count;
                    _logger.LogInformation("[{ConnectorSource}] Synced {Count} TempBasal records", ConnectorSource, tempBasals.Count);
                }
            }

            var notifications = CareLinkSystemEventMapper.MapNotifications(data.NotificationHistory, pumpOffsetMs);
            if (notifications.Count > 0 && await PublishSystemEventDataAsync(notifications, config, cancellationToken))
                _logger.LogInformation("[{ConnectorSource}] Synced {Count} notification events", ConnectorSource, notifications.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing treatments", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Treatment error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "[{ConnectorSource}] Error publishing treatments", ConnectorSource);
            result.Success = false;
            result.Errors.Add($"Treatment error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Seeds the tenant timezone timeline origin from the pump's reported <c>clientTimeZoneName</c>.
    ///     Idempotent — <see cref="ITimezoneTimelineService.EnsureOriginAsync"/> only seeds when the
    ///     tenant has no timeline yet. Unresolved or UTC zones are skipped (never stored). Never fails
    ///     the sync.
    /// </summary>
    private async Task ConfigureCareLinkTimezoneAsync(CareLinkData data, CancellationToken ct)
    {
        if (_timezoneTimelineService is null)
            return;

        try
        {
            var iana = Utilities.CareLinkTimezoneResolver.ResolveIana(data.ClientTimeZoneName);
            if (iana is null)
            {
                if (!string.IsNullOrWhiteSpace(data.ClientTimeZoneName))
                    _logger.LogDebug("[{ConnectorSource}] Could not resolve CareLink timezone '{Timezone}'; skipping timeline seed",
                        ConnectorSource, data.ClientTimeZoneName);
                return;
            }

            var seeded = await _timezoneTimelineService.EnsureOriginAsync(iana, ct);
            if (seeded)
                _logger.LogInformation("[{ConnectorSource}] Seeded timezone timeline origin: {Zone}", ConnectorSource, iana);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Never fail a sync over timeline setup.
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to configure timezone timeline", ConnectorSource);
        }
    }

    /// <summary>
    ///     Fetches data as the authenticated patient.
    ///     Tries the monitor endpoint first; if device is BLE/SIMPLERA, fetches via the BLE periodic endpoint.
    ///     Falls back to the legacy connect endpoint on failure.
    /// </summary>
    private async Task<CareLinkData?> FetchAsPatientAsync(
        CareLinkConnectorConfiguration config,
        CareLinkUserInfo? userInfo,
        CancellationToken ct)
    {
        var host = GetServerHost(config);

        // If the monitor endpoint returns inline readings, use them directly.
        var monitorData = await TryGetMonitorDataAsync(host, ct);
        if (HasReadings(monitorData))
            return monitorData;

        // Modern Medtronic pumps (NGP/BLE/SIMPLERA) serve sensor data only via the country-settings
        // periodic endpoint; the monitor and legacy connect endpoints return empty for them.
        var periodic = await FetchViaPeriodicEndpointAsync(host, config, role: "patient", patientId: userInfo?.Id, ct);
        if (periodic != null)
            return periodic;

        // Legacy connect fallback for older devices.
        return await FetchViaConnectEndpointAsync(host, ct);
    }

    /// <summary>
    ///     Fetches data as a care partner.
    ///     Resolves the patient ID (errors if multiple patients exist and none is configured),
    ///     then tries the BLE monitor endpoint before falling back to the versioned POST endpoint.
    /// </summary>
    private async Task<CareLinkData?> FetchAsCarePartnerAsync(
        CareLinkConnectorConfiguration config,
        CancellationToken ct)
    {
        var host = GetServerHost(config);

        // Resolve patient ID
        var patientId = config.PatientId;
        if (string.IsNullOrEmpty(patientId))
        {
            patientId = await ResolvePatientIdAsync(host, ct);
            if (patientId == null)
                return null;
        }

        // If the monitor endpoint returns inline readings, use them directly.
        var monitorData = await TryGetMonitorDataAsync(host, ct);
        if (HasReadings(monitorData))
            return monitorData;

        // Modern Medtronic pumps serve sensor data only via the country-settings periodic endpoint.
        var periodic = await FetchViaPeriodicEndpointAsync(host, config, role: "carepartner", patientId, ct);
        if (periodic != null)
            return periodic;

        // Versioned POST fallback for care partners.
        return await PostToVersionedEndpointAsync(host, config, patientId, ct);
    }

    /// <summary>
    ///     Fetches the monitor endpoint, returning null on any failure. The monitor endpoint returns
    ///     inline readings for some devices and an empty body for modern pumps that report via the
    ///     periodic endpoint.
    /// </summary>
    private async Task<CareLinkData?> TryGetMonitorDataAsync(string host, CancellationToken ct)
    {
        try
        {
            var monitorResponse = await GetWithHeadersAsync(
                $"https://{host}{CareLinkConstants.Endpoints.MonitorData}",
                AuthHeaders(), ct);
            if (monitorResponse.IsSuccessStatusCode)
                return await DeserializeResponseAsync<CareLinkData>(monitorResponse, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] Monitor endpoint unavailable", ConnectorSource);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] Monitor endpoint unavailable", ConnectorSource);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] Monitor endpoint unavailable", ConnectorSource);
        }

        return null;
    }

    private static bool HasReadings(CareLinkData? data) =>
        data?.Sgs?.Count > 0 || data?.LastSG != null;

    /// <summary>
    ///     Fetches country settings and POSTs to the periodic data endpoint — the modern data source
    ///     for Medtronic NGP/BLE/SIMPLERA devices, which return empty monitor/connect responses.
    /// </summary>
    private async Task<CareLinkData?> FetchViaPeriodicEndpointAsync(
        string host,
        CareLinkConnectorConfiguration config,
        string role,
        string? patientId,
        CancellationToken ct)
    {
        try
        {
            var settingsResponse = await GetWithHeadersAsync(
                $"https://{host}{CareLinkConstants.Endpoints.CountrySettings}" +
                $"?countryCode={config.CountryCode}&language={config.LanguageCode}",
                AuthHeaders(), ct);

            if (!settingsResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("[{ConnectorSource}] Could not fetch country settings", ConnectorSource);
                return null;
            }

            var settings = await DeserializeResponseAsync<CareLinkCountrySettings>(settingsResponse, ct);
            var endpoint = settings?.BlePeriodicDataEndpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                _logger.LogDebug("[{ConnectorSource}] No BLE periodic data endpoint in country settings", ConnectorSource);
                return null;
            }

            var body = new Dictionary<string, string?> { ["username"] = config.Username, ["role"] = role };
            if (patientId != null)
                body["patientId"] = patientId;

            using var jsonContent = JsonContent.Create(body);
            var response = await PostWithHeadersAsync(endpoint, jsonContent, AuthHeaders(), ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await DeserializeResponseAsync<CareLinkData>(response, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] BLE endpoint fetch failed", ConnectorSource);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] BLE endpoint fetch failed", ConnectorSource);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "[{ConnectorSource}] BLE endpoint fetch failed", ConnectorSource);
            return null;
        }
    }

    /// <summary>
    ///     Fetches via the legacy connect endpoint (patient role).
    /// </summary>
    private async Task<CareLinkData?> FetchViaConnectEndpointAsync(string host, CancellationToken ct)
    {
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var url = $"https://{host}{CareLinkConstants.Endpoints.ConnectData}" +
                      $"?cpSerialNumber=NONE&msgType=last24hours&requestTime={timestamp}";

            var response = await GetWithHeadersAsync(url, AuthHeaders(), ct);
            if (!response.IsSuccessStatusCode)
                return null;

            return await DeserializeResponseAsync<CareLinkData>(response, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Connect endpoint failed", ConnectorSource);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Connect endpoint failed", ConnectorSource);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Connect endpoint failed", ConnectorSource);
            return null;
        }
    }

    /// <summary>
    ///     POSTs to a versioned care-partner endpoint.
    ///     Uses the cached version on first attempt; iterates <see cref="CareLinkConstants.BleEndpointVersions"/>
    ///     if uncached, caching the first version that succeeds.
    /// </summary>
    private async Task<CareLinkData?> PostToVersionedEndpointAsync(
        string host,
        CareLinkConnectorConfiguration config,
        string patientId,
        CancellationToken ct)
    {
        var versionsToTry = _cachedBleVersion != null
            ? [_cachedBleVersion]
            : CareLinkConstants.BleEndpointVersions;

        var body = new Dictionary<string, string?> { ["username"] = config.Username, ["role"] = "carepartner", ["patientId"] = patientId };

        foreach (var version in versionsToTry)
        {
            try
            {
                var url = $"https://{host}/connect/carepartner{version}display/data";
                using var jsonContent = JsonContent.Create(body);
                var response = await PostWithHeadersAsync(url, jsonContent, AuthHeaders(), ct);

                if (response.IsSuccessStatusCode)
                {
                    _cachedBleVersion = version;
                    return await DeserializeResponseAsync<CareLinkData>(response, ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "[{ConnectorSource}] Versioned endpoint {Version} failed", ConnectorSource, version);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "[{ConnectorSource}] Versioned endpoint {Version} failed", ConnectorSource, version);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "[{ConnectorSource}] Versioned endpoint {Version} failed", ConnectorSource, version);
            }
        }

        // Full failure: reset cache
        _cachedBleVersion = null;
        _logger.LogWarning("[{ConnectorSource}] All versioned care-partner endpoints failed", ConnectorSource);
        return null;
    }

    /// <summary>
    ///     Resolves the patient ID for a care-partner account.
    ///     Auto-selects if exactly one patient is linked; fails with a clear error if multiple exist
    ///     and no PatientId is configured (medical data safety).
    /// </summary>
    private async Task<string?> ResolvePatientIdAsync(string host, CancellationToken ct)
    {
        try
        {
            var response = await GetWithHeadersAsync(
                $"https://{host}{CareLinkConstants.Endpoints.LinkedPatients}",
                AuthHeaders(), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[{ConnectorSource}] Failed to fetch linked patients: {Status}",
                    ConnectorSource, response.StatusCode);
                return null;
            }

            var patients = await DeserializeResponseAsync<List<CareLinkPatientLink>>(response, ct);
            if (patients == null || patients.Count == 0)
            {
                _logger.LogWarning("[{ConnectorSource}] No linked patients found", ConnectorSource);
                return null;
            }

            if (patients.Count == 1)
                return patients[0].Username;

            // Multiple patients: refuse to pick — user must configure PatientId
            _logger.LogError(
                "[{ConnectorSource}] Multiple linked patients found but no PatientId is configured. " +
                "Set PatientId to one of: {Patients}",
                ConnectorSource,
                string.Join(", ", patients.Select(p => p.Username)));
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Error resolving patient ID", ConnectorSource);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Error resolving patient ID", ConnectorSource);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Error resolving patient ID", ConnectorSource);
            return null;
        }
    }

    /// <summary>
    ///     Persists the refresh token to secrets storage if it changed during the sync cycle.
    /// </summary>
    private async Task PersistRefreshTokenIfChangedAsync(CancellationToken ct)
    {
        var cached = await _tokenProvider.GetCachedSessionAsync();
        var currentRefreshToken = cached?.Metadata?.GetValueOrDefault("RefreshToken");
        if (string.IsNullOrEmpty(currentRefreshToken) || currentRefreshToken == _initialRefreshToken)
            return;

        try
        {
            var secrets = new Dictionary<string, string> { ["refresh_token"] = currentRefreshToken };

            var clientId = cached?.Metadata?.GetValueOrDefault("ClientId");
            if (!string.IsNullOrEmpty(clientId))
                secrets["client_id"] = clientId;

            var tokenUrl = cached?.Metadata?.GetValueOrDefault("TokenUrl");
            if (!string.IsNullOrEmpty(tokenUrl))
                secrets["token_url"] = tokenUrl;

            var audience = cached?.Metadata?.GetValueOrDefault("Audience");
            if (!string.IsNullOrEmpty(audience))
                secrets["audience"] = audience;

            await _configService.SaveSecretsAsync("CareLink", secrets, "connector-runtime", ct);
            _logger.LogInformation("[{ConnectorSource}] Persisted updated refresh token", ConnectorSource);
            _initialRefreshToken = currentRefreshToken;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "[{ConnectorSource}] Failed to persist refresh token", ConnectorSource);
        }
    }

    /// <summary>
    ///     Returns a per-request Authorization header dictionary using the token obtained during
    ///     <see cref="AuthenticateAsync"/>. Avoids mutating <c>DefaultRequestHeaders</c> which is not thread-safe.
    /// </summary>
    private Dictionary<string, string> AuthHeaders() =>
        new() { ["Authorization"] = $"Bearer {_accessToken}" };

    /// <summary>
    ///     Returns true if the last medical device update is older than the staleness threshold.
    /// </summary>
    private static bool IsDataStale(CareLinkData data)
    {
        if (data.CurrentServerTime == 0 || data.LastMedicalDeviceDataUpdateServerTime == 0)
            return false;

        var ageMins = (data.CurrentServerTime - data.LastMedicalDeviceDataUpdateServerTime) / 60_000.0;
        return ageMins > CareLinkConstants.StaleDataThresholdMinutes;
    }

    /// <summary>
    ///     Maps config.Server to the appropriate CareLink host name.
    /// </summary>
    private static string GetServerHost(CareLinkConnectorConfiguration config) =>
        config.Server.Equals("EU", StringComparison.OrdinalIgnoreCase)
            ? CareLinkConstants.Servers.Eu
            : CareLinkConstants.Servers.Us;
}
