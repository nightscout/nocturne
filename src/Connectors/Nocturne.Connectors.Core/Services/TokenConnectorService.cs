using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     A connector that signs in to its source through an <see cref="AuthTokenProviderBase{TConfig}"/>.
/// </summary>
public abstract class TokenConnectorService<TConfig, TTokenProvider> : BaseConnectorService<TConfig>
    where TConfig : BaseConnectorConfiguration
    where TTokenProvider : AuthTokenProviderBase<TConfig>
{
    protected TokenConnectorService(
        HttpClient httpClient,
        IConnectorServerResolver<TConfig> serverResolver,
        ILogger logger,
        IRetryDelayStrategy retryDelayStrategy,
        TTokenProvider tokenProvider,
        IConnectorPublisher? publisher = null,
        TimeProvider? timeProvider = null
    )
        : base(httpClient, serverResolver, logger, retryDelayStrategy, publisher, timeProvider)
    {
        TokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
    }

    protected TTokenProvider TokenProvider { get; }

    /// <summary>
    ///     The run's token, or <c>null</c> once the provider's
    ///     <see cref="AuthTokenProviderBase{TConfig}.SignInFailureReason"/> is recorded, so that
    ///     <see cref="BaseConnectorService{TConfig}.AuthenticationFailedResult"/> reports it.
    /// </summary>
    protected async Task<string?> ValidTokenAsync(TConfig config, CancellationToken cancellationToken)
    {
        var token = await TokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (!string.IsNullOrEmpty(token))
            return token;

        TrackFailedAuthentication(TokenProvider.SignInFailureReason);
        return null;
    }

    /// <summary>
    ///     The <c>reAuthenticateOnUnauthorized</c> answer to
    ///     <see cref="BaseConnectorService{TConfig}.ExecuteWithRetryAsync{T}"/>: drop the token the
    ///     source refused, sign in again, and hand the retry the new one through
    ///     <paramref name="useToken"/>. A failed sign-in leaves the caller's token as it was.
    /// </summary>
    protected Func<Task<bool>> ReauthenticateWith(
        TConfig config,
        Action<string> useToken,
        CancellationToken cancellationToken) => async () =>
    {
        TokenProvider.InvalidateToken();
        var token = await TokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (string.IsNullOrEmpty(token)) return false;
        useToken(token);
        return true;
    };
}
