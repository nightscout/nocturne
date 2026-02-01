using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     Abstract base class for authentication token providers.
///     Handles thread-safe token caching, expiry checking, and refresh logic.
///     Derived classes only need to implement the AcquireTokenAsync method.
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public abstract class AuthTokenProviderBase<TConfig>(
    TConfig config,
    HttpClient httpClient,
    ILogger logger)
    : IAuthTokenProvider, IDisposable
    where TConfig : IConnectorConfiguration
{
    protected readonly TConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    protected readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    protected readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private bool _disposed;

    private string? _token;
    private DateTime _tokenExpiresAt = DateTime.MinValue;

    /// <summary>
    ///     Default token lifetime buffer in minutes.
    ///     Tokens will be refreshed this many minutes before actual expiry to prevent edge cases.
    /// </summary>
    protected virtual int TokenLifetimeBufferMinutes => 5;

    /// <inheritdoc />
    public bool IsTokenExpired => string.IsNullOrEmpty(_token) || DateTime.UtcNow >= _tokenExpiresAt;

    /// <inheritdoc />
    public DateTime? TokenExpiresAt => _tokenExpiresAt == DateTime.MinValue ? null : _tokenExpiresAt;

    /// <inheritdoc />
    public async Task<string?> GetValidTokenAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: return cached token if still valid
        if (!IsTokenExpired)
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (!IsTokenExpired)
            {
                return _token;
            }

            _logger.LogDebug("Token expired or missing, acquiring new token for {ProviderName}", GetType().Name);

            var result = await AcquireTokenAsync(cancellationToken);

            if (result.Token != null)
            {
                _token = result.Token;
                _tokenExpiresAt = result.ExpiresAt.AddMinutes(-TokenLifetimeBufferMinutes);

                _logger.LogInformation(
                    "Successfully acquired token for {ProviderName}, expires at {ExpiresAt}",
                    GetType().Name,
                    _tokenExpiresAt);

                return _token;
            }

            _logger.LogWarning("Failed to acquire token for {ProviderName}", GetType().Name);
            return _token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring token for {ProviderName} {ex}", GetType().Name, ex);
            return null;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateToken()
    {
        _token = null;
        _tokenExpiresAt = DateTime.MinValue;
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
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the token and its expiry time, or (null, DateTime.MinValue) on failure</returns>
    protected abstract Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken);

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

    protected void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) _tokenLock.Dispose();
        _disposed = true;
    }
}