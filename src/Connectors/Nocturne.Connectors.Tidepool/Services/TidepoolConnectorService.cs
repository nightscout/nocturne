using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Configurations;
using Nocturne.Connectors.Tidepool.Mappers;
using Nocturne.Connectors.Tidepool.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Tidepool.Services;

/// <summary>
///     Connector service for Tidepool data source.
///     Fetches glucose readings, boluses, food entries, and exercise data.
/// </summary>
public class TidepoolConnectorService : BaseConnectorService<TidepoolConnectorConfiguration>
{
    private readonly TidepoolEntryMapper _entryMapper;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly TidepoolAuthTokenProvider _tokenProvider;
    private readonly TidepoolTreatmentMapper _treatmentMapper;

    public TidepoolConnectorService(
        HttpClient httpClient,
        ILogger<TidepoolConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        TidepoolAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy =
            rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _entryMapper = new TidepoolEntryMapper(logger, ConnectorSource);
        _treatmentMapper = new TidepoolTreatmentMapper(logger, ConnectorSource);
    }

    protected override string ConnectorSource => DataSources.TidepoolConnector;
    public override string ServiceName => "Tidepool";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose, SyncDataType.Treatments];

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (token == null)
        {
            TrackFailedRequest("Failed to get valid Tidepool session token");
            return false;
        }

        if (string.IsNullOrEmpty(_tokenProvider.UserId))
        {
            TrackFailedRequest("Tidepool user ID not available after authentication");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        var bgValues = await FetchDataAsync<TidepoolBgValue[]>(
            $"{TidepoolConstants.DataTypes.Cbg},{TidepoolConstants.DataTypes.Smbg}",
            since);

        if (bgValues == null) return [];

        var entries = _entryMapper.MapBgValues(bgValues).ToList();
        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} glucose entries from Tidepool",
            ConnectorSource,
            entries.Count);

        return entries;
    }

    protected override async Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
        DateTime? from, DateTime? to)
    {
        var bgValues = await FetchDataAsync<TidepoolBgValue[]>(
            $"{TidepoolConstants.DataTypes.Cbg},{TidepoolConstants.DataTypes.Smbg}",
            from, to);

        if (bgValues == null) return [];

        return _entryMapper.MapBgValues(bgValues);
    }

    protected override async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
        DateTime? from, DateTime? to)
    {
        // Fetch boluses, food, and activities in parallel
        var bolusTask = FetchDataAsync<TidepoolBolus[]>(
            TidepoolConstants.DataTypes.Bolus, from, to);
        var foodTask = FetchDataAsync<TidepoolFood[]>(
            TidepoolConstants.DataTypes.Food, from, to);
        var activityTask = FetchDataAsync<TidepoolPhysicalActivity[]>(
            TidepoolConstants.DataTypes.PhysicalActivity, from, to);

        await Task.WhenAll(bolusTask, foodTask, activityTask);

        var boluses = await bolusTask;
        var foods = await foodTask;
        var activities = await activityTask;

        var treatments = _treatmentMapper.MapTreatments(boluses, foods, activities).ToList();

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} treatments from Tidepool (boluses: {Boluses}, food: {Food}, activities: {Activities})",
            ConnectorSource,
            treatments.Count,
            boluses?.Length ?? 0,
            foods?.Length ?? 0,
            activities?.Length ?? 0);

        return treatments;
    }

    /// <summary>
    ///     Fetches typed data from the Tidepool API data endpoint.
    /// </summary>
    private async Task<T?> FetchDataAsync<T>(
        string dataType, DateTime? startDate = null, DateTime? endDate = null) where T : class
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_tokenProvider.UserId))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Cannot fetch data: missing token or user ID",
                ConnectorSource);
            return null;
        }

        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(token, dataType, startDate, endDate),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync();
                if (string.IsNullOrEmpty(newToken)) return false;
                token = newToken;
                return true;
            },
            operationName: $"FetchTidepoolData({dataType})"
        );
    }

    private async Task<T?> FetchDataCoreAsync<T>(
        string token, string dataType, DateTime? startDate, DateTime? endDate) where T : class
    {
        var userId = _tokenProvider.UserId;
        var url = $"/data/{userId}?type={dataType}";

        if (startDate.HasValue)
            url += $"&startDate={startDate.Value.ToUniversalTime():o}";
        if (endDate.HasValue)
            url += $"&endDate={endDate.Value.ToUniversalTime():o}";

        var headers = new Dictionary<string, string>
        {
            [TidepoolConstants.Headers.SessionToken] = token
        };

        var response = await GetWithHeadersAsync(url, headers);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode);
        }

        return await DeserializeResponseAsync<T>(response);
    }
}
