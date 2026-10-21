using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Utilities;
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
    private readonly IConnectorConfigurationLoader<NightscoutConnectorConfiguration> _configLoader;
    private readonly NightscoutCircuitBreaker _circuitBreaker;
    private readonly ILogger _logger;

    // Cached per sink instance. Sinks are transient (resolved from a scope per request),
    // so this avoids repeated DB reads within a single request that writes multiple entities.
    private NightscoutConnectorConfiguration? _cachedConfig;

    protected NightscoutWriteBackSink(
        HttpClient httpClient,
        IConnectorConfigurationLoader<NightscoutConnectorConfiguration> configLoader,
        NightscoutCircuitBreaker circuitBreaker,
        ILogger logger)
    {
        _httpClient = httpClient;
        _configLoader = configLoader;
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

    /// <summary>
    /// Options the payload is serialized with; null keeps <see cref="JsonContent"/>'s web defaults.
    /// </summary>
    protected virtual JsonSerializerOptions? SerializerOptions => null;

    public async Task OnCreatedAsync(IReadOnlyList<T> items, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null)
            return;

        var filtered = FilterItems(items);
        if (filtered.Count == 0)
            return;

        // WriteBackBatchSize is bound straight from the tenant's configuration row and
        // its declared minimum of 1 only reaches the UI schema, so a zero or negative
        // value can arrive here. Either would leave the loop index standing still and
        // POST at the tenant's Nightscout without end.
        var batchSize = Math.Max(1, config.WriteBackBatchSize);

        for (var i = 0; i < filtered.Count; i += batchSize)
        {
            var batch = filtered.Skip(i).Take(batchSize).ToList();
            if (await SendAsync(config, HttpMethod.Post, Endpoint, batch, ct))
                await AfterWrittenAsync(config, batch, ct);
        }
    }

    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        if (await SendAsync(config, HttpMethod.Post, Endpoint, new[] { item }, ct))
            await AfterWrittenAsync(config, [item], ct);
    }

    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        if (await SendAsync(config, HttpMethod.Put, Endpoint, item, ct))
            await AfterWrittenAsync(config, [item], ct);
    }

    /// <summary>
    /// Runs once upstream has accepted <paramref name="items"/>, to tidy what an earlier write left there.
    /// </summary>
    protected virtual Task AfterWrittenAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<T> items, CancellationToken ct)
        => Task.CompletedTask;

    /// <returns>The response body, or null when the request failed (counted against the breaker).</returns>
    protected async Task<string?> ExchangeAsync(
        NightscoutConnectorConfiguration config, HttpMethod method, string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, ResolveAbsoluteUrl(config.Url, pathAndQuery));
            request.Headers.Add(
                "api-secret",
                NightscoutConnectorService.ComputeApiSecretHash(config.ApiSecret));

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            _circuitBreaker.RecordSuccess();
            return body;
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(ex, method);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure(ex, method);
        }

        return null;
    }

    private void RecordFailure(Exception ex, HttpMethod method)
    {
        _circuitBreaker.RecordFailure();
        _logger.LogWarning(ex, "Nightscout write-back failed for {Method} {Endpoint}", method, Endpoint);
    }

    public Task OnDeletedAsync(T? item, CancellationToken ct = default)
    {
        // Nightscout v1 DELETE requires an ID, not the full object.
        // Since the interface only gives us the item (which may be null after deletion),
        // we skip write-back for deletes. The bidirectional sync handles this
        // through the connector's next poll cycle.
        return Task.CompletedTask;
    }

    private async Task<NightscoutConnectorConfiguration?> ResolveIfReadyAsync(CancellationToken ct)
    {
        var config = _cachedConfig ??= await _configLoader.LoadForTenantAsync(ct);

        if (!config.WriteBackEnabled)
            return null;

        if (_circuitBreaker.IsOpen)
        {
            _logger.LogDebug(
                "Nightscout write-back circuit breaker is open, skipping {Endpoint}",
                Endpoint);
            return null;
        }

        return config;
    }

    private static string ResolveAbsoluteUrl(string configUrl, string endpoint)
        => $"{ConnectorUrl.ResolveBase(configUrl, "Nightscout")}{endpoint}";

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

    private async Task<bool> SendAsync<TPayload>(
        NightscoutConnectorConfiguration config,
        HttpMethod method,
        string endpoint,
        TPayload payload,
        CancellationToken ct)
    {
        try
        {
            var absoluteUrl = ResolveAbsoluteUrl(config.Url, endpoint);
            using var request = new HttpRequestMessage(method, absoluteUrl);
            request.Headers.Add(
                "api-secret",
                NightscoutConnectorService.ComputeApiSecretHash(config.ApiSecret));
            request.Content = JsonContent.Create(payload, options: SerializerOptions);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            _circuitBreaker.RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            _circuitBreaker.RecordFailure();
            _logger.LogWarning(
                ex,
                "Nightscout write-back failed for {Method} {Endpoint}",
                method,
                endpoint);
            return false;
        }
    }
}
