using System.Net.Http.Headers;
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
    private readonly NocturneRemoteConnectorConfiguration _config;

    public NocturneRemoteConnectorService(
        HttpClient httpClient,
        ILogger<NocturneRemoteConnectorService> logger,
        NocturneRemoteConnectorConfiguration config,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override string ConnectorSource => DataSources.NocturneRemoteConnector;
    public override string ServiceName => "Nocturne Remote";

    public override List<SyncDataType> SupportedDataTypes =>
    [
        SyncDataType.Glucose,
        SyncDataType.ManualBG,
        SyncDataType.Boluses,
        SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations,
        SyncDataType.Notes,
        SyncDataType.DeviceEvents,
        SyncDataType.StateSpans,
        SyncDataType.Profiles,
        SyncDataType.DeviceStatus,
        SyncDataType.Activity,
        SyncDataType.Food
    ];

    public override async Task<bool> AuthenticateAsync()
    {
        EnsureConfiguration();

        try
        {
            var url = $"{NocturneRemoteConstants.SensorGlucose}?limit=1";
            var response = await _httpClient.GetAsync(url);

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
                _httpClient.BaseAddress);
            return true;
        }
        catch (Exception ex)
        {
            TrackFailedRequest($"Authentication failed: {ex.Message}");
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to remote Nocturne instance at {Url}",
                ConnectorSource,
                _httpClient.BaseAddress);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        if (!await AuthenticateAsync())
        {
            return new SyncResult
            {
                Success = false,
                Message = "Authentication failed"
            };
        }

        return await base.SyncDataAsync(request, config, cancellationToken, progressReporter);
    }

    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken cancellationToken,
        ISyncProgressReporter? progressReporter = null)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        if (!request.DataTypes.Any())
            request.DataTypes = SupportedDataTypes;

        var enabledTypes = config.GetEnabledDataTypes(SupportedDataTypes);
        var activeTypes = request.DataTypes.Where(t => enabledTypes.Contains(t)).ToList();

        foreach (var type in activeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var (count, lastTime, success) = type switch
                {
                    SyncDataType.Glucose => await SyncSensorGlucoseAsync(request, config, cancellationToken),
                    SyncDataType.ManualBG => await SyncBGChecksAsync(request, config, cancellationToken),
                    SyncDataType.Boluses => await SyncBolusesAsync(request, config, cancellationToken),
                    SyncDataType.CarbIntake => await SyncCarbIntakeAsync(request, config, cancellationToken),
                    SyncDataType.BolusCalculations => await SyncBolusCalculationsAsync(request, config, cancellationToken),
                    SyncDataType.Notes => await SyncNotesAsync(request, config, cancellationToken),
                    SyncDataType.DeviceEvents => await SyncDeviceEventsAsync(request, config, cancellationToken),
                    SyncDataType.StateSpans => await SyncStateSpansAsync(request, config, cancellationToken),
                    SyncDataType.Profiles => await SyncProfilesAsync(config, cancellationToken),
                    SyncDataType.DeviceStatus => await SyncDeviceStatusAsync(request, config, cancellationToken),
                    SyncDataType.Activity => await SyncActivityAsync(request, config, cancellationToken),
                    SyncDataType.Food => await SyncFoodAsync(config, cancellationToken),
                    _ => (0, null, true)
                };

                result.ItemsSynced[type] = count;
                result.LastEntryTimes[type] = lastTime;
                if (!success)
                {
                    result.Success = false;
                    result.Errors.Add($"{type} publish failed");
                }
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

    private async Task<(int count, DateTime? lastTime, bool success)> SyncSensorGlucoseAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<SensorGlucose>(
            NocturneRemoteConstants.SensorGlucose, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishSensorGlucoseDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncBGChecksAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<BGCheck>(
            NocturneRemoteConstants.BGChecks, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishBGCheckDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncBolusesAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<Bolus>(
            NocturneRemoteConstants.Boluses, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishBolusDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncCarbIntakeAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<CarbIntake>(
            NocturneRemoteConstants.CarbIntake, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishCarbIntakeDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncBolusCalculationsAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<BolusCalculation>(
            NocturneRemoteConstants.BolusCalculations, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishBolusCalculationDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncNotesAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<Note>(
            NocturneRemoteConstants.Notes, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishNoteDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncDeviceEventsAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedV4Async<DeviceEvent>(
            NocturneRemoteConstants.DeviceEvents, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var prepared = ImportHelper.PrepareForImport(records);
        var lastTime = prepared.Max(r => r.Timestamp);
        var success = await PublishDeviceEventDataAsync(prepared, config, ct);
        return (prepared.Count, lastTime, success);
    }

    #endregion

    #region Legacy Model Sync Methods

    private async Task<(int count, DateTime? lastTime, bool success)> SyncStateSpansAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<StateSpan>(
            NocturneRemoteConstants.StateSpans, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var lastTime = records
            .Select(s => s.StartTimestamp)
            .DefaultIfEmpty()
            .Max();
        var success = await PublishStateSpanDataAsync(records, config, ct);
        return (records.Count, lastTime == default ? null : lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncProfilesAsync(
        NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<Profile>(
            NocturneRemoteConstants.ProfileRecords, null, null, ct);
        if (records.Count == 0) return (0, null, true);

        var lastTime = records
            .Where(p => p.Mills > 0)
            .Select(p => DateTimeOffset.FromUnixTimeMilliseconds(p.Mills).UtcDateTime)
            .DefaultIfEmpty()
            .Max();
        var success = await PublishProfileDataAsync(records, config, ct);
        return (records.Count, lastTime == default ? null : lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncDeviceStatusAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        // DeviceStatus uses the v1 API because the publisher only supports the legacy model.
        // The remote instance exposes v1 compatibility endpoints.
        var records = await FetchV1DeviceStatusAsync(request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var lastTime = records
            .Select(d => DateTimeOffset.TryParse(d.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
            .Where(dt => dt.HasValue)
            .Max();
        var success = await PublishDeviceStatusAsync(records, config, ct);
        return (records.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncActivityAsync(
        SyncRequest request, NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var records = await FetchPaginatedLegacyAsync<Activity>(
            NocturneRemoteConstants.Activity, request.From, request.To, ct);
        if (records.Count == 0) return (0, null, true);

        var lastTime = records
            .Select(a => DateTimeOffset.TryParse(a.CreatedAt, out var dto) ? dto.UtcDateTime : (DateTime?)null)
            .Where(dt => dt.HasValue)
            .Max();
        var success = await PublishActivityDataAsync(records, config, ct);
        return (records.Count, lastTime, success);
    }

    private async Task<(int count, DateTime? lastTime, bool success)> SyncFoodAsync(
        NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        // Foods endpoint returns a flat array, not PaginatedResponse
        var url = $"{NocturneRemoteConstants.Foods}?count={_config.MaxCount}";
        var response = await _httpClient.GetAsync(url, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to fetch foods: HTTP {StatusCode}",
                ConnectorSource,
                (int)response.StatusCode);
            return (0, null, true);
        }

        var foods = await DeserializeResponseAsync<Food[]>(response, ct);
        if (foods == null || foods.Length == 0) return (0, null, true);

        var success = await PublishFoodDataAsync(foods, config, ct);
        return (foods.Length, null, success);
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

            var url = BuildPaginatedUrl(endpoint, from, to, _config.MaxCount, offset);
            var response = await _httpClient.GetAsync(url, ct);

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

            var url = BuildPaginatedUrl(endpoint, from, to, _config.MaxCount, offset);
            var response = await _httpClient.GetAsync(url, ct);

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

            var url = BuildV1DeviceStatusUrl(from, currentTo);
            var response = await _httpClient.GetAsync(url, ct);

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

    private void EnsureConfiguration()
    {
        if (_httpClient.BaseAddress != null)
            return;

        if (string.IsNullOrEmpty(_config.Url))
            throw new InvalidOperationException("Remote Nocturne URL is not configured");

        var url = _config.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? _config.Url
            : $"https://{_config.Url}";

        _httpClient.BaseAddress = new Uri(url);

        if (!string.IsNullOrEmpty(_config.Token))
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _config.Token);
    }
}
