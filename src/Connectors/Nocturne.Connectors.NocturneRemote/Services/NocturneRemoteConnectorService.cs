using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.NocturneRemote.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.NocturneRemote.Services;

/// <summary>
///     Connector service that pulls data from a remote Nocturne V4 instance.
///     Uses direct grant bearer token authentication and the V4 paginated API.
/// </summary>
public class NocturneRemoteConnectorService : BaseConnectorService<NocturneRemoteConnectorConfiguration>
{
    private NocturneRemoteConnectorConfiguration _config;
    private string? _resolvedBaseUrl;
    private Dictionary<string, string>? _authHeaders;

    public NocturneRemoteConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NocturneRemoteConnectorConfiguration> serverResolver,
        ILogger<NocturneRemoteConnectorService> logger,
        IConnectorRegistration<NocturneRemoteConnectorConfiguration> registration,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _config = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
    }

    protected override string ConnectorSource => DataSources.NocturneRemoteConnector;
    public override string ServiceName => "Nocturne Remote";


    public override async Task<bool> AuthenticateAsync()
    {
        // Legacy no-config overload; uses the injected startup config.
        return await AuthenticateWithConfigAsync(_config);
    }

    private async Task<bool> AuthenticateWithConfigAsync(NocturneRemoteConnectorConfiguration config)
    {
        ResolveConfiguration(config);

        try
        {
            var url = BuildAbsoluteUrl($"{NocturneRemoteConstants.SensorGlucose}?limit=1");
            var response = await GetWithHeadersAsync(url, _authHeaders);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[{ConnectorSource}] Auth check returned HTTP {StatusCode}: {Body}",
                    ConnectorSource,
                    (int)response.StatusCode,
                    body);
                TrackFailedRequest($"Auth check failed: HTTP {(int)response.StatusCode}");
                return false;
            }

            TrackSuccessfulRequest();
            _logger.LogInformation(
                "[{ConnectorSource}] Successfully authenticated with remote Nocturne instance at {Url}",
                ConnectorSource,
                _resolvedBaseUrl);
            return true;
        }
        catch (Exception ex)
        {
            TrackFailedRequest($"Authentication failed: {ex.Message}");
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to remote Nocturne instance at {Url}",
                ConnectorSource,
                _resolvedBaseUrl);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken = default,
        DateTime? since = null,
        ISyncProgressReporter? progressReporter = null)
    {
        // Prime _config with the per-tenant config before base calls AuthenticateAsync(),
        // which delegates to AuthenticateWithConfigAsync(_config).
        _config = config;
        return await base.SyncDataAsync(config, cancellationToken, since, progressReporter);
    }

    protected override Task<bool> EnsureAuthenticatedAsync(
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken) => AuthenticateWithConfigAsync(config);

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToHashSet();

        foreach (var type in activeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await (type switch
                {
                    SyncDataType.Glucose => SyncSensorGlucoseAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.ManualBG => SyncBGChecksAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.Boluses => SyncBolusesAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.CarbIntake => SyncCarbIntakeAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.BolusCalculations => SyncBolusCalculationsAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.Notes => SyncNotesAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceEvents => SyncDeviceEventsAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.StateSpans => SyncStateSpansAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.Profiles => SyncProfilesAsync(config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceStatus => SyncDeviceStatusAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.Activity => SyncActivityAsync(request, config, result, activeTypes, cancellationToken),
                    SyncDataType.Food => SyncFoodAsync(config, result, activeTypes, cancellationToken),
                    _ => Task.CompletedTask
                });
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Failed to sync {type}: {ex.Message}");
                _logger.LogError(ex, "Failed to sync {DataType} for {Connector}", type, ConnectorSource);
            }
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    #region V4 Data Type Sync Methods

    private async Task SyncSensorGlucoseAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<SensorGlucose>(
            NocturneRemoteConstants.SensorGlucose, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
            ImportHelper.PrepareForImport(records), PublishSensorGlucoseDataAsync, config, ct);
    }

    private async Task SyncBGChecksAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<BGCheck>(
            NocturneRemoteConstants.BGChecks, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBGCheckDataAsync, config, ct);
    }

    private async Task SyncBolusesAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<Bolus>(
            NocturneRemoteConstants.Boluses, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusDataAsync, config, ct);
    }

    private async Task SyncCarbIntakeAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<CarbIntake>(
            NocturneRemoteConstants.CarbIntake, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            ImportHelper.PrepareForImport(records), PublishCarbIntakeDataAsync, config, ct);
    }

    private async Task SyncBolusCalculationsAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<BolusCalculation>(
            NocturneRemoteConstants.BolusCalculations, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusCalculationDataAsync, config, ct);
    }

    private async Task SyncNotesAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<Note>(
            NocturneRemoteConstants.Notes, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
            ImportHelper.PrepareForImport(records), PublishNoteDataAsync, config, ct);
    }

    private async Task SyncDeviceEventsAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<DeviceEvent>(
            NocturneRemoteConstants.DeviceEvents, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            ImportHelper.PrepareForImport(records), PublishDeviceEventDataAsync, config, ct);
    }

    #endregion

    #region Legacy Model Sync Methods

    private async Task SyncStateSpansAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<StateSpan>(
            NocturneRemoteConstants.StateSpans, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            records, PublishStateSpanDataAsync, config, ct);
    }

    private async Task SyncProfilesAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<Profile>(
            NocturneRemoteConstants.ProfileRecords, null, null, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
            records, PublishProfileDataAsync, config, ct);
    }

    private async Task SyncDeviceStatusAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // DeviceStatus uses the v1 API because the publisher only supports the legacy model.
        // The remote instance exposes v1 compatibility endpoints.
        var records = await FetchV1DeviceStatusAsync(request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceStatus, activeTypes,
            records, PublishDeviceStatusAsync, config, ct);
    }

    private async Task SyncActivityAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<Activity>(
            NocturneRemoteConstants.Activity, request.From, request.To, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
            records, PublishActivityDataAsync, config, ct);
    }

    private async Task SyncFoodAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // Foods endpoint returns a flat array, not PaginatedResponse
        var url = BuildAbsoluteUrl($"{NocturneRemoteConstants.Foods}?count={_config.MaxCount}");
        var response = await GetWithHeadersAsync(url, _authHeaders, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to fetch foods: HTTP {StatusCode}",
                ConnectorSource,
                (int)response.StatusCode);
            return;
        }

        var foods = await DeserializeResponseAsync<Food[]>(response, ct);

        // Food carries no per-record time, so no timestamp selector is supplied.
        await PublishRecordTypeAsync(result, SyncDataType.Food, activeTypes,
            foods?.ToList() ?? [], PublishFoodDataAsync, config, ct);
    }

    #endregion

    #region Pagination Helpers

    /// <summary>
    ///     Fetches all pages of V4 records from a paginated endpoint.
    /// </summary>
    private async Task<List<T>> FetchPaginatedV4Async<T>(
        string endpoint, DateTime? from, DateTime? to, CancellationToken ct) where T : IV4Record
    {
        var all = new List<T>();
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildAbsoluteUrl(BuildPaginatedUrl(endpoint, from, to, _config.MaxCount, offset));
            var response = await GetWithHeadersAsync(url, _authHeaders, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Failed to fetch {Endpoint}: HTTP {StatusCode}",
                    ConnectorSource,
                    endpoint,
                    (int)response.StatusCode);
                break;
            }

            var page = await DeserializeResponseAsync<PaginatedResponse<T>>(response, ct);
            if (page?.Data == null)
                break;

            var items = page.Data.ToList();
            if (items.Count == 0)
                break;

            all.AddRange(items);

            // Stop if we've fetched all records or got a partial page
            if (all.Count >= page.Pagination.Total || items.Count < _config.MaxCount)
                break;

            offset += items.Count;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Fetched {Count} {Type} records from remote",
            ConnectorSource,
            all.Count,
            typeof(T).Name);

        return all;
    }

    /// <summary>
    ///     Fetches all pages of legacy model records from a V4 paginated endpoint.
    /// </summary>
    private async Task<List<T>> FetchPaginatedLegacyAsync<T>(
        string endpoint, DateTime? from, DateTime? to, CancellationToken ct) where T : class
    {
        var all = new List<T>();
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildAbsoluteUrl(BuildPaginatedUrl(endpoint, from, to, _config.MaxCount, offset));
            var response = await GetWithHeadersAsync(url, _authHeaders, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Failed to fetch {Endpoint}: HTTP {StatusCode}",
                    ConnectorSource,
                    endpoint,
                    (int)response.StatusCode);
                break;
            }

            var page = await DeserializeResponseAsync<PaginatedResponse<T>>(response, ct);
            if (page?.Data == null)
                break;

            var items = page.Data.ToList();
            if (items.Count == 0)
                break;

            all.AddRange(items);

            if (all.Count >= page.Pagination.Total || items.Count < _config.MaxCount)
                break;

            offset += items.Count;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Fetched {Count} {Type} records from remote",
            ConnectorSource,
            all.Count,
            typeof(T).Name);

        return all;
    }

    /// <summary>
    ///     Fetches legacy DeviceStatus records from the v1 API of the remote instance.
    /// </summary>
    private async Task<List<DeviceStatus>> FetchV1DeviceStatusAsync(
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        var allStatuses = new List<DeviceStatus>();
        var currentTo = to;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = BuildAbsoluteUrl(BuildV1DeviceStatusUrl(from, currentTo));
            var response = await GetWithHeadersAsync(url, _authHeaders, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "[{ConnectorSource}] Failed to fetch device statuses: HTTP {StatusCode}",
                    ConnectorSource,
                    (int)response.StatusCode);
                break;
            }

            var statuses = await DeserializeResponseAsync<DeviceStatus[]>(response, ct);
            if (statuses == null || statuses.Length == 0)
                break;

            allStatuses.AddRange(statuses);

            if (statuses.Length < _config.MaxCount)
                break;

            var oldestDate = statuses
                .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
                .Where(dt => dt.HasValue)
                .Min();

            if (!oldestDate.HasValue)
                break;

            if (currentTo.HasValue && oldestDate.Value >= currentTo.Value)
                break;

            currentTo = oldestDate.Value.AddMilliseconds(-1);

            if (from.HasValue && currentTo < from)
                break;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Fetched {Count} DeviceStatus records from remote v1 API",
            ConnectorSource,
            allStatuses.Count);

        return allStatuses;
    }

    #endregion

    #region URL Builders

    private static string BuildPaginatedUrl(
        string endpoint, DateTime? from, DateTime? to, int limit, int offset)
    {
        var url = $"{endpoint}?limit={limit}&offset={offset}";

        if (from.HasValue)
            url += $"&from={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&to={to.Value.ToUniversalTime():o}";

        return url;
    }

    private string BuildV1DeviceStatusUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/devicestatus.json?count={_config.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    #endregion

    private void ResolveConfiguration(NocturneRemoteConnectorConfiguration config)
    {
        if (string.IsNullOrEmpty(config.Url))
            throw new InvalidOperationException("Remote Nocturne URL is not configured");

        var url = config.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? config.Url
            : $"https://{config.Url}";

        _resolvedBaseUrl = url.TrimEnd('/');

        _authHeaders = !string.IsNullOrEmpty(config.Token)
            ? new Dictionary<string, string> { ["Authorization"] = $"Bearer {config.Token}" }
            : null;
    }

    private string BuildAbsoluteUrl(string relativePath)
    {
        return _resolvedBaseUrl != null ? $"{_resolvedBaseUrl}{relativePath}" : relativePath;
    }
}
