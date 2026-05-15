using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Abstract base class for authentication token providers.
///     Handles thread-safe, per-tenant token caching via <see cref="IConnectorTokenCache"/>.
///     Derived classes only need to implement the AcquireTokenAsync method.
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public abstract class AuthTokenProviderBase<TConfig>(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<TConfig> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger logger)
    : IAuthTokenProvider, IDisposable
    where TConfig : BaseConnectorConfiguration
{
    protected readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    protected readonly IConnectorTokenCache _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));
    protected readonly IConnectorServerResolver<TConfig> _serverResolver = serverResolver ?? throw new ArgumentNullException(nameof(serverResolver));
    protected readonly ITenantAccessor _tenantAccessor = tenantAccessor ?? throw new ArgumentNullException(nameof(tenantAccessor));
    protected readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private bool _disposed;

    /// <summary>
    ///     Default token lifetime buffer in minutes.
    ///     Tokens will be refreshed this many minutes before actual expiry to prevent edge cases.
    /// </summary>
    protected virtual int TokenLifetimeBufferMinutes => 5;

    /// <summary>
    ///     The connector name used as the cache key prefix.
    ///     Concrete providers must supply this.
    /// </summary>
    protected abstract string ConnectorName { get; }

    /// <inheritdoc />
    public bool IsTokenExpired
    {
        get
        {
            if (!_tenantAccessor.IsResolved) return true;
            var cached = _tokenCache.GetAsync(ConnectorName, _tenantAccessor.TenantId).GetAwaiter().GetResult();
            return cached == null;
        }
    }

    /// <inheritdoc />
    public DateTime? TokenExpiresAt
    {
        get
        {
            if (!_tenantAccessor.IsResolved) return null;
            var cached = _tokenCache.GetAsync(ConnectorName, _tenantAccessor.TenantId).GetAwaiter().GetResult();
            return cached?.ExpiresAt;
        }
    }

    /// <inheritdoc />
    public Task<string?> GetValidTokenAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Use the overload that accepts TConfig");
    }

    /// <summary>
    ///     Gets a valid authentication token for the current tenant, refreshing if expired.
    ///     This method is thread-safe with per-tenant locking via the token cache.
    /// </summary>
    public async Task<string?> GetValidTokenAsync(TConfig config, CancellationToken cancellationToken = default)
    {
        if (!_tenantAccessor.IsResolved)
            throw new InvalidOperationException("Connector token request requires a resolved tenant context");

        var tenantId = _tenantAccessor.TenantId;

        // Fast path: check cache
        var cached = await _tokenCache.GetAsync(ConnectorName, tenantId);
        if (cached != null)
            return cached.Token;

        // Acquire per-tenant lock
        var tenantLock = await _tokenCache.GetLockAsync(ConnectorName, tenantId);
        await tenantLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after lock
            cached = await _tokenCache.GetAsync(ConnectorName, tenantId);
            if (cached != null)
                return cached.Token;

            _logger.LogDebug("Token expired or missing, acquiring new token for {ProviderName}", GetType().Name);

            var result = await AcquireTokenAsync(config, cancellationToken);

            if (result.Token != null)
            {
                var expiresAt = result.ExpiresAt.AddMinutes(-TokenLifetimeBufferMinutes);
                await _tokenCache.SetAsync(ConnectorName, tenantId,
                    new ConnectorSession(result.Token, expiresAt, result.Metadata));

                _logger.LogInformation(
                    "Successfully acquired token for {ProviderName}, expires at {ExpiresAt}",
                    GetType().Name, expiresAt);

                return result.Token;
            }

            _logger.LogWarning("Failed to acquire token for {ProviderName}", GetType().Name);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring token for {ProviderName}", GetType().Name);
            return null;
        }
        finally
        {
            tenantLock.Release();
        }
    }

    /// <summary>
    ///     Returns the cached session for the current tenant, or null if not cached.
    ///     Used by connector services that need to read metadata (e.g. session cookies, user data).
    /// </summary>
    public async Task<ConnectorSession?> GetCachedSessionAsync()
    {
        if (!_tenantAccessor.IsResolved) return null;
        return await _tokenCache.GetAsync(ConnectorName, _tenantAccessor.TenantId);
    }

    /// <inheritdoc />
    public void InvalidateToken()
    {
        if (_tenantAccessor.IsResolved)
            _tokenCache.Invalidate(ConnectorName, _tenantAccessor.TenantId);
        _logger.LogDebug("Token invalidated for {ProviderName}", GetType().Name);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Acquires a new authentication token from the external service.
    ///     This method is called when the cached token is expired or missing.
    /// </summary>
    /// <param name="config">The per-tenant connector configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the token, its expiry time, and optional metadata</returns>
    protected abstract Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        TConfig config, CancellationToken cancellationToken);

    protected async Task<T?> ExecuteWithRetryAsync<T>(
        Func<int, Task<(T? Result, bool ShouldRetry)>> operation,
        IRetryDelayStrategy retryDelayStrategy,
        int maxRetries,
        string operationName,
        CancellationToken cancellationToken)
        where T : class
    {
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var (result, shouldRetry) = await operation(attempt);
                if (result != null)
                    return result;

                if (!shouldRetry)
                    return null;

                if (attempt < maxRetries - 1)
                    await retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "HTTP error during {OperationName} attempt {Attempt}",
                    operationName,
                    attempt + 1);

                if (attempt < maxRetries - 1)
                    await retryDelayStrategy.ApplyRetryDelayAsync(attempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Unexpected error during {OperationName} attempt {Attempt}",
                    operationName,
                    attempt + 1);
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        _logger.LogError("{OperationName} failed after {MaxRetries} attempts", operationName, maxRetries);
        return null;
    }

    /// <summary>
    ///     Reads the error response body from a failed HTTP response, logs it with the appropriate
    ///     severity based on whether the error is retryable, and returns whether a retry is warranted.
    ///     This consolidates the common error handling pattern used across connector token providers.
    /// </summary>
    /// <param name="response">The failed HTTP response (caller must verify !IsSuccessStatusCode before calling)</param>
    /// <param name="operationName">A human-readable name for the operation, used in log messages</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the error is retryable and the caller should retry; false otherwise</returns>
    protected async Task<bool> HandleErrorResponseAsync(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsRetryableError())
        {
            _logger.LogWarning(
                "{OperationName} failed with retryable error: {StatusCode} - {Error}",
                operationName,
                response.StatusCode,
                errorContent);
            return true;
        }

        _logger.LogError(
            "{OperationName} failed with non-retryable error: {StatusCode} - {Error}",
            operationName,
            response.StatusCode,
            errorContent);
        return false;
    }

    protected void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
    }
}
