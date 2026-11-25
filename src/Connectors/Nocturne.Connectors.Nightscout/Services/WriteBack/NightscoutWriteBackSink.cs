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
            if (await SendCreatedAsync(config, batch, ct) is { Count: > 0 } accepted)
                await AfterWrittenAsync(config, accepted, ct);
        }
    }

    public async Task OnCreatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        if (await SendCreatedAsync(config, [item], ct) is { Count: > 0 } accepted)
            await AfterWrittenAsync(config, accepted, ct);
    }

    public async Task OnUpdatedAsync(T item, CancellationToken ct = default)
    {
        var config = await ResolveIfReadyAsync(ct);
        if (config is null || ShouldSkip(item))
            return;

        if (await SendUpdatedAsync(config, item, ct))
            await AfterWrittenAsync(config, [item], ct);
    }

    /// <summary>Sends an updated record upstream: a PUT of it, unless a sink writes it otherwise.</summary>
    /// <returns>Whether upstream accepted the write.</returns>
    protected virtual Task<bool> SendUpdatedAsync(NightscoutConnectorConfiguration config, T item, CancellationToken ct)
        => SendAsync(config, HttpMethod.Put, item, SerializerOptions, ct);

    /// <summary>
    /// The documents a create of <paramref name="batch"/> sends, once what an earlier write left
    /// upstream is tidied where it would refuse them; the batch itself unless a sink sends it otherwise.
    /// </summary>
    protected virtual Task<IReadOnlyList<T>> PrepareAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<T> batch, CancellationToken ct)
        => Task.FromResult(batch);

    /// <summary>
    /// Removes what an earlier write left upstream that refused a create of <paramref name="batch"/>
    /// with <paramref name="refusal"/>, which is then sent once more.
    /// </summary>
    /// <returns>Whether anything was removed.</returns>
    protected virtual Task<bool> ClearRefusalAsync(
        NightscoutConnectorConfiguration config, IReadOnlyList<T> batch, string refusal, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>
    /// Runs once upstream has accepted <paramref name="items"/>, to tidy what an earlier write left there.
    /// It gets every item upstream accepted or already stores, also when others of the same batch failed.
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
            using var request = NewRequest(config, method, pathAndQuery);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            // A read answered says nothing of whether writes land, so only a write resets the breaker.
            if (method != HttpMethod.Get)
                _circuitBreaker.RecordSuccess();
            return body;
        }
        catch (HttpRequestException ex)
        {
            RecordFailure(ex, method);
        }
        catch (TaskCanceledException ex) when (ConnectorFaults.IsTimeout(ex, ct))
        {
            RecordFailure(ex, method);
        }

        return null;
    }

    /// <summary>Counts a failed exchange with the upstream against the circuit breaker, and logs it.</summary>
    protected void RecordFailure(Exception? ex, HttpMethod method)
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

    private static HttpRequestMessage NewRequest(
        NightscoutConnectorConfiguration config, HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, NightscoutAddress.Resolve(config.Url).For(pathAndQuery));
        if (NightscoutConnectorService.ApiSecretHeaderValue(config.ApiSecret) is { } secret)
            request.Headers.Add("api-secret", secret);
        return request;
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

    /// <returns>The items of <paramref name="batch"/> upstream accepted or already stores.</returns>
    private async Task<IReadOnlyList<T>> SendCreatedAsync(
        NightscoutConnectorConfiguration config, List<T> batch, CancellationToken ct, bool cleared = false)
    {
        if (await TrySendAsync(config, HttpMethod.Post, await PrepareAsync(config, batch, ct), SerializerOptions, ct) is not { } outcome)
            return [];
        if (outcome.Refusal is not { } refusal)
            return batch;

        if (!IsAlreadyStored(refusal))
            return await ClearAndResendAsync<IReadOnlyList<T>>(
                config, batch, refusal, cleared, () => SendCreatedAsync(config, batch, ct, cleared: true), [], ct);

        if (batch.Count == 1)
        {
            _circuitBreaker.RecordSuccess();
            return batch;
        }

        var written = new List<T>(batch.Count);
        foreach (var item in batch)
            written.AddRange(await SendCreatedAsync(config, [item], ct));
        return written;
    }

    /// <summary>
    /// POSTs <paramref name="payload"/>, a create of <paramref name="batch"/>. A refusal
    /// <see cref="ClearRefusalAsync"/> clears sends it once more.
    /// </summary>
    /// <returns>Whether upstream accepted it.</returns>
    protected async Task<bool> SendCreateAsync<TPayload>(
        NightscoutConnectorConfiguration config,
        TPayload payload,
        IReadOnlyList<T> batch,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        if (await TrySendAsync(config, HttpMethod.Post, payload, options, ct) is not { } outcome)
            return false;
        if (outcome.Refusal is not { } refusal)
            return true;

        return await ClearAndResendAsync(
            config, batch, refusal, cleared: false, () => SendAsync(config, HttpMethod.Post, payload, options, ct), false, ct);
    }

    /// <returns>What <paramref name="resend"/> came to, or <paramref name="refused"/> when nothing was cleared.</returns>
    private async Task<TResult> ClearAndResendAsync<TResult>(
        NightscoutConnectorConfiguration config,
        IReadOnlyList<T> batch,
        string refusal,
        bool cleared,
        Func<Task<TResult>> resend,
        TResult refused,
        CancellationToken ct)
    {
        if (!cleared && await ClearRefusalAsync(config, batch, refusal, ct))
            return await resend();

        RecordFailure(null, HttpMethod.Post);
        return refused;
    }

    /// <summary>Sends <paramref name="payload"/> to <see cref="Endpoint"/>, recording the outcome on the circuit breaker.</summary>
    /// <returns>Whether upstream accepted it.</returns>
    protected async Task<bool> SendAsync<TPayload>(
        NightscoutConnectorConfiguration config,
        HttpMethod method,
        TPayload payload,
        JsonSerializerOptions? options,
        CancellationToken ct)
    {
        if (await TrySendAsync(config, method, payload, options, ct) is not { } outcome)
            return false;
        if (outcome.Refusal is null)
            return true;

        RecordFailure(null, method);
        return false;
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
            using var request = NewRequest(config, method, Endpoint);
            request.Content = JsonContent.Create(payload, options: options);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new SendOutcome(await response.Content.ReadAsStringAsync(ct));

            _circuitBreaker.RecordSuccess();
            return new SendOutcome(null);
        }
        catch (Exception ex)
        {
            RecordFailure(ex, method);
            return null;
        }
    }
}
