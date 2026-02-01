using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Configurations;
using Nocturne.Connectors.Dexcom.Mappers;
using Nocturne.Connectors.Dexcom.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Dexcom.Services;

/// <summary>
///     Connector service for Dexcom Share data source
///     Enhanced implementation based on the original nightscout-connect Dexcom Share implementation
/// </summary>
public class DexcomConnectorService : BaseConnectorService<DexcomConnectorConfiguration>
{
    private readonly DexcomEntryMapper _entryMapper;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly DexcomAuthTokenProvider _tokenProvider;

    public DexcomConnectorService(
        HttpClient httpClient,
        ILogger<DexcomConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        DexcomAuthTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _retryDelayStrategy =
            retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy =
            rateLimitingStrategy
            ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _tokenProvider =
            tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _entryMapper = new DexcomEntryMapper(logger, ConnectorSource);
    }

    protected override string ConnectorSource => DataSources.DexcomConnector;
    public override string ServiceName => "Dexcom Share";
    public override List<SyncDataType> SupportedDataTypes => [SyncDataType.Glucose];

    public override async Task<bool> AuthenticateAsync()
    {
        var token = await _tokenProvider.GetValidTokenAsync();
        if (token == null)
        {
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        var batchData = await FetchBatchDataAsync(since);
        var fetchGlucoseDataAsync = batchData == null
            ? []
            : _entryMapper.TransformBatchDataToEntries(batchData).ToList();
        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} glucose entries from Dexcom",
            ConnectorSource,
            fetchGlucoseDataAsync.Count()
        );

        return fetchGlucoseDataAsync;
    }

    private async Task<DexcomEntry[]?> FetchBatchDataAsync(DateTime? since = null)
    {
        // Get valid session token from provider
        var sessionId = await _tokenProvider.GetValidTokenAsync();
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning(
                "[{ConnectorSource}] Failed to get valid session, authentication failed",
                ConnectorSource
            );
            TrackFailedRequest("Failed to get valid session");
            return null;
        }

        // Apply rate limiting
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchRawDataCoreAsync(sessionId, since),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                var newToken = await _tokenProvider.GetValidTokenAsync();
                if (string.IsNullOrEmpty(newToken)) return false;
                sessionId = newToken;
                return true;
            },
            operationName: "FetchDexcomData"
        );

        // Log batch data summary
        if (result == null) return result;
        var validEntries = result.Where(e => e.Value > 0).ToArray();
        var minDate = validEntries.Length > 0 ? validEntries.Min(e => e.Wt) : "N/A";
        var maxDate = validEntries.Length > 0 ? validEntries.Max(e => e.Wt) : "N/A";

        _logger.LogInformation(
            "[{ConnectorSource}] Fetched Dexcom batch data: TotalEntries={TotalCount}, ValidEntries={ValidCount}, DateRange={MinDate} to {MaxDate}",
            ConnectorSource,
            result.Length,
            validEntries.Length,
            minDate,
            maxDate
        );

        return result;
    }

    /// <summary>
    ///     Core data fetch logic without retry handling (called by ExecuteWithRetryAsync)
    /// </summary>
    private async Task<DexcomEntry[]?> FetchRawDataCoreAsync(
        string sessionId,
        DateTime? since = null
    )
    {
        // Calculate time range
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        var startTime = since.HasValue
            ? since.Value > twoDaysAgo ? since.Value : twoDaysAgo
            : twoDaysAgo;

        var timeDiff = DateTime.UtcNow - startTime;
        var maxCount = Math.Ceiling(timeDiff.TotalMinutes / 5); // 5-minute intervals
        var minutes = (int)(maxCount * 5);

        var url =
            $"/ShareWebServices/Services/Publisher/ReadPublisherLatestGlucoseValues?sessionID={sessionId}&minutes={minutes}&maxCount={(int)maxCount}";

        var response = await _httpClient.PostAsync(
            url,
            new StringContent("{}", Encoding.UTF8, "application/json")
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode
            );
        }

        var dexcomEntries = await DeserializeResponseAsync<DexcomEntry[]>(response);
        return dexcomEntries ?? [];
    }
}