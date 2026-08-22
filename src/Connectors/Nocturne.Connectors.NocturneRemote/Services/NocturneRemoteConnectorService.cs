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
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private NocturneRemoteConnectorConfiguration _config;
    private string? _resolvedBaseUrl;
    private Dictionary<string, string>? _authHeaders;

    public NocturneRemoteConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<NocturneRemoteConnectorConfiguration> serverResolver,
        ILogger<NocturneRemoteConnectorService> logger,
        IConnectorRegistration<NocturneRemoteConnectorConfiguration> registration,
        IRetryDelayStrategy retryDelayStrategy,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, serverResolver, logger, publisher)
    {
        _config = registration?.Defaults ?? throw new ArgumentNullException(nameof(registration));
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
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

        // Glucose keeps request.From: the framework derived it from the newest stored glucose
        // record, so it already is glucose's own cursor. Every other family resolves its own the
        // same way instead of inheriting that one. Sharing it strands a family that fell behind —
        // this run's glucose publish moves the shared cursor past the very range a failed crawl
        // still owes, which is what turns a repairable gap into a permanent one. An explicit range
        // (request.To set, as a cursor reset sends) is honoured as given.
        var openEnded = request.To is null;
        var treatmentFrom = openEnded && activeTypes.Overlaps(TreatmentFamily)
            ? await CalculateTreatmentSinceTimestampAsync(config)
            : request.From;
        var deviceStatusFrom = openEnded && activeTypes.Contains(SyncDataType.DeviceStatus)
            ? await CalculateDeviceStatusCatchUpSinceAsync(config) ?? request.From
            : request.From;
        var activityFrom = openEnded && activeTypes.Contains(SyncDataType.Activity)
            ? await CalculateActivityCatchUpSinceAsync(config) ?? request.From
            : request.From;

        foreach (var type in activeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await (type switch
                {
                    SyncDataType.Glucose => SyncSensorGlucoseAsync(request.From, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.ManualBG => SyncBGChecksAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Boluses => SyncBolusesAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.CarbIntake => SyncCarbIntakeAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.BolusCalculations => SyncBolusCalculationsAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Notes => SyncNotesAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceEvents => SyncDeviceEventsAsync(treatmentFrom, request.To, config, result, activeTypes, cancellationToken),
                    // State spans have no resume watermark of their own, so they stay on the
                    // glucose cursor and keep the exposure described above.
                    SyncDataType.StateSpans => SyncStateSpansAsync(request.From, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Profiles => SyncProfilesAsync(config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceStatus => SyncDeviceStatusAsync(deviceStatusFrom, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Activity => SyncActivityAsync(activityFrom, request.To, config, result, activeTypes, cancellationToken),
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

    /// <summary>
    ///     The types whose records all land in the v1 treatments collection, and so all resume from
    ///     the one watermark <see cref="BaseConnectorService{TConfig}.CalculateTreatmentSinceTimestampAsync"/>
    ///     reports for this source.
    /// </summary>
    /// <remarks>
    ///     That watermark is the newest record of any of them, so these six do not resume
    ///     independently of one another: a sibling that published in the same run still carries the
    ///     bound past a range one of them failed to read. Separating them needs a per-type watermark
    ///     the publisher does not expose.
    /// </remarks>
    private static readonly SyncDataType[] TreatmentFamily =
    [
        SyncDataType.ManualBG, SyncDataType.Boluses, SyncDataType.CarbIntake,
        SyncDataType.BolusCalculations, SyncDataType.Notes, SyncDataType.DeviceEvents
    ];

    #region V4 Data Type Sync Methods

    private async Task SyncSensorGlucoseAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<SensorGlucose>(
            NocturneRemoteConstants.SensorGlucose, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
            ImportHelper.PrepareForImport(records), PublishSensorGlucoseDataAsync, config, ct);
    }

    private async Task SyncBGChecksAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<BGCheck>(
            NocturneRemoteConstants.BGChecks, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBGCheckDataAsync, config, ct);
    }

    private async Task SyncBolusesAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Bolus>(
            NocturneRemoteConstants.Boluses, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusDataAsync, config, ct);
    }

    private async Task SyncCarbIntakeAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<CarbIntake>(
            NocturneRemoteConstants.CarbIntake, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            ImportHelper.PrepareForImport(records), PublishCarbIntakeDataAsync, config, ct);
    }

    private async Task SyncBolusCalculationsAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<BolusCalculation>(
            NocturneRemoteConstants.BolusCalculations, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusCalculationDataAsync, config, ct);
    }

    private async Task SyncNotesAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Note>(
            NocturneRemoteConstants.Notes, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
            ImportHelper.PrepareForImport(records), PublishNoteDataAsync, config, ct);
    }

    private async Task SyncDeviceEventsAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<DeviceEvent>(
            NocturneRemoteConstants.DeviceEvents, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceEvents, activeTypes,
            ImportHelper.PrepareForImport(records), PublishDeviceEventDataAsync, config, ct);
    }

    #endregion

    #region Legacy Model Sync Methods

    private async Task SyncStateSpansAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<StateSpan>(
            NocturneRemoteConstants.StateSpans, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            records, PublishStateSpanDataAsync, config, ct);
    }

    private async Task SyncProfilesAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Profile>(
            NocturneRemoteConstants.ProfileRecords, null, null, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
            records, PublishProfileDataAsync, config, ct);
    }

    private async Task SyncDeviceStatusAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // DeviceStatus uses the v1 API because the publisher only supports the legacy model.
        // The remote instance exposes v1 compatibility endpoints.
        var records = await FetchV1DeviceStatusAsync(from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceStatus, activeTypes,
            records, PublishDeviceStatusAsync, config, ct);
    }

    private async Task SyncActivityAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Activity>(
            NocturneRemoteConstants.Activity, from, to, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
            records, PublishActivityDataAsync, config, ct);
    }

    private async Task SyncFoodAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // Foods endpoint returns a flat array, not PaginatedResponse
        var foods = await FetchWithRetryAsync<Food[]>(
            $"{NocturneRemoteConstants.Foods}?count={_config.MaxCount}",
            NocturneRemoteConstants.Foods, ct);

        if (foods == null)
            throw FetchFailed(NocturneRemoteConstants.Foods);

        // Food carries no per-record time, so no timestamp selector is supplied.
        await PublishRecordTypeAsync(result, SyncDataType.Food, activeTypes,
            foods.ToList(), PublishFoodDataAsync, config, ct);
    }

    #endregion

    #region Fetch Helpers

    /// <summary>
    ///     One request to the remote instance under the connector retry budget, answering
    ///     <c>null</c> when it did not survive it. Callers turn that into
    ///     <see cref="BaseConnectorService{TConfig}.FetchFailed"/>; the two steps are separate
    ///     because only the caller knows what the missing payload was for.
    /// </summary>
    /// <remarks>
    ///     Mirrors the Nightscout connector's fetch layer so both crawls raise on the same
    ///     condition: a transient status is retried, and a status the remote will keep returning,
    ///     an unparseable body, or an exhausted budget all report the same <c>null</c>.
    /// </remarks>
    private Task<T?> FetchWithRetryAsync<T>(string relativeUrl, string operationName, CancellationToken ct)
        where T : class =>
        ExecuteWithRetryAsync(
            async () => await FetchCoreAsync<T>(relativeUrl, ct),
            _retryDelayStrategy,
            maxRetries: _config.MaxRetryAttempts,
            operationName: operationName,
            cancellationToken: ct);

    private async Task<T?> FetchCoreAsync<T>(string relativeUrl, CancellationToken ct) where T : class
    {
        var response = await GetWithHeadersAsync(BuildAbsoluteUrl(relativeUrl), _authHeaders, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<T>(response, ct);
    }

    #endregion

    #region Pagination Helpers

    /// <summary>
    ///     Fetches every page of a V4 paginated endpoint. <typeparamref name="T"/> spans both the V4
    ///     records and the legacy models the publishers still take, which page identically.
    /// </summary>
    /// <remarks>
    ///     The crawl accumulates the whole range before its caller publishes any of it, so a page
    ///     the remote never delivers costs the range rather than truncating it: pages arrive
    ///     newest-first and the family's next lower bound comes from its newest stored record, so
    ///     publishing the pages either side of a failure would put the ones in between out of reach.
    ///     Each page is fetched under the retry budget, so a raised failure means the page did not
    ///     survive <see cref="BaseConnectorConfiguration.MaxRetryAttempts"/> attempts rather than one
    ///     unlucky moment. See <see cref="BaseConnectorService{TConfig}.FetchFailed"/>.
    /// </remarks>
    private async Task<List<T>> FetchPaginatedAsync<T>(
        string endpoint, DateTime? from, DateTime? to, CancellationToken ct) where T : class
    {
        var all = new List<T>();
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await FetchWithRetryAsync<PaginatedResponse<T>>(
                BuildPaginatedUrl(endpoint, from, to, _config.MaxCount, offset), endpoint, ct);

            if (page?.Data == null)
                throw FetchFailed(endpoint);

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
    /// <remarks>A page that never arrives costs the range, for the reason given on
    /// <see cref="FetchPaginatedAsync{T}"/>.</remarks>
    private async Task<List<DeviceStatus>> FetchV1DeviceStatusAsync(
        DateTime? from, DateTime? to, CancellationToken ct)
    {
        var allStatuses = new List<DeviceStatus>();
        var currentTo = to;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var statuses = await FetchWithRetryAsync<DeviceStatus[]>(
                BuildV1DeviceStatusUrl(from, currentTo), V1DeviceStatusEndpoint, ct);

            if (statuses == null)
                throw FetchFailed(V1DeviceStatusEndpoint);

            if (statuses.Length == 0)
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

    private const string V1DeviceStatusEndpoint = "/api/v1/devicestatus.json";

    private string BuildV1DeviceStatusUrl(DateTime? from, DateTime? to)
    {
        var url = $"{V1DeviceStatusEndpoint}?count={_config.MaxCount}";

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
