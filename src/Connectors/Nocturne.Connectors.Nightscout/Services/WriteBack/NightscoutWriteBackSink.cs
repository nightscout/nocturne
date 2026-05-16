using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Core.Contracts.Events;

namespace Nocturne.Connectors.Nightscout.Services.WriteBack;

/// <summary>
/// Abstract base class for Nightscout write-back event sinks.
/// POSTs, PUTs, or DELETEs data back to the upstream Nightscout instance
/// to maintain bidirectional sync during the migration period.
/// </summary>
public abstract class NightscoutWriteBackSink<T> : IDataEventSink<T>
{
    private readonly HttpClient _httpClient;
    private readonly NightscoutConnectorConfiguration _config;
    private readonly NightscoutCircuitBreaker _circuitBreaker;
    private readonly ILogger _logger;

    protected NightscoutWriteBackSink(
        HttpClient httpClient,
        NightscoutConnectorConfiguration config,
        NightscoutCircuitBreaker circuitBreaker,
        ILogger logger)
    {
        _httpClient = httpClient;
        _config = config;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
    }

    /// <summary>
    /// The Nightscout API v1 endpoint path (e.g. "/api/v1/entries").
    /// </summary>
    protected abstract string Endpoint { get; }

    /// <summary>
    /// Override to filter items that should not be written back (e.g. loop prevention).
    /// </summary>
    protected virtual bool ShouldSkip(T item) => false;

    public async Task OnCreatedAsync(IReadOnlyList<T> items, CancellationToken ct = default)
    {
        if (!ShouldProceed())
            return;

        var filtered = FilterItems(items);
        if (filtered.Count == 0)
            return;

        // Batch into configured batch size
        for (var i = 0; i < filtered.Count; i += _config.WriteBackBatchSize)
        {
            var batch = filtered.Skip(i).Take(_config.WriteBackBatchSize).ToList();
            await SendAsync(HttpMethod.Post, Endpoint, batch, ct);
        }
    }

    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        if (!ShouldProceed() || ShouldSkip(item))
            return;

        await SendAsync(HttpMethod.Post, Endpoint, new[] { item }, ct);
    }

    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        if (!ShouldProceed() || ShouldSkip(item))
            return;

        await SendAsync(HttpMethod.Put, Endpoint, item, ct);
    }

    public Task OnDeletedAsync(T? item, CancellationToken ct = default)
    {
        // Nightscout v1 DELETE requires an ID, not the full object.
        // Since the interface only gives us the item (which may be null after deletion),
        // we skip write-back for deletes. The bidirectional sync handles this
        // through the connector's next poll cycle.
        return Task.CompletedTask;
    }

    private bool ShouldProceed()
    {
        if (!_config.WriteBackEnabled)
            return false;

        if (_circuitBreaker.IsOpen)
        {
            _logger.LogDebug(
                "Nightscout write-back circuit breaker is open, skipping {Endpoint}",
                Endpoint);
            return false;
        }

        return true;
    }

    private static string ResolveAbsoluteUrl(string configUrl, string endpoint)
    {
        var baseUrl = configUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? configUrl
            : $"https://{configUrl}";
        return $"{baseUrl.TrimEnd('/')}{endpoint}";
    }

    private List<T> FilterItems(IReadOnlyList<T> items)
    {
        var filtered = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (!ShouldSkip(item))
                filtered.Add(item);
        }

        return filtered;
    }

    private async Task SendAsync<TPayload>(
        HttpMethod method,
        string endpoint,
        TPayload payload,
        CancellationToken ct)
    {
        try
        {
            var absoluteUrl = ResolveAbsoluteUrl(_config.Url, endpoint);
            using var request = new HttpRequestMessage(method, absoluteUrl);
            request.Headers.Add(
                "api-secret",
                NightscoutConnectorService.ComputeApiSecretHash(_config.ApiSecret));
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            _circuitBreaker.RecordSuccess();
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(
                ex,
                "Nightscout write-back failed for {Method} {Endpoint}",
                method,
                endpoint);
        }
    }
}
