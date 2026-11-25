using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Services;

/// <summary>
///     A connector that signs in to its source through an <see cref="AuthTokenProviderBase{TConfig}"/>.
///     The base runs the sign-in once before the sync and hands the sync a <see cref="TokenSession"/>, so a
///     sign-in that fails always reaches the tenant with its reason.
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

    /// <summary>
    ///     For what a sync reads off the provider besides the token, such as the cached session's
    ///     metadata. A sync takes its token from its <see cref="TokenSession"/>.
    /// </summary>
    protected TTokenProvider TokenProvider { get; }

    /// <summary>
    ///     Whether this run signs in at all. A run with nothing to fetch answers <c>false</c>, so it
    ///     neither asks the source for a token nor fails on a bad one, and its sync must not read the
    ///     <see cref="TokenSession.Token"/>.
    /// </summary>
    protected virtual bool NeedsSignIn(SyncRequest request, TConfig config) => true;

    /// <summary>A run that does not sign in fetches nothing, so it resumes from nothing.</summary>
    private protected sealed override IEnumerable<ResumeKey> RunResumesFrom(SyncRequest request, TConfig config) =>
        NeedsSignIn(request, config) ? base.RunResumesFrom(request, config) : [];

    /// <summary>
    ///     Work that has to precede the sign-in, such as seeding the provider with stored secrets or
    ///     announcing the step. Runs only when <see cref="NeedsSignIn"/> does.
    /// </summary>
    protected virtual Task PrepareSignInAsync(TConfig config, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     The rest of the sign-in once the source has issued a token, such as reading which account
    ///     the token belongs to. Answering <c>false</c> fails the run as a refused sign-in, with the
    ///     reason recorded through <see cref="BaseConnectorService{TConfig}.TrackFailedAuthentication"/>.
    /// </summary>
    protected virtual Task<bool> CompleteSignInAsync(
        TConfig config, TokenSession session, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <summary>The sign-in is <see cref="PerformSyncInternalAsync"/>'s, so the run has signed in only once it says so.</summary>
    protected sealed override bool SignsInBeforeSync => false;

    /// <summary>
    ///     The sync, with the sign-in already done. Everything <see cref="BaseConnectorService{TConfig}.PerformSyncInternalAsync"/>
    ///     says of a sync holds here.
    /// </summary>
    protected abstract Task<SyncResult> SyncWithTokenAsync(
        SyncRequest request,
        TConfig config,
        TokenSession session,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    protected sealed override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        TConfig config,
        CancellationToken cancellationToken)
    {
        if (!NeedsSignIn(request, config))
            return await SyncWithTokenAsync(request, config, new TokenSession(null, null), cancellationToken);

        await PrepareSignInAsync(config, cancellationToken);

        var token = await ValidTokenAsync(config, cancellationToken);
        if (token is null)
            return AuthenticationFailedResult();

        var session = new TokenSession(token, () => SignInAgainAsync(config, cancellationToken));
        if (!await CompleteSignInAsync(config, session, cancellationToken))
            return AuthenticationFailedResult();

        await SignedInAsync(config, cancellationToken);
        return await SyncWithTokenAsync(request, config, session, cancellationToken);
    }

    /// <summary>
    ///     The run's token, or <c>null</c> once the provider's
    ///     <see cref="AuthTokenProviderBase{TConfig}.SignInFailureReason"/> is recorded, so that
    ///     <see cref="BaseConnectorService{TConfig}.AuthenticationFailedResult"/> reports it.
    /// </summary>
    private async Task<string?> ValidTokenAsync(TConfig config, CancellationToken cancellationToken)
    {
        var token = await TokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (!string.IsNullOrEmpty(token))
            return token;

        TrackFailedAuthentication(TokenProvider.SignInFailureReason);
        return null;
    }

    private Task<string?> SignInAgainAsync(TConfig config, CancellationToken cancellationToken)
    {
        TokenProvider.InvalidateToken();
        return ValidTokenAsync(config, cancellationToken);
    }
}
