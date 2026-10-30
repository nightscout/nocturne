using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;
using Nocturne.API.Services.Auth;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for opaque direct grant tokens.
/// Validates tokens by SHA-256 hashing and looking up the grant in the database, falling back to
/// the digest-prefix rule for a token imported from a classic Nightscout instance.
/// Accepts the token via the <c>Authorization: Bearer</c> header or the Nightscout-style
/// <c>?token=</c> query parameter (how xDrip4iOS and other Nightscout uploaders send it).
/// Skips JWT-formatted tokens (starting with "eyJ") to let other handlers process them.
/// </summary>
public class DirectGrantTokenHandler : IAuthHandler
{
    /// <summary>
    /// Prefix identifying opaque direct grant tokens (see <see cref="Controllers.Authentication.DirectGrantController"/>).
    /// </summary>
    internal const string TokenPrefix = "noc_";

    /// <summary>
    /// Handler priority (150 - after session cookies, before OIDC/legacy JWT)
    /// </summary>
    public int Priority => 150;

    /// <summary>
    /// Handler name for logging
    /// </summary>
    public string Name => "DirectGrantTokenHandler";

    private readonly IOAuthGrantRepository _grants;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DirectGrantTokenHandler> _logger;

    /// <summary>
    /// Creates a new instance of DirectGrantTokenHandler
    /// </summary>
    /// <param name="grants">The stored grants, pinned per read to the request's tenant.</param>
    public DirectGrantTokenHandler(
        IOAuthGrantRepository grants,
        TimeProvider timeProvider,
        ILogger<DirectGrantTokenHandler> logger)
    {
        _grants = grants;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var (token, rawToken) = ExtractToken(context);
        if (string.IsNullOrEmpty(token))
        {
            return AuthResult.Skip();
        }

        // Deliberately looser than TokenFormat.IsJwt: a malformed JWT still belongs to the JWT
        // handlers, and no noc_ token can open with a Base64-URL JSON header.
        if (token.StartsWith("eyJ", StringComparison.Ordinal))
        {
            return AuthResult.Skip();
        }

        // Direct grants are tenant-scoped — only match grants for the resolved tenant
        var tenantCtx = context.GetTenantContext();
        if (tenantCtx is null)
        {
            return AuthResult.Skip();
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (await FindActiveGrantAsync(
                _grants, token, tenantCtx.TenantId, now, context.RequestAborted, rawToken)
            is not var (grant, membership))
        {
            _logger.LogDebug("No matching direct grant found for token");
            return AuthResult.Skip();
        }

        new RequestMembership(grant.SubjectId, tenantCtx.TenantId, MembershipFacts.From(membership)).StoreOn(context);

        // Update last used metadata (fire and forget via separate context)
        if (LastUsedStamp.IsDue(grant.LastUsedAt, now))
        {
            _ = RecordLastUsedAsync(
                _grants, _logger, grant.Id, tenantCtx.TenantId,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.Headers.UserAgent.FirstOrDefault());
        }

        _logger.LogDebug("Direct grant authentication successful for grant {GrantId}, subject {SubjectId}",
            grant.Id, grant.SubjectId);

        return AuthResult.Success(new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.DirectGrant,
            SubjectId = grant.SubjectId,
            Scopes = [.. grant.Scopes],
            TokenId = grant.Id,
            // MemberScopeMiddleware applies the membership's limit on top of this one.
            LimitTo24Hours = grant.LimitTo24Hours,
        });
    }

