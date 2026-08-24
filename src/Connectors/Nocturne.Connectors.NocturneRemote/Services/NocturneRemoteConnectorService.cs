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
        // record, so it already is glucose's own cursor. Every other family widens it with its own
        // resume point (see ResumeFrom) rather than inheriting it alone. An explicit range
        // (request.To set, as a cursor reset sends) bypasses the catch-up bounds entirely.
        var openEnded = request.To is null;

        // Resolved at most once per run and awaited inside the loop's error boundary, so a publisher
        // that cannot answer fails only the families whose bound it was: a faulted task re-throws on
        // every await, which attributes it to each of them and leaves the rest of the run alone.
        Task<DateTime?>? treatment = null;
        Task<DateTime?>? deviceStatus = null;
        Task<DateTime?>? activity = null;

        // The six types below all land in the v1 treatments collection, so they share one watermark:
        // its newest record of any of them. They therefore do not resume independently of each
        // other — a sibling that published in the same run still carries the bound past a range one
        // of them failed to read. Separating them needs a per-type watermark the publisher does not
        // expose.
        Task<DateTime?> TreatmentFromAsync() =>
            treatment ??= BoundAsync(() => CalculateTreatmentSinceTimestampAsync(config));
        Task<DateTime?> DeviceStatusFromAsync() =>
            deviceStatus ??= BoundAsync(() => CalculateDeviceStatusCatchUpSinceAsync(config));
        Task<DateTime?> ActivityFromAsync() =>
            activity ??= BoundAsync(() => CalculateActivityCatchUpSinceAsync(config));

        async Task<DateTime?> BoundAsync(Func<Task<DateTime?>> resumePoint)
        {
            if (!openEnded)
                return request.From;

            // Awaited before the bound is decided, so a watermark the publisher cannot answer
            // fails this family whether or not the value is used.
            var resume = await resumePoint();

            // A run carrying no glucose cursor imports the remote's full history here, which no
            // family's resume point may narrow — unlike ResumeFrom's own reading of an absent
            // caller bound, which the other connectors keep.
            return request.From is null ? null : ResumeFrom(request.From, resume ?? request.From);
        }

        foreach (var type in activeTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await (type switch
                {
                    SyncDataType.Glucose => SyncSensorGlucoseAsync(request.From, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.ManualBG => SyncBGChecksAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Boluses => SyncBolusesAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.CarbIntake => SyncCarbIntakeAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.BolusCalculations => SyncBolusCalculationsAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Notes => SyncNotesAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceEvents => SyncDeviceEventsAsync(await TreatmentFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    // State spans have no resume watermark to widen with, so they alone still
                    // resume from wherever the glucose cursor reached.
                    SyncDataType.StateSpans => SyncStateSpansAsync(request.From, request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Profiles => SyncProfilesAsync(config, result, activeTypes, cancellationToken),
                    SyncDataType.DeviceStatus => SyncDeviceStatusAsync(await DeviceStatusFromAsync(), request.To, config, result, activeTypes, cancellationToken),
                    SyncDataType.Activity => SyncActivityAsync(await ActivityFromAsync(), request.To, config, result, activeTypes, cancellationToken),
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
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<SensorGlucose>(
            NocturneRemoteConstants.SensorGlucose, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
            ImportHelper.PrepareForImport(records), PublishSensorGlucoseDataAsync, config, ct);
    }

    private async Task SyncBGChecksAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<BGCheck>(
            NocturneRemoteConstants.BGChecks, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.ManualBG, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBGCheckDataAsync, config, ct);
    }

    private async Task SyncBolusesAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Bolus>(
            NocturneRemoteConstants.Boluses, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Boluses, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusDataAsync, config, ct);
    }

    private async Task SyncCarbIntakeAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<CarbIntake>(
            NocturneRemoteConstants.CarbIntake, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.CarbIntake, activeTypes,
            ImportHelper.PrepareForImport(records), PublishCarbIntakeDataAsync, config, ct);
    }

    private async Task SyncBolusCalculationsAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<BolusCalculation>(
            NocturneRemoteConstants.BolusCalculations, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.BolusCalculations, activeTypes,
            ImportHelper.PrepareForImport(records), PublishBolusCalculationDataAsync, config, ct);
    }

    private async Task SyncNotesAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Note>(
            NocturneRemoteConstants.Notes, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Notes, activeTypes,
            ImportHelper.PrepareForImport(records), PublishNoteDataAsync, config, ct);
    }

    private async Task SyncDeviceEventsAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<DeviceEvent>(
            NocturneRemoteConstants.DeviceEvents, from, to, config, ct);

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
            NocturneRemoteConstants.StateSpans, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.StateSpans, activeTypes,
            records, PublishStateSpanDataAsync, config, ct);
    }

    private async Task SyncProfilesAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Profile>(
            NocturneRemoteConstants.ProfileRecords, null, null, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Profiles, activeTypes,
            records, PublishProfileDataAsync, config, ct);
    }

    private async Task SyncDeviceStatusAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // DeviceStatus uses the v1 API because the publisher only supports the legacy model.
        // The remote instance exposes v1 compatibility endpoints.
        var records = await FetchV1DeviceStatusAsync(from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.DeviceStatus, activeTypes,
            records, PublishDeviceStatusAsync, config, ct);
    }

    private async Task SyncActivityAsync(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        var records = await FetchPaginatedAsync<Activity>(
            NocturneRemoteConstants.Activity, from, to, config, ct);

        await PublishRecordTypeAsync(result, SyncDataType.Activity, activeTypes,
            records, PublishActivityDataAsync, config, ct);
    }

    private async Task SyncFoodAsync(
        NocturneRemoteConnectorConfiguration config, SyncResult result,
        HashSet<SyncDataType> activeTypes, CancellationToken ct)
    {
        // Foods endpoint returns a flat array, not PaginatedResponse
        var foods = await FetchOrFailAsync<Food[]>(
            $"{NocturneRemoteConstants.Foods}?count={config.MaxCount}",
            NocturneRemoteConstants.Foods, config, ct);

        // Food carries no per-record time, so no timestamp selector is supplied.
        await PublishRecordTypeAsync(result, SyncDataType.Food, activeTypes,
            foods.ToList(), PublishFoodDataAsync, config, ct);
    }

    #endregion

    #region Fetch Helpers

    /// <summary>
    ///     One payload from the remote instance, fetched under the tenant's retry budget, raising
    ///     <see cref="BaseConnectorService{TConfig}.FetchFailed"/> rather than answering with nothing.
    /// </summary>
    /// <remarks>
    ///     Mirrors the Nightscout connector's fetch layer so both crawls give up on the same
    ///     condition. A transient status is retried; a status the remote keeps returning and a body
    ///     that will not parse are reported as no payload, which becomes the raised failure here. An
    ///     exhausted budget does not reach that point — the retry loop re-throws the last transient
    ///     exception, which already names the status.
    /// </remarks>
    private async Task<T> FetchOrFailAsync<T>(
        string relativeUrl,
        string operationName,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken ct) where T : class
    {
        // Captured rather than logged alone, because the tenant reads the sync card and not the
        // connector logs, and a refused scope is the failure they can actually act on.
        string? refusal = null;

        var payload = await ExecuteWithRetryAsync(
            async () =>
            {
                var response = await GetWithHeadersAsync(BuildAbsoluteUrl(relativeUrl), _authHeaders, ct);

                if (response.IsSuccessStatusCode)
                    return await DeserializeResponseAsync<T>(response, ct);

                refusal = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                throw new HttpRequestException(
                    $"{refusal}: {await ReadFailureBodyAsync(response, config, ct)}",
                    null,
                    response.StatusCode);
            },
            _retryDelayStrategy,
            maxRetries: config.MaxRetryAttempts,
            operationName: operationName,
            cancellationToken: ct);

        return payload ?? throw FetchFailed(operationName, refusal);
    }

    /// <summary>
    ///     As much of a failed response's body as is worth quoting back to the tenant.
    /// </summary>
    /// <remarks>
    ///     The body ends up in the connector's last-error message and its logs, and the remote is the
    ///     tenant's own instance behind their own proxy — an error page that echoes the request would
    ///     otherwise put the bearer token there. The token is removed and the rest is clamped, since
    ///     what identifies the failure is at the front of the body.
    /// </remarks>
    private static async Task<string> ReadFailureBodyAsync(
        HttpResponseMessage response,
        NocturneRemoteConnectorConfiguration config,
        CancellationToken ct)
    {
        const int maxQuotedBody = 200;

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!string.IsNullOrEmpty(config.Token))
            body = body.Replace(config.Token, "[redacted]", StringComparison.Ordinal);

        return body.Length <= maxQuotedBody ? body : body[..maxQuotedBody] + "...";
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
        string endpoint, DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config, CancellationToken ct) where T : class
    {
        var all = new List<T>();
        var offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var page = await FetchOrFailAsync<PaginatedResponse<T>>(
                BuildPaginatedUrl(endpoint, from, to, config.MaxCount, offset), endpoint, config, ct);

            // An envelope that parses supplies an empty page rather than a null one, so this is a
            // remote answering with the envelope and no page in it.
            if (page.Data == null)
                throw FetchFailed(endpoint, "response carried no page");

            var items = page.Data.ToList();
            if (items.Count == 0)
                break;

            all.AddRange(items);

            if (all.Count >= page.Pagination.Total || items.Count < config.MaxCount)
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
        DateTime? from, DateTime? to,
        NocturneRemoteConnectorConfiguration config, CancellationToken ct)
    {
        var allStatuses = new List<DeviceStatus>();
        var currentTo = to;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var statuses = await FetchOrFailAsync<DeviceStatus[]>(
                BuildV1DeviceStatusUrl(from, currentTo, config), V1DeviceStatusEndpoint, config, ct);

            if (statuses.Length == 0)
                break;

            allStatuses.AddRange(statuses);

            if (statuses.Length < config.MaxCount)
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

    private static string BuildV1DeviceStatusUrl(
        DateTime? from, DateTime? to, NocturneRemoteConnectorConfiguration config)
    {
        var url = $"{V1DeviceStatusEndpoint}?count={config.MaxCount}";

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
