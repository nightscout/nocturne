using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Nocturne.API.Helpers;
using Nocturne.API.Multitenancy;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Configuration;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Services.Auth;

/// <summary>
/// Handles OpenID Connect authentication flows including login, session refresh,
/// logout, and account linking.
/// </summary>
/// <seealso cref="IOidcAuthService"/>
/// <seealso cref="IOidcProviderService"/>
/// <seealso cref="ISessionService"/>
/// <seealso cref="ISubjectService"/>
public class OidcAuthService : IOidcAuthService
{
    private readonly IOidcProviderService _providerService;
    private readonly ISubjectService _subjectService;
    private readonly ISessionService _sessionService;
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantMemberService _tenantMemberService;
    private readonly IMemberInviteService _memberInviteService;
    private readonly IDataProtector _stateProtector;
    private readonly OidcOptions _options;
    private readonly BaseDomainOptions _baseDomain;
    private readonly ILogger<OidcAuthService> _logger;

    /// <summary>
    /// Initialises a new <see cref="OidcAuthService"/>.
    /// </summary>
    /// <param name="providerService">Service for resolving and caching OIDC provider configurations.</param>
    /// <param name="subjectService">Service for finding or creating subjects from OIDC identities.</param>
    /// <param name="sessionService">Service for issuing and rotating first-party sessions.</param>
    /// <param name="jwtService">Service for generating Nocturne access tokens (non-rotation refresh path only).</param>
    /// <param name="refreshTokenService">Service for validating refresh tokens (non-rotation refresh path only).</param>
    /// <param name="httpClientFactory">Factory for the <c>OidcProvider</c> named HTTP client.</param>
    /// <param name="tenantMemberService">Service for verifying tenant membership before issuing a login session.</param>
    /// <param name="memberInviteService">Service for accepting an invite named by the login's return URL.</param>
    /// <param name="dataProtectionProvider">Provider for the protector that authenticates the state parameter.</param>
    /// <param name="options">OIDC session and state configuration options.</param>
    /// <param name="baseDomainOptions">Base domain configuration for building the public redirect URIs.</param>
    /// <param name="logger">Logger instance.</param>
    public OidcAuthService(
        IOidcProviderService providerService,
        ISubjectService subjectService,
        ISessionService sessionService,
        IJwtService jwtService,
        IRefreshTokenService refreshTokenService,
        IHttpClientFactory httpClientFactory,
        ITenantMemberService tenantMemberService,
        IMemberInviteService memberInviteService,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<OidcOptions> options,
        IOptions<BaseDomainOptions> baseDomainOptions,
        ILogger<OidcAuthService> logger
    )
    {
        _providerService = providerService;
        _subjectService = subjectService;
        _sessionService = sessionService;
        _jwtService = jwtService;
        _refreshTokenService = refreshTokenService;
        _httpClientFactory = httpClientFactory;
        _tenantMemberService = tenantMemberService;
        _memberInviteService = memberInviteService;
        _stateProtector = dataProtectionProvider.CreateProtector("Nocturne.Oidc.State");
        _options = options.Value;
        _baseDomain = baseDomainOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OidcAuthorizationRequest> GenerateAuthorizationUrlAsync(
        Guid? providerId,
        string? returnUrl = null,
        string? tenantSlug = null
    )
    {
        OidcProvider provider;

        if (providerId.HasValue)
        {
            provider =
                await _providerService.GetProviderByIdAsync(providerId.Value)
                ?? throw new InvalidOperationException($"OIDC provider {providerId} not found");
        }
        else
        {
            var providers = await _providerService.GetEnabledProvidersAsync();
            provider =
                providers.FirstOrDefault()
                ?? throw new InvalidOperationException("No OIDC providers configured");
        }

        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException($"OIDC provider {provider.Name} is not enabled");
        }

        // Get discovery document for authorization endpoint
        var discoveryDoc =
            await _providerService.GetDiscoveryDocumentAsync(provider.Id)
            ?? throw new InvalidOperationException(
                $"Could not fetch OIDC discovery document for {provider.Name}"
            );

        // Generate state parameter (includes return URL, provider ID, and nonce)
        var stateData = new OidcStateData
        {
            ProviderId = provider.Id,
            ReturnUrl = returnUrl ?? "/",
            Nonce = GenerateRandomString(32),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.State.Lifetime),
            Intent = "login",
            TenantSlug = tenantSlug,
        };

