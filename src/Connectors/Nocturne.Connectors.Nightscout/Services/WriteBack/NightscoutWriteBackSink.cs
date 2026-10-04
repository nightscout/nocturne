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

    /// <summary>
    /// Whether the body a write was refused with says the upstream already stores what it was sent.
    /// A batch refused that way is sent again one record at a time, and a lone record refused that
    /// way counts as written.
    /// </summary>
    protected virtual bool IsAlreadyStored(string refusal) => false;

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
            await SendCreatedAsync(config, batch, ct);
        }
    }

    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        await SendCreatedAsync(config, [item], ct);
    }

    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        await SendUpdatedAsync(config, item, ct);
    }

    public Task OnDeletedAsync(T? item, CancellationToken ct = default)
    {
        // Nightscout v1 DELETE requires an ID, not the full object.
        // Since the interface only gives us the item (which may be null after deletion),
        // we skip write-back for deletes. The bidirectional sync handles this
        // through the connector's next poll cycle.
        return Task.CompletedTask;
    }

    /// <summary>Sends an updated record upstream: a PUT of it, unless a sink writes it otherwise.</summary>
    protected virtual Task SendUpdatedAsync(NightscoutConnectorConfiguration config, T item, CancellationToken ct)
        => SendAsync(config, HttpMethod.Put, item, SerializerOptions, ct);

    /// <summary>Sends <paramref name="payload"/> to <see cref="Endpoint"/>, recording the outcome on the circuit breaker.</summary>
    protected async Task SendAsync<TPayload>(
        NightscoutConnectorConfiguration config,
        HttpMethod method,
        TPayload payload,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        if (await TrySendAsync(config, method, payload, options, ct) is { Refusal: not null })
            RecordFailure(method, null);
    }

    /// <summary>
    /// Reads <paramref name="pathAndQuery"/> from the upstream, for a sink that must know what the
    /// upstream holds before it writes. A read that fails counts against the circuit breaker, as a
    /// failed write does: the sink cannot write without its answer.
    /// </summary>
    /// <returns>The response body, or null when the read failed.</returns>
    protected async Task<string?> GetAsync(
        NightscoutConnectorConfiguration config, string pathAndQuery, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ResolveAbsoluteUrl(config.Url, pathAndQuery));
            request.Headers.Add("api-secret", NightscoutConnectorService.ComputeApiSecretHash(config.ApiSecret));
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            RecordFailure(HttpMethod.Get, null);
            return null;
        }
        catch (Exception ex)
        {
            RecordFailure(HttpMethod.Get, ex);
            return null;
        }
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

    private async Task SendCreatedAsync(NightscoutConnectorConfiguration config, List<T> batch, CancellationToken ct)
    {
        if (await TrySendAsync(config, HttpMethod.Post, batch, SerializerOptions, ct) is not { Refusal: { } refusal })
            return;

        if (!IsAlreadyStored(refusal))
        {
            RecordFailure(HttpMethod.Post, null);
            return;
        }

        if (batch.Count == 1)
        {
            _circuitBreaker.RecordSuccess();
            return;
        }

        foreach (var item in batch)
            await SendCreatedAsync(config, [item], ct);
    }

    /// <summary>What an upstream write came to; a transport failure is recorded where it happens.</summary>
    /// <param name="Refusal">The body a non-success response carried, or null when it was accepted.</param>
    private readonly record struct SendOutcome(string? Refusal);

    /// <returns>The outcome of a write the upstream answered, or null when the request failed.</returns>
    private async Task<SendOutcome?> TrySendAsync<TPayload>(
        NightscoutConnectorConfiguration config,
        HttpMethod method,
        TPayload payload,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, ResolveAbsoluteUrl(config.Url, Endpoint));
            request.Headers.Add(
                "api-secret",
                NightscoutConnectorService.ComputeApiSecretHash(config.ApiSecret));
            request.Content = JsonContent.Create(payload, options: options);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new SendOutcome(await response.Content.ReadAsStringAsync(ct));

            _circuitBreaker.RecordSuccess();
            return new SendOutcome(null);
        }
        catch (Exception ex)
        {
            RecordFailure(method, ex);
            return null;
        }
    }

    /// <summary>Counts a failed exchange with the upstream against the circuit breaker, and logs it.</summary>
    protected void RecordFailure(HttpMethod method, Exception? ex)
    {
        _circuitBreaker.RecordFailure();
        _logger.LogWarning(
            ex,
            "Nightscout write-back failed for {Method} {Endpoint}",
            method,
            Endpoint);
    }
}
