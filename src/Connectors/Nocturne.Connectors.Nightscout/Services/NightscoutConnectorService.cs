using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;

namespace Nocturne.Connectors.Nightscout.Services;

public class NightscoutConnectorService : BaseConnectorService<NightscoutConnectorConfiguration>
{
    private readonly IRetryDelayStrategy _retryDelayStrategy;
    private readonly IRateLimitingStrategy _rateLimitingStrategy;
    private readonly NightscoutConnectorConfiguration _config;
    private string? _apiSecretHash;

    public NightscoutConnectorService(
        HttpClient httpClient,
        ILogger<NightscoutConnectorService> logger,
        IRetryDelayStrategy retryDelayStrategy,
        IRateLimitingStrategy rateLimitingStrategy,
        NightscoutConnectorConfiguration config,
        IConnectorPublisher? publisher = null
    )
        : base(httpClient, logger, publisher)
    {
        _retryDelayStrategy = retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));
        _rateLimitingStrategy = rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    protected override string ConnectorSource => DataSources.NightscoutConnector;
    public override string ServiceName => "Nightscout";

    public override List<SyncDataType> SupportedDataTypes =>
        _config.SyncTreatments
            ? [SyncDataType.Glucose, SyncDataType.Treatments]
            : [SyncDataType.Glucose];

    public override async Task<bool> AuthenticateAsync()
    {
        EnsureBaseAddress();

        if (string.IsNullOrEmpty(_config.ApiSecret))
        {
            _logger.LogError(
                "[{ConnectorSource}] API secret is not configured",
                ConnectorSource);
            TrackFailedRequest("API secret is not configured");
            return false;
        }

        _apiSecretHash = ComputeApiSecretHash(_config.ApiSecret);

        _logger.LogDebug(
            "[{ConnectorSource}] Authenticating with Nightscout at {Url}",
            ConnectorSource,
            _httpClient.BaseAddress);

        try
        {
            var headers = GetAuthHeaders();
            var response = await GetWithHeadersAsync("/api/v1/entries.json?count=1", headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    "[{ConnectorSource}] Nightscout auth check returned HTTP {StatusCode}: {Body}",
                    ConnectorSource,
                    (int)response.StatusCode,
                    body);
                TrackFailedRequest($"Nightscout auth check failed: HTTP {(int)response.StatusCode}");
                return false;
            }

            TrackSuccessfulRequest();
            _logger.LogInformation(
                "[{ConnectorSource}] Successfully authenticated with Nightscout instance",
                ConnectorSource);
            return true;
        }
        catch (Exception ex)
        {
            TrackFailedRequest($"Nightscout authentication failed: {ex.Message}");
            _logger.LogError(ex,
                "[{ConnectorSource}] Failed to connect to Nightscout instance at {Url}",
                ConnectorSource,
                _httpClient.BaseAddress);
            return false;
        }
    }

    public override async Task<SyncResult> SyncDataAsync(
        SyncRequest request,
        NightscoutConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        if (!await AuthenticateAsync())
        {
            return new SyncResult
            {
                Success = false,
                Message = "Authentication failed"
            };
        }

        return await base.SyncDataAsync(request, config, cancellationToken);
    }

    public override async Task<IEnumerable<Entry>> FetchGlucoseDataAsync(DateTime? since = null)
    {
        return await FetchGlucoseDataRangeAsync(since, null);
    }

    protected override async Task<IEnumerable<Entry>> FetchGlucoseDataRangeAsync(
        DateTime? from, DateTime? to)
    {
        var entries = await FetchDataAsync<Entry[]>(
            BuildEntriesUrl(from, to),
            "FetchGlucoseData");

        if (entries == null) return [];

        foreach (var entry in entries)
        {
            entry.DataSource = ConnectorSource;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} glucose entries from Nightscout",
            ConnectorSource,
            entries.Length);

        return entries;
    }

    protected override async Task<IEnumerable<Treatment>> FetchTreatmentsAsync(
        DateTime? from, DateTime? to)
    {
        var treatments = await FetchDataAsync<Treatment[]>(
            BuildTreatmentsUrl(from, to),
            "FetchTreatments");

        if (treatments == null) return [];

        foreach (var treatment in treatments)
        {
            treatment.DataSource = ConnectorSource;
        }

        _logger.LogInformation(
            "[{ConnectorSource}] Retrieved {Count} treatments from Nightscout",
            ConnectorSource,
            treatments.Length);

        return treatments;
    }

    private async Task<T?> FetchDataAsync<T>(string url, string operationName) where T : class
    {
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        return await ExecuteWithRetryAsync(
            async () => await FetchDataCoreAsync<T>(url),
            _retryDelayStrategy,
            operationName: operationName);
    }

    private async Task<T?> FetchDataCoreAsync<T>(string url) where T : class
    {
        var headers = GetAuthHeaders();
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

    private string BuildEntriesUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/entries.json?count={_config.MaxCount}";

        if (from.HasValue)
        {
            var fromMs = new DateTimeOffset(from.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$gte]={fromMs}";
        }

        if (to.HasValue)
        {
            var toMs = new DateTimeOffset(to.Value, TimeSpan.Zero).ToUnixTimeMilliseconds();
            url += $"&find[date][$lte]={toMs}";
        }

        return url;
    }

    private string BuildTreatmentsUrl(DateTime? from, DateTime? to)
    {
        var url = $"/api/v1/treatments.json?count={_config.MaxCount}";

        if (from.HasValue)
            url += $"&find[created_at][$gte]={from.Value.ToUniversalTime():o}";

        if (to.HasValue)
            url += $"&find[created_at][$lte]={to.Value.ToUniversalTime():o}";

        return url;
    }

    private void EnsureBaseAddress()
    {
        if (_httpClient.BaseAddress != null)
            return;

        if (string.IsNullOrEmpty(_config.Url))
            throw new InvalidOperationException("Nightscout URL is not configured");

        var url = _config.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? _config.Url
            : $"https://{_config.Url}";

        _httpClient.BaseAddress = new Uri(url);
    }

    private Dictionary<string, string> GetAuthHeaders()
    {
        return new Dictionary<string, string>
        {
            ["api-secret"] = _apiSecretHash ?? ComputeApiSecretHash(_config.ApiSecret)
        };
    }

    internal static string ComputeApiSecretHash(string apiSecret)
    {
        if (IsAlreadySha1Hash(apiSecret))
            return apiSecret.ToLowerInvariant();

        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(apiSecret));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsAlreadySha1Hash(string value)
    {
        return value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));
    }
}