    /// <summary>
    /// Finds the active direct grant <paramref name="token"/> identifies on
    /// <paramref name="tenantId"/>, with its subject's membership of that tenant read in the same
    /// statement, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Reads pinned to <paramref name="tenantId"/>, for the reason on
    /// <see cref="IOAuthGrantRepository.FindLiveDirectGrantAsync(Guid, DirectGrantLookup, DateTime, CancellationToken)"/>.
    /// </remarks>
    /// <param name="grants">The stored grants.</param>
    /// <param name="token">The presented token, <c>noc_</c>-prefixed.</param>
    /// <param name="tenantId">The tenant the grant must belong to.</param>
    /// <param name="now">The instant the grant must be active at.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="legacyToken">
    /// The credential exactly as presented, before the <c>noc_</c> prefix was normalized in, or null
    /// when there is nothing to match the legacy rule against. Kept separate from
    /// <paramref name="token"/> because a dashless Nightscout token, which is a bare digest, stops
    /// parsing as one once a prefix is prepended.
    /// </param>
    internal static async Task<AuthenticatedDirectGrant?> FindActiveGrantAsync(
        IOAuthGrantRepository grants,
        string token,
        Guid tenantId,
        DateTime now,
        CancellationToken ct = default,
        string? legacyToken = null)
    {
        // Both spellings of the credential hash to a candidate: the query path normalizes the noc_
        // prefix in, so a token stored under the hash of its bare form would otherwise authenticate
        // over Authorization: Bearer and not over ?token=. Which transport carried a credential must
        // not decide whether it works.
        var candidateHashes = new List<string>(2) { HashUtils.Sha256Hex(token) };
        if (legacyToken != null && !string.Equals(legacyToken, token, StringComparison.Ordinal))
        {
            candidateHashes.Add(HashUtils.Sha256Hex(legacyToken));
        }

        // Nightscout's own rule, reproduced: the part after the last dash authenticates when it is a
        // prefix of the stored digest, so the name-abbrev is cosmetic and any prefix of 16-40 chars
        // works. ExtractDigestPrefix validates it as hex, which is also what makes it safe to use as
        // a LIKE pattern below.
        var digestPrefix = LegacyNightscoutToken.ExtractDigestPrefix(legacyToken ?? token);

        // Two lookups rather than one OR'd predicate. The hash match is what almost every request
        // takes; the digest match is a filtered-index range scan only an imported Nightscout token
        // can satisfy, and OR-ing the two would deny the planner the digest index on every request.
        var byHash = await grants.FindLiveDirectGrantAsync(
            tenantId, new DirectGrantLookup.ByTokenHash(candidateHashes), now, ct);
        if (byHash != null || digestPrefix == null)
        {
            return byHash;
        }

        return await grants.FindLiveDirectGrantAsync(
            tenantId, new DirectGrantLookup.ByLegacyTokenDigestPrefix(digestPrefix), now, ct);
    }

    /// <summary>
    /// Extracts a direct grant token from the request. Accepts the <c>Authorization: Bearer</c>
    /// header (any opaque value) or the Nightscout-style <c>?token=</c> query parameter — how
    /// xDrip4iOS and other Nightscout uploaders send their credential.
    /// </summary>
    /// <remarks>
    /// On the query-parameter path the <c>noc_</c> prefix is normalized in: uploaders routinely
    /// drop the human-facing marker and send only the secret suffix, so both <c>noc_&lt;secret&gt;</c>
    /// and a bare <c>&lt;secret&gt;</c> resolve to the same grant. The raw value is returned alongside
    /// it because a classic Nightscout <c>name-hash</c> token is matched by digest prefix rather
    /// than by hash, and a bare digest stops parsing as one once a prefix is prepended.
    /// A value that isn't one of our credentials matches no grant and falls through (Skip).
    /// </remarks>
    /// <returns>
    /// The value to hash, and the value as presented. Both are null when no credential was sent.
    /// </returns>
    private static (string? Normalized, string? Raw) ExtractToken(HttpContext context)
    {
        var bearer = context.Request.GetAuthorizationCredential();
        if (!string.IsNullOrEmpty(bearer))
        {
            return (bearer, bearer);
        }

        var queryToken = context.Request.Query["token"].FirstOrDefault();
        if (!string.IsNullOrEmpty(queryToken))
        {
            return (
                queryToken.StartsWith(TokenPrefix, StringComparison.Ordinal)
                    ? queryToken
                    : TokenPrefix + queryToken,
                queryToken);
        }

        return (null, null);
    }

    /// <summary>
    /// Stamps when, from where and by what a grant was last presented.
    /// </summary>
    /// <remarks>
    /// Pinned to <paramref name="tenantId"/>, for the reason given on
    /// <see cref="FindActiveGrantAsync"/>. Every caller starts it fire-and-forget from a request that
    /// has already authenticated, so a failure is logged and swallowed rather than surfaced.
    /// </remarks>
    /// <param name="grants">The stored grants.</param>
    /// <param name="logger">Where a failure is reported.</param>
    /// <param name="grantId">The grant that was presented.</param>
    /// <param name="tenantId">The tenant the grant belongs to.</param>
    /// <param name="ipAddress">The caller's address, when the connection has one.</param>
    /// <param name="userAgent">The caller's user-agent, when it sent one.</param>
    internal static async Task RecordLastUsedAsync(
        IOAuthGrantRepository grants,
        ILogger logger,
        Guid grantId,
        Guid tenantId,
        string? ipAddress,
        string? userAgent)
    {
        try
        {
            await grants.RecordUseAsync(
                tenantId, grantId, DateTime.UtcNow, new GrantPresentation(ipAddress, userAgent), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update last used metadata for grant {GrantId}", grantId);
        }
    }
}
