using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
/// Abstract base class for authentication token providers.
/// Handles thread-safe token caching, expiry checking, and refresh logic.
/// Derived classes only need to implement the AcquireTokenAsync method.
/// </summary>
/// <typeparam name="TConfig">The connector-specific configuration type</typeparam>
public abstract class AuthTokenProviderBase<TConfig> : IAuthTokenProvider, IDisposable
    where TConfig : IConnectorConfiguration
{
    protected readonly TConfig _config;
    protected readonly HttpClient _httpClient;
    protected readonly ILogger _logger;

    private string? _token;
    private DateTime _tokenExpiresAt = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Default token lifetime buffer in minutes.
    /// Tokens will be refreshed this many minutes before actual expiry to prevent edge cases.
    /// </summary>
    protected virtual int TokenLifetimeBufferMinutes => 5;

    /// <inheritdoc />
    public bool IsTokenExpired => string.IsNullOrEmpty(_token) || DateTime.UtcNow >= _tokenExpiresAt;

    /// <inheritdoc />
    public DateTime? TokenExpiresAt => _tokenExpiresAt == DateTime.MinValue ? null : _tokenExpiresAt;

    protected AuthTokenProviderBase(
        TConfig config,
        HttpClient httpClient,
        ILogger logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            }
            else
            {
                _logger.LogWarning("Failed to acquire token for {ProviderName}", GetType().Name);
            }

            return _token;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error acquiring token for {ProviderName}", GetType().Name);
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

    /// <summary>
    /// Acquires a new authentication token from the external service.
    /// This method is called when the cached token is expired or missing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A tuple containing the token and its expiry time, or (null, DateTime.MinValue) on failure</returns>
    protected abstract Task<(string? Token, DateTime ExpiresAt)> AcquireTokenAsync(CancellationToken cancellationToken);

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _tokenLock.Dispose();
            }
            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