        return await BuildAuthorizationUrlAsync(provider, stateData, returnUrl);
    }

    private async Task<OidcAuthorizationRequest> BuildAuthorizationUrlAsync(
        OidcProvider provider,
        OidcStateData stateData,
        string? returnUrl,
        string callbackPath = LoginCallbackPath
    )
    {
        var discoveryDoc =
            await _providerService.GetDiscoveryDocumentAsync(provider.Id)
            ?? throw new InvalidOperationException(
                $"Could not fetch OIDC discovery document for {provider.Name}"
            );

        var state = EncodeState(stateData);

        var redirectUri = GetRedirectUri(callbackPath);
        var authUrl = BuildAuthorizationUrl(
            discoveryDoc.AuthorizationEndpoint,
            provider.ClientId,
            redirectUri,
            provider.Scopes,
            state,
            stateData.Nonce
        );

        return new OidcAuthorizationRequest
        {
            AuthorizationUrl = authUrl,
            State = state,
            Nonce = stateData.Nonce,
            ProviderId = provider.Id,
            ReturnUrl = stateData.ReturnUrl,
            ExpiresAt = stateData.ExpiresAt,
        };
    }

    private record CallbackParseResult(
        bool Success,
        string? Error,
        string? ErrorDescription,
        OidcStateData? StateData,
        OidcProvider? Provider,
        OidcIdTokenClaims? Claims
    )
    {
        public static CallbackParseResult Fail(string error, string? desc = null) =>
            new(false, error, desc, null, null, null);
        public static CallbackParseResult Ok(OidcStateData s, OidcProvider p, OidcIdTokenClaims c) =>
            new(true, null, null, s, p, c);
    }

    private async Task<CallbackParseResult> ValidateCallbackAndParseIdTokenAsync(
        string code, string state, string expectedState, string callbackPath = LoginCallbackPath)
    {
        if (string.IsNullOrEmpty(state) || state != expectedState)
        {
            return CallbackParseResult.Fail(
                "invalid_state",
                "State parameter mismatch - possible CSRF attack");
        }

        OidcStateData stateData;
        try
        {
            stateData = DecodeState(state);
        }
        catch (Exception ex)
        {
            // Also the forgery path: an unprotectable state was not issued by this instance.
            _logger.LogWarning(ex, "Rejected an OIDC state that failed to decode");
            return CallbackParseResult.Fail("invalid_state", "Invalid state format");
        }

        if (stateData.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return CallbackParseResult.Fail("expired_state", "Authentication request has expired");
        }

        var provider = await _providerService.GetProviderByIdAsync(stateData.ProviderId);
        if (provider == null || !provider.IsEnabled)
        {
            return CallbackParseResult.Fail("invalid_provider", "OIDC provider not found or disabled");
        }

        var discoveryDoc = await _providerService.GetDiscoveryDocumentAsync(provider.Id);
        if (discoveryDoc == null)
        {
            return CallbackParseResult.Fail("provider_error", "Could not fetch provider configuration");
        }

        OidcProviderTokenResponse providerTokens;
        try
        {
            providerTokens = await ExchangeCodeForTokensAsync(
                discoveryDoc.TokenEndpoint,
                code,
                provider.ClientId,
                provider.ClientSecret,
                GetRedirectUri(callbackPath)
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Token exchange failed");
            return CallbackParseResult.Fail("token_exchange_failed", ex.Message);
        }

        OidcIdTokenClaims idTokenClaims;
        try
        {
            if (provider.ProviderType == OidcProviderType.OAuth2)
            {
                // OAuth2 providers issue no ID token (and no nonce); identity comes from the userinfo endpoint.
                idTokenClaims = await FetchOAuth2UserClaimsAsync(provider, discoveryDoc, providerTokens.AccessToken);
            }
            else
            {
                idTokenClaims = ParseIdToken(providerTokens.IdToken);

                // Required, not conditional: every state this service issues carries a nonce, so
                // an absent one means the ID token cannot be bound to this authorization request.
                if (string.IsNullOrEmpty(stateData.Nonce) || idTokenClaims.Nonce != stateData.Nonce)
                {
                    return CallbackParseResult.Fail("invalid_nonce", "ID token nonce mismatch");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve identity from provider response");
            return CallbackParseResult.Fail("invalid_id_token", ex.Message);
        }

        return CallbackParseResult.Ok(stateData, provider, idTokenClaims);
    }

    /// <inheritdoc />
    public async Task<OidcCallbackResult> HandleCallbackAsync(
        string code,
        string state,
        string expectedState,
        string? ipAddress = null,
        string? userAgent = null,
        Guid? currentTenantId = null
    )
    {
        var parsed = await ValidateCallbackAndParseIdTokenAsync(code, state, expectedState);
        if (!parsed.Success)
        {
            return OidcCallbackResult.Failed(parsed.Error ?? "callback_failed", parsed.ErrorDescription);
        }

        return await CompleteLoginAsync(
            parsed.StateData!, parsed.Provider!, parsed.Claims!, currentTenantId, ipAddress, userAgent);
    }

    /// <summary>
    /// Completes a login after the OIDC callback has been validated and the ID token parsed:
    /// resolves the subject from the external identity, enforces tenant membership, and issues
    /// a session. Because an OIDC identity resolves to a <em>global</em> subject, a valid external
    /// identity must still be a member of <paramref name="currentTenantId"/> before a session is
    /// issued — otherwise any external identity could mint a session on any tenant's subdomain.
    /// Extracted from <see cref="HandleCallbackAsync"/> so the membership gate can be unit-tested
    /// without exercising the OIDC code exchange.
    /// <para>
    /// No TOTP second-factor gate here. <c>PasskeyController.LoginComplete</c> withholds the session
    /// when the subject has an authenticator enrolled; this path issues one regardless, so a subject
    /// with TOTP and a linked provider signs in through the provider without a code. Adding the gate
    /// needs a pending-second-factor state that survives the provider redirect.
    /// </para>
    /// </summary>
    internal async Task<OidcCallbackResult> CompleteLoginAsync(
        OidcStateData stateData,
        OidcProvider provider,
        OidcIdTokenClaims idTokenClaims,
        Guid? currentTenantId,
        string? ipAddress,
        string? userAgent)
    {
        // Find or create subject
        var subject = await _subjectService.FindOrCreateFromOidcAsync(
            provider.Id,
            idTokenClaims.Sub,
            provider.IssuerUrl,
            idTokenClaims.Email,
            idTokenClaims.Name ?? idTokenClaims.PreferredUsername,
            provider.DefaultRoles
        );

        // Cross-tenant guard: deny — without issuing a session — when the authenticated
        // identity is not a member of the tenant being logged into, and holds no invite to it.
        // The per-request membership gate in AuthenticationMiddleware would block this subject's
        // data access anyway, but only after a session (and a "logged in" UI) already existed.
        //
        // Two conditions, because either one alone leaves a gap.
        //
        // A resolved tenant always requires membership. A callback delivered to a tenant
        // subdomain resolves that tenant wherever the login started, so an apex-minted state
        // (no slug) replayed at {tenant}.{basedomain} arrives here with a tenant resolved;
        // keying only off the slug would skip the check for exactly that case.
        //
        // A state that names a tenant also requires one to have resolved. Such a state was
        // minted by a tenant login and must have been bounced to that subdomain, so an
        // unresolved tenant means something upstream failed — and treating that as "nothing to
        // check" is how the gate silently disappeared when the state encoding changed.
        if (currentTenantId is { } tenantId)
        {
            if (!await _tenantMemberService.IsMemberAsync(subject.Id, tenantId)
                && !await IsInvitedByReturnUrlAsync(stateData.ReturnUrl, tenantId))
            {
                _logger.LogWarning(
                    "OIDC login denied: subject {SubjectId} is not a member of tenant {TenantId}",
                    subject.Id,
                    tenantId);
                return OidcCallbackResult.NotAMember(subject.Id, stateData.ReturnUrl);
            }
        }
        else if (!string.IsNullOrEmpty(stateData.TenantSlug))
        {
            _logger.LogError(
                "OIDC login denied: state was minted for tenant '{TenantSlug}' but no tenant "
                    + "resolved on the callback, so membership could not be verified",
                stateData.TenantSlug);
            return OidcCallbackResult.Failed(
                "invalid_state",
                "The login could not be completed against the tenant it was started from.");
        }

        // Update last login
        await _subjectService.UpdateLastLoginAsync(subject.Id);

        // Issue session via SessionService
        var session = await _sessionService.IssueSessionAsync(
            subject.Id,
            new SessionContext(
                OidcSessionId: idTokenClaims.SessionId,
                DeviceDescription: ParseUserAgentShort(userAgent),
                IpAddress: ipAddress,
                UserAgent: userAgent));

        var tokens = new OidcTokenResponse
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            TokenType = "Bearer",
            ExpiresIn = session.ExpiresInSeconds,
            RefreshExpiresIn = (int)_options.Session.RefreshTokenLifetime.TotalSeconds,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds),
            SubjectId = subject.Id,
        };

        var permissions = await _subjectService.GetSubjectPermissionsAsync(subject.Id);
        var roles = await _subjectService.GetSubjectRolesAsync(subject.Id);

        var userInfo = new OidcUserInfo
        {
            SubjectId = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
            EmailVerified = idTokenClaims.EmailVerified,
            Picture = idTokenClaims.Picture,
            Roles = roles,
            Permissions = permissions,
            ProviderName = provider.Name,
            LastLoginAt = DateTimeOffset.UtcNow,
        };

        _logger.LogInformation(
            "OIDC authentication successful for user {Name} ({Email}) via {Provider}",
            subject.Name,
            subject.Email ?? "no email",
            provider.Name
        );

        return OidcCallbackResult.Succeeded(tokens, userInfo, stateData.ReturnUrl);
    }

    /// <summary>
    /// Whether the login was started from a join link whose invite is still valid for
    /// <paramref name="tenantId"/>, and so should be allowed to complete for a non-member.
    /// </summary>
    /// <param name="returnUrl">The return URL carried in the (data-protected) state.</param>
    /// <param name="tenantId">The tenant the callback resolved to.</param>
    /// <remarks>
    /// A first-time OIDC identity is a member of nothing, so the membership guard alone sends the
    /// invitee back to a login page that cannot help them. This only issues the session; the join
    /// itself stays behind the Accept button on the join page, which the invite-token exemption
    /// makes reachable for a signed-in non-member.
    /// <para>
    /// Joining here instead would be a forced join: the login endpoint is anonymous and takes the
    /// return URL from the query string, so anyone could navigate a victim to a join link of their
    /// choosing and have the silent IdP round-trip write the membership without a gesture.
    /// </para>
    /// </remarks>
    private async Task<bool> IsInvitedByReturnUrlAsync(string? returnUrl, Guid tenantId)
    {
        var token = InviteTokenFromReturnUrl(returnUrl);
        if (token == null)
            return false;

        var invite = await _memberInviteService.GetInviteByTokenAsync(token, tenantId);
        return invite is { IsValid: true };
    }

    /// <summary>
    /// Reads the invite token out of a join link (<c>/join?token=...</c>), the only return URL
    /// that carries one. Returns null for any other return URL.
    /// </summary>
    /// <param name="returnUrl">The return URL carried in the state.</param>
    /// <returns>The invite token, or null.</returns>
    private static string? InviteTokenFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl))
            return null;

        var queryStart = returnUrl.IndexOf('?');
        if (queryStart < 0)
            return null;

        if (!returnUrl.AsSpan(0, queryStart)
                .Equals(IMemberInviteService.JoinPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(returnUrl[queryStart..])
            .TryGetValue(IMemberInviteService.TokenQueryParameter, out var values)
                ? values.ToString()
                : null;

        return string.IsNullOrEmpty(token) ? null : token;
    }

    /// <inheritdoc />
    public async Task<OidcTokenResponse?> RefreshSessionAsync(
        string refreshToken,
        string? ipAddress = null,
        string? userAgent = null
    )
    {
        // Rotation path: delegate entirely to SessionService
        if (_options.Session.RotateRefreshTokens)
        {
            var session = await _sessionService.RotateSessionAsync(
                refreshToken,
                new SessionContext(IpAddress: ipAddress, UserAgent: userAgent));

            if (session is null)
                return null;

            // Resolve subject ID from the new refresh token for the response
            var rotatedSubjectId = await _refreshTokenService.ValidateRefreshTokenAsync(session.RefreshToken);
            if (!rotatedSubjectId.HasValue)
                return null;

            return new OidcTokenResponse
            {
                AccessToken = session.AccessToken,
                RefreshToken = session.RefreshToken,
                TokenType = "Bearer",
                ExpiresIn = session.ExpiresInSeconds,
                RefreshExpiresIn = (int)_options.Session.RefreshTokenLifetime.TotalSeconds,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds),
                SubjectId = rotatedSubjectId.Value,
            };
        }

        // Non-rotation path: validate and re-mint access token without creating a new refresh token
        var subjectId = await _refreshTokenService.ValidateRefreshTokenAsync(refreshToken);
        if (!subjectId.HasValue)
            return null;

        await _refreshTokenService.UpdateLastUsedAsync(refreshToken);

        var subject = await _subjectService.GetSubjectByIdAsync(subjectId.Value);
        if (subject == null || !subject.IsActive)
        {
            await _refreshTokenService.RevokeRefreshTokenAsync(refreshToken, "Subject inactive");
            return null;
        }

        var permissions = await _subjectService.GetSubjectPermissionsAsync(subjectId.Value);
        var roles = await _subjectService.GetSubjectRolesAsync(subjectId.Value);

        var accessTokenLifetime = _options.Session.AccessTokenLifetime;
        var accessToken = _jwtService.GenerateAccessToken(
            new SubjectInfo
            {
                Id = subject.Id,
                Name = subject.Name,
                Email = subject.Email,
            },
            permissions,
            roles,
            accessTokenLifetime
        );

        return new OidcTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenType = "Bearer",
            ExpiresIn = (int)accessTokenLifetime.TotalSeconds,
            RefreshExpiresIn = (int)_options.Session.RefreshTokenLifetime.TotalSeconds,
            ExpiresAt = DateTimeOffset.UtcNow.Add(accessTokenLifetime),
            SubjectId = subjectId.Value,
        };
    }

    /// <inheritdoc />
    public async Task<OidcLogoutResult> LogoutAsync(string refreshToken, Guid? providerId = null)
    {
        // Revoke the refresh token
        var revoked = await _refreshTokenService.RevokeRefreshTokenAsync(
            refreshToken,
            "User logout"
        );

        if (!revoked)
        {
            // Token might already be revoked, which is fine
            _logger.LogDebug("Refresh token not found or already revoked during logout");
        }

        // Get provider logout URL if requested
        string? providerLogoutUrl = null;
        if (providerId.HasValue)
        {
            var provider = await _providerService.GetProviderByIdAsync(providerId.Value);
            if (provider != null)
            {
                var discoveryDoc = await _providerService.GetDiscoveryDocumentAsync(
                    providerId.Value
                );
                if (!string.IsNullOrEmpty(discoveryDoc?.EndSessionEndpoint))
                {
                    // Build RP-initiated logout URL
                    var logoutUrl = new UriBuilder(discoveryDoc.EndSessionEndpoint);
                    var query = System.Web.HttpUtility.ParseQueryString(string.Empty);
                    query["client_id"] = provider.ClientId;
                    query["post_logout_redirect_uri"] = _baseDomain.PublicOrigin ?? "";
                    logoutUrl.Query = query.ToString();
                    providerLogoutUrl = logoutUrl.ToString();
                }
            }
        }

        return OidcLogoutResult.Succeeded(providerLogoutUrl);
    }

    /// <inheritdoc />
    public async Task<OidcUserInfo?> GetUserInfoAsync(Guid subjectId)
    {
        var subject = await _subjectService.GetSubjectByIdAsync(subjectId);
        if (subject == null)
        {
            return null;
        }

        var permissions = await _subjectService.GetSubjectPermissionsAsync(subjectId);
        var roles = await _subjectService.GetSubjectRolesAsync(subjectId);

        // Get provider name from the most recently used linked OIDC identity.
        // We don't currently persist the "current session provider" on the refresh-token row,
        // so "most recently used" is the best available proxy for "the provider the user just
        // signed in with". Falls back to most recently linked if LastUsedAt is null.
        var mostRecent = await _subjectService.GetMostRecentlyUsedIdentityAsync(subjectId);
        string? providerName = mostRecent?.ProviderName;

        return new OidcUserInfo
        {
            SubjectId = subject.Id,
            Name = subject.Name,
            Email = subject.Email,
            Roles = roles,
            Permissions = permissions,
            ProviderName = providerName,
            LastLoginAt = subject.LastLoginAt,
            PreferredLanguage = subject.PreferredLanguage,
            Preferences = UserDisplayPreferences.Deserialize(subject.Preferences),
            AvatarUrl = subject.AvatarUrl,
        };
    }

    /// <inheritdoc />
    public async Task<Guid?> ValidateSessionAsync(string refreshToken)
    {
        return await _refreshTokenService.ValidateRefreshTokenAsync(refreshToken);
    }

    /// <inheritdoc />
    public async Task<OidcAuthorizationRequest> GenerateLinkAuthorizationUrlAsync(
        Guid providerId, Guid subjectId, string? returnUrl = null, string? tenantSlug = null)
    {
        var provider =
            await _providerService.GetProviderByIdAsync(providerId)
            ?? throw new InvalidOperationException($"OIDC provider {providerId} not found");

        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException($"OIDC provider {provider.Name} is not enabled");
        }

        var stateData = new OidcStateData
        {
            ProviderId = provider.Id,
            ReturnUrl = returnUrl ?? "/",
            Nonce = GenerateRandomString(32),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.State.Lifetime),
            Intent = "link",
            SubjectId = subjectId,
            TenantSlug = tenantSlug,
        };

        return await BuildAuthorizationUrlAsync(provider, stateData, returnUrl, callbackPath: LinkCallbackPath);
    }

    /// <inheritdoc />
    public async Task<OidcLinkResult> HandleLinkCallbackAsync(
        string code, string state, string expectedState,
        Guid authenticatedSubjectId,
        string? ipAddress = null, string? userAgent = null)
    {
        var parsed = await ValidateCallbackAndParseIdTokenAsync(code, state, expectedState, LinkCallbackPath);
        if (!parsed.Success)
        {
            return OidcLinkResult.Failed(parsed.Error ?? "callback_failed", parsed.ErrorDescription);
        }

        var stateData = parsed.StateData!;
        var provider = parsed.Provider!;
        var claims = parsed.Claims!;

        return await AttachVerifiedIdentityAsync(stateData, provider, claims, authenticatedSubjectId);
    }

    /// <inheritdoc />
    public async Task<OidcAuthorizationRequest> GenerateSetupAuthorizationUrlAsync(
        Guid providerId, Guid subjectId, string? tenantSlug = null)
    {
        var provider =
            await _providerService.GetProviderByIdAsync(providerId)
            ?? throw new InvalidOperationException($"OIDC provider {providerId} not found");

        if (!provider.IsEnabled)
            throw new InvalidOperationException($"OIDC provider {provider.Name} is not enabled");

        var stateData = new OidcStateData
        {
            ProviderId = provider.Id,
            ReturnUrl = "/setup",
            Nonce = GenerateRandomString(32),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.Add(_options.State.Lifetime),
            Intent = "setup",
            SubjectId = subjectId,
            TenantSlug = tenantSlug,
        };

        return await BuildAuthorizationUrlAsync(provider, stateData, "/setup", callbackPath: SetupCallbackPath);
    }

    /// <inheritdoc />
    public async Task<OidcSetupCallbackResult> HandleSetupCallbackAsync(
        string code, string state, string expectedState,
        string? ipAddress = null, string? userAgent = null)
    {
        var parsed = await ValidateCallbackAndParseIdTokenAsync(code, state, expectedState, SetupCallbackPath);
        if (!parsed.Success)
            return OidcSetupCallbackResult.Failed(parsed.Error ?? "callback_failed", parsed.ErrorDescription);

        var stateData = parsed.StateData!;
        var provider = parsed.Provider!;
        var claims = parsed.Claims!;

        if (stateData.Intent != "setup")
            return OidcSetupCallbackResult.Failed("invalid_intent", "State was not issued for a setup flow");

        if (!stateData.SubjectId.HasValue)
            return OidcSetupCallbackResult.Failed("invalid_state", "No subject ID in setup state");

        var subjectId = stateData.SubjectId.Value;

        // Link OIDC identity to the pre-created admin subject
        var (outcome, _) = await _subjectService.AttachOidcIdentityAsync(
            subjectId,
            provider.Id,
            claims.Sub,
            provider.IssuerUrl,
            claims.Email);

        if (outcome == OidcLinkOutcome.AlreadyLinkedToOther)
            return OidcSetupCallbackResult.Failed(
                "identity_already_linked",
                "This provider account is already linked to another Nocturne user");

        // Update subject email/name from OIDC claims if not already set
        var subject = await _subjectService.GetSubjectByIdAsync(subjectId);
        if (subject == null)
            return OidcSetupCallbackResult.Failed("subject_not_found", "Pre-created setup subject not found");

        await _subjectService.UpdateLastLoginAsync(subjectId);

        // Issue session via SessionService
        var session = await _sessionService.IssueSessionAsync(
            subjectId,
            new SessionContext(
                OidcSessionId: claims.SessionId,
                DeviceDescription: "Setup OIDC",
                IpAddress: ipAddress,
                UserAgent: userAgent));

        var tokens = new OidcTokenResponse
        {
            AccessToken = session.AccessToken,
            RefreshToken = session.RefreshToken,
            TokenType = "Bearer",
            ExpiresIn = session.ExpiresInSeconds,
            RefreshExpiresIn = (int)_options.Session.RefreshTokenLifetime.TotalSeconds,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(session.ExpiresInSeconds),
            SubjectId = subjectId,
        };

        _logger.LogInformation(
            "Setup OIDC: linked identity for subject {SubjectId} via provider {Provider}",
            subjectId, provider.Name);

        return OidcSetupCallbackResult.Succeeded(subjectId, tokens, stateData.ReturnUrl);
    }

    /// <summary>
    /// Attaches a verified OIDC identity to an already-authenticated subject.
    /// Extracted from <see cref="HandleLinkCallbackAsync"/> to enable unit testing
    /// without mocking token exchange and JWKS verification.
    /// </summary>
    /// <param name="stateData">Decoded state data from the link callback, which must have <c>Intent == "link"</c>.</param>
    /// <param name="provider">The OIDC provider from which the identity originated.</param>
    /// <param name="claims">Parsed claims from the provider's ID token.</param>
    /// <param name="authenticatedSubjectId">The currently authenticated subject to link the identity to.</param>
    /// <returns>An <see cref="OidcLinkResult"/> describing the outcome of the link operation.</returns>
    internal async Task<OidcLinkResult> AttachVerifiedIdentityAsync(
        OidcStateData stateData,
        OidcProvider provider,
        OidcIdTokenClaims claims,
        Guid authenticatedSubjectId)
    {
        if (stateData.Intent != "link")
        {
            return OidcLinkResult.Failed("invalid_intent", "State was not issued for a link flow");
        }
        if (stateData.SubjectId != authenticatedSubjectId)
        {
            return OidcLinkResult.Failed("invalid_state", "State subject mismatch");
        }

        var (outcome, identityId) = await _subjectService.AttachOidcIdentityAsync(
            authenticatedSubjectId,
            provider.Id,
            claims.Sub,
            provider.IssuerUrl,
            claims.Email);

        return outcome switch
        {
            OidcLinkOutcome.Created or OidcLinkOutcome.AlreadyLinkedToSelf
                => OidcLinkResult.Succeeded(identityId!.Value, stateData.ReturnUrl),
            OidcLinkOutcome.AlreadyLinkedToOther
                => OidcLinkResult.Failed(
                    "identity_already_linked",
                    "This provider account is already linked to another Nocturne user"),
            _ => OidcLinkResult.Failed("unknown_error", "Unexpected link outcome"),
        };
    }

    #region Private Helper Methods

    private const string LoginCallbackPath = "/api/auth/oidc/callback";
    private const string LinkCallbackPath = "/api/auth/oidc/link/callback";
    private const string SetupCallbackPath = "/api/v4/setup/oidc/callback";

    /// <summary>
    /// Builds the absolute redirect URI by combining the deployment's public origin
    /// (derived from <c>BASE_DOMAIN</c>) with the specified callback path.
    /// </summary>
    /// <param name="callbackPath">The server-relative callback path (default: <see cref="LoginCallbackPath"/>).</param>
    /// <returns>The fully qualified redirect URI.</returns>
    private string GetRedirectUri(string callbackPath = LoginCallbackPath)
    {
        var origin =
            _baseDomain.PublicOrigin
            ?? throw new InvalidOperationException(
                $"Cannot build an OIDC redirect URI: {BaseDomainOptions.ConfigKey} is not configured"
            );
        return $"{origin}{callbackPath}";
    }

    /// <summary>
    /// Constructs the provider's authorization URL with all required OIDC query parameters.
    /// </summary>
    /// <param name="authorizationEndpoint">The provider's authorization endpoint URL.</param>
    /// <param name="clientId">The registered OAuth client identifier.</param>
    /// <param name="redirectUri">The registered redirect URI for the callback.</param>
    /// <param name="scopes">The requested OAuth scopes.</param>
    /// <param name="state">URL-safe state token for CSRF protection.</param>
    /// <param name="nonce">Replay-prevention nonce embedded in the ID token.</param>
    /// <returns>The fully assembled authorization URL string.</returns>
    private static string BuildAuthorizationUrl(
        string authorizationEndpoint,
        string clientId,
        string redirectUri,
        IEnumerable<string> scopes,
        string state,
        string? nonce
    )
    {
        var url = new UriBuilder(authorizationEndpoint);
        var query = System.Web.HttpUtility.ParseQueryString(string.Empty);

        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = redirectUri;
        query["scope"] = string.Join(" ", scopes);
        query["state"] = state;

        if (!string.IsNullOrEmpty(nonce))
        {
            query["nonce"] = nonce;
        }

        url.Query = query.ToString();
        return url.ToString();
    }

    /// <summary>
    /// Exchanges an authorisation code for provider tokens at the token endpoint.
    /// Uses HTTP Basic authentication when a <paramref name="clientSecret"/> is provided (confidential client).
    /// </summary>
    /// <param name="tokenEndpoint">The provider's token endpoint URL.</param>
    /// <param name="code">The authorisation code from the callback.</param>
    /// <param name="clientId">The registered OAuth client identifier.</param>
    /// <param name="clientSecret">Optional client secret for confidential clients.</param>
    /// <param name="redirectUri">The redirect URI that was used in the authorisation request.</param>
    /// <returns>The <see cref="OidcProviderTokenResponse"/> containing ID and access tokens.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the provider returns a non-success status code.</exception>
    private async Task<OidcProviderTokenResponse> ExchangeCodeForTokensAsync(
        string tokenEndpoint,
        string code,
        string clientId,
        string? clientSecret,
        string redirectUri
    )
    {
        var httpClient = _httpClientFactory.CreateClient("OidcProvider");

        // GitHub's token endpoint returns form-encoded by default; request JSON so the same
        // deserializer handles every provider. Standards-compliant OIDC providers already return JSON.
        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        var content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
            }
        );

        // Add client secret if provided (confidential client)
        if (!string.IsNullOrEmpty(clientSecret))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")
            );
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                credentials
            );
        }

        var response = await httpClient.PostAsync(tokenEndpoint, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Token exchange failed: {StatusCode} - {Response}",
                response.StatusCode,
                responseBody
            );
            throw new InvalidOperationException($"Token exchange failed: {response.StatusCode}");
        }

        var tokens = JsonSerializer.Deserialize<OidcProviderTokenResponse>(
            responseBody,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return tokens ?? throw new InvalidOperationException("Empty token response");
    }

    /// <summary>
    /// Parses the payload claims from a JWT ID token without validating the signature.
    /// </summary>
    /// <remarks>
    /// Full signature validation is performed by the OIDC token handler when the token is
    /// subsequently used. This method is intentionally minimal — it only decodes the Base64url
    /// payload and deserialises the JSON claims.
    /// </remarks>
    /// <param name="idToken">The raw JWT ID token string.</param>
    /// <returns>Deserialised <see cref="OidcIdTokenClaims"/> from the token payload.</returns>
    /// <exception cref="InvalidOperationException">Thrown for malformed token format or empty claims.</exception>
    private static OidcIdTokenClaims ParseIdToken(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Invalid ID token format");
        }

        var payload = parts[1];

        var json = Encoding.UTF8.GetString(Base64Url.Decode(payload));

        var claims = JsonSerializer.Deserialize<OidcIdTokenClaims>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return claims ?? throw new InvalidOperationException("Invalid ID token claims");
    }

    private const string SubClaim = "sub";
    private const string EmailClaim = "email";
    private const string NameClaim = "name";
    private const string PreferredUsernameClaim = "preferred_username";
    private const string PictureClaim = "picture";

    /// <summary>
    /// Resolves identity claims for an OAuth2 provider from its configured userinfo endpoint, standing
    /// in for the ID token that plain OAuth2 does not issue. The userinfo response fields are mapped to
    /// standard claims via <see cref="OAuth2ProviderSettings.ClaimMappings"/>. When the response carries
    /// no email and an emails endpoint is configured, the primary verified address is fetched from it.
    /// </summary>
    /// <param name="provider">The OAuth2 provider whose settings drive endpoint and claim resolution.</param>
    /// <param name="discoveryDoc">The synthesized discovery document (carries the userinfo endpoint).</param>
    /// <param name="accessToken">The OAuth2 access token returned by the token endpoint.</param>
    /// <returns>Claims mapped into the same <see cref="OidcIdTokenClaims"/> shape used by the OIDC path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the userinfo call fails or yields no subject.</exception>
    internal async Task<OidcIdTokenClaims> FetchOAuth2UserClaimsAsync(
        OidcProvider provider, OidcDiscoveryDocument? discoveryDoc, string accessToken)
    {
        var settings = provider.OAuth2
            ?? throw new InvalidOperationException($"OAuth2 provider {provider.Name} has no settings configured");

        var userInfoEndpoint = discoveryDoc?.UserInfoEndpoint ?? settings.UserInfoEndpoint;
        if (string.IsNullOrEmpty(userInfoEndpoint))
            throw new InvalidOperationException($"OAuth2 provider {provider.Name} has no userinfo endpoint");

        var map = settings.ClaimMappings ?? new();

        using var user = await GetJsonAsync(userInfoEndpoint, accessToken);
        var root = user.RootElement;

        var sub = GetMappedClaim(root, map, SubClaim);
        if (string.IsNullOrWhiteSpace(sub))
            throw new InvalidOperationException("Userinfo response did not include a subject identifier");

        // An email taken straight from the userinfo response carries no verification signal; only the
        // dedicated email endpoint reports verification, so EmailVerified is asserted only for that source.
        var email = GetMappedClaim(root, map, EmailClaim);
        bool? emailVerified = null;
        if (string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(settings.UserInfoEmailEndpoint))
        {
            email = await FetchPrimaryVerifiedEmailAsync(settings.UserInfoEmailEndpoint, accessToken);
            emailVerified = email is not null ? true : null;
        }

        return new OidcIdTokenClaims
        {
            Sub = sub,
            Email = email,
            EmailVerified = emailVerified,
            Name = GetMappedClaim(root, map, NameClaim),
            PreferredUsername = GetMappedClaim(root, map, PreferredUsernameClaim),
            Picture = GetMappedClaim(root, map, PictureClaim),
        };
    }

    /// <summary>
    /// Fetches the primary, verified email from a provider's email-list endpoint. Used when the userinfo
    /// response carries no email (some providers expose email separately). The endpoint is expected to
    /// return a JSON array of objects with <c>email</c>, <c>primary</c>, and <c>verified</c> fields.
    /// Returns null when no verified email is available.
    /// </summary>
    private async Task<string?> FetchPrimaryVerifiedEmailAsync(string emailEndpoint, string accessToken)
    {
        using var emails = await GetJsonAsync(emailEndpoint, accessToken);
        if (emails.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        string? firstVerified = null;
        foreach (var entry in emails.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !GetJsonBool(entry, "verified"))
                continue;

            var address = GetStringProperty(entry, "email");
            if (address is null)
                continue;

            firstVerified ??= address;
            if (GetJsonBool(entry, "primary"))
                return address;
        }

        return firstVerified;
    }

    /// <summary>
    /// Reads a flag from a JSON object, tolerating providers that express booleans as a JSON bool,
    /// the string <c>"true"</c>, or the number <c>1</c>. Missing or any other value is treated as false.
    /// </summary>
    private static bool GetJsonBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(prop.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.Number => prop.TryGetInt64(out var n) && n != 0,
            _ => false,
        };
    }

    /// <summary>
    /// Issues an authenticated bearer-token GET and returns the parsed JSON. A <c>User-Agent</c> is
    /// always sent because some providers (e.g. GitHub) reject API requests without one.
    /// </summary>
    private async Task<JsonDocument> GetJsonAsync(string url, string accessToken)
    {
        var httpClient = _httpClientFactory.CreateClient("OidcProvider");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Nocturne", "1.0"));

        var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OAuth2 userinfo call to {Url} failed: {StatusCode}", url, response.StatusCode);
            throw new InvalidOperationException($"OAuth2 userinfo request failed: {response.StatusCode}");
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>
    /// Reads a standard claim from a userinfo response using the provider's field mapping, falling back
    /// to the standard claim name itself. String values pass through; numeric values are stringified so
    /// numeric subject identifiers map cleanly to the string <c>sub</c> claim.
    /// </summary>
    private static string? GetMappedClaim(JsonElement root, Dictionary<string, string> map, string standardClaim)
    {
        var field = map.TryGetValue(standardClaim, out var mapped) && !string.IsNullOrEmpty(mapped)
            ? mapped
            : standardClaim;

        if (!root.TryGetProperty(field, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null,
        };
    }

    private static string? GetStringProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>
    /// Generates a cryptographically secure URL-safe Base64 random string of the specified byte length.
    /// </summary>
    /// <param name="length">Number of random bytes to generate.</param>
    /// <returns>A URL-safe Base64 string (without padding).</returns>
    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Base64Url.Encode(bytes);
    }

    /// <summary>
    /// Serialises an <see cref="OidcStateData"/> object to an authenticated, URL-safe state string.
    /// </summary>
    /// <param name="data">The state data to encode.</param>
    /// <returns>A data-protected state string.</returns>
    /// <remarks>
    /// The payload is protected rather than plain-encoded because the callback trusts what it
    /// carries. <c>Intent</c> selects which flow runs and <c>SubjectId</c> names the account an
    /// identity is bound to, so an unauthenticated blob lets a caller mint state for any subject.
    /// The state cookie is a CSRF defence, not an integrity one — a caller acting as its own HTTP
    /// client supplies both halves of the double-submit.
    /// </remarks>
    private string EncodeState(OidcStateData data) =>
        _stateProtector.Protect(JsonSerializer.Serialize(data));

    /// <summary>
    /// Deserialises an <see cref="OidcStateData"/> object from a protected state string.
    /// </summary>
    /// <param name="encoded">State string produced by <see cref="EncodeState"/>.</param>
    /// <returns>The decoded <see cref="OidcStateData"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the state cannot be deserialised.</exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown when the state was not produced by this instance's key ring, including any attempt
    /// to forge or tamper with it.
    /// </exception>
    private OidcStateData DecodeState(string encoded)
    {
        var json = _stateProtector.Unprotect(encoded);

        return JsonSerializer.Deserialize<OidcStateData>(json)
            ?? throw new InvalidOperationException("Invalid state data");
    }

    /// <inheritdoc />
    public string? TryReadTenantSlug(string state)
    {
        if (string.IsNullOrEmpty(state))
            return null;

        try
        {
            return DecodeState(state).TenantSlug;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read a tenant slug from the OIDC state");
            return null;
        }
    }

    /// <summary>
    /// Extracts a short human-readable device description from a user-agent string.
    /// </summary>
    /// <param name="userAgent">The raw HTTP user-agent header value, or <see langword="null"/>.</param>
    /// <returns>A brief platform label (e.g. <c>Windows</c>, <c>iPhone</c>), a truncated user-agent, or <see langword="null"/>.</returns>
    private static string? ParseUserAgentShort(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return null;

        // Simple parsing - in production you might use a library like UAParser
        if (userAgent.Contains("Windows"))
            return "Windows";
        if (userAgent.Contains("Macintosh"))
            return "Mac";
        if (userAgent.Contains("Linux"))
            return "Linux";
        if (userAgent.Contains("iPhone"))
            return "iPhone";
        if (userAgent.Contains("iPad"))
            return "iPad";
        if (userAgent.Contains("Android"))
            return "Android";

        return userAgent.Length > 50 ? userAgent[..50] + "..." : userAgent;
    }

    #endregion

    #region Private Classes

    /// <summary>
    /// State data encoded in the state parameter
    /// </summary>
    internal class OidcStateData
    {
        public Guid ProviderId { get; set; }
        public string? ReturnUrl { get; set; }
        public string? Nonce { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public string Intent { get; set; } = "login";
        public Guid? SubjectId { get; set; }
        public string? TenantSlug { get; set; }
    }

    /// <summary>
    /// Token response from OIDC provider
    /// </summary>
    private class OidcProviderTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")]
        public string IdToken { get; set; } = string.Empty;
        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = "Bearer";
        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    /// <summary>
    /// Claims extracted from ID token
    /// </summary>
    internal class OidcIdTokenClaims
    {
        public string Sub { get; set; } = string.Empty;
        public string? Email { get; set; }
        public bool? EmailVerified { get; set; }
        public string? Name { get; set; }
        public string? PreferredUsername { get; set; }
        public string? GivenName { get; set; }
        public string? FamilyName { get; set; }
        public string? Picture { get; set; }
        public string? Nonce { get; set; }
        public string? Sid { get; set; } // Session ID
        public string? SessionId => Sid;
        public long? Iat { get; set; }
        public long? Exp { get; set; }
    }

    #endregion
}
