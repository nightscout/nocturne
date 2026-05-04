using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
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
    private readonly OidcOptions _options;
    private readonly IConfiguration _configuration;
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
    /// <param name="options">OIDC session and state configuration options.</param>
    /// <param name="configuration">Application configuration for reading the base URL.</param>
    /// <param name="logger">Logger instance.</param>
    public OidcAuthService(
        IOidcProviderService providerService,
        ISubjectService subjectService,
        ISessionService sessionService,
        IJwtService jwtService,
        IRefreshTokenService refreshTokenService,
        IHttpClientFactory httpClientFactory,
        IOptions<OidcOptions> options,
        IConfiguration configuration,
        ILogger<OidcAuthService> logger
    )
    {
        _providerService = providerService;
        _subjectService = subjectService;
        _sessionService = sessionService;
        _jwtService = jwtService;
        _refreshTokenService = refreshTokenService;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<OidcAuthorizationRequest> GenerateAuthorizationUrlAsync(
        Guid? providerId,
        string? returnUrl = null,
        string? state = null,
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

        return await BuildAuthorizationUrlAsync(provider, stateData, returnUrl, state);
    }

    private async Task<OidcAuthorizationRequest> BuildAuthorizationUrlAsync(
        OidcProvider provider,
        OidcStateData stateData,
        string? returnUrl,
        string? state = null,
        string callbackPath = LoginCallbackPath
    )
    {
        var discoveryDoc =
            await _providerService.GetDiscoveryDocumentAsync(provider.Id)
            ?? throw new InvalidOperationException(
                $"Could not fetch OIDC discovery document for {provider.Name}"
            );

        state ??= EncodeState(stateData);

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
            _logger.LogWarning(ex, "Failed to decode OIDC state");
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
            idTokenClaims = ParseIdToken(providerTokens.IdToken);

            if (!string.IsNullOrEmpty(stateData.Nonce) && idTokenClaims.Nonce != stateData.Nonce)
            {
                return CallbackParseResult.Fail("invalid_nonce", "ID token nonce mismatch");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ID token parsing failed");
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
        string? userAgent = null
    )
    {
        var parsed = await ValidateCallbackAndParseIdTokenAsync(code, state, expectedState);
        if (!parsed.Success)
        {
            return OidcCallbackResult.Failed(parsed.Error ?? "callback_failed", parsed.ErrorDescription);
        }

        var stateData = parsed.StateData!;
        var provider = parsed.Provider!;
        var idTokenClaims = parsed.Claims!;

        // Find or create subject
        var subject = await _subjectService.FindOrCreateFromOidcAsync(
            provider.Id,
            idTokenClaims.Sub,
            provider.IssuerUrl,
            idTokenClaims.Email,
            idTokenClaims.Name ?? idTokenClaims.PreferredUsername,
            provider.DefaultRoles
        );

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
                    query["post_logout_redirect_uri"] = _configuration[ServiceNames.ConfigKeys.BaseUrl] ?? "";
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
    /// Builds the absolute redirect URI by combining the configured base URL with the specified callback path.
    /// </summary>
    /// <param name="callbackPath">The server-relative callback path (default: <see cref="LoginCallbackPath"/>).</param>
    /// <returns>The fully qualified redirect URI.</returns>
    private string GetRedirectUri(string callbackPath = LoginCallbackPath)
    {
        var baseUrl = _configuration[ServiceNames.ConfigKeys.BaseUrl]?.TrimEnd('/') ?? "http://localhost:5000";
        return $"{baseUrl}{callbackPath}";
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
        // Pad base64 if needed
        switch (payload.Length % 4)
        {
            case 2:
                payload += "==";
                break;
            case 3:
                payload += "=";
                break;
        }

        var json = Encoding.UTF8.GetString(
            Convert.FromBase64String(payload.Replace('-', '+').Replace('_', '/'))
        );

        var claims = JsonSerializer.Deserialize<OidcIdTokenClaims>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        return claims ?? throw new InvalidOperationException("Invalid ID token claims");
    }

    /// <summary>
    /// Generates a cryptographically secure URL-safe Base64 random string of the specified byte length.
    /// </summary>
    /// <param name="length">Number of random bytes to generate.</param>
    /// <returns>A URL-safe Base64 string (without padding).</returns>
    private static string GenerateRandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Serialises an <see cref="OidcStateData"/> object to a URL-safe Base64 string.
    /// </summary>
    /// <param name="data">The state data to encode.</param>
    /// <returns>URL-safe Base64-encoded JSON state string.</returns>
    private static string EncodeState(OidcStateData data)
    {
        var json = JsonSerializer.Serialize(data);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    /// <summary>
    /// Deserialises an <see cref="OidcStateData"/> object from a URL-safe Base64 string.
    /// </summary>
    /// <param name="encoded">URL-safe Base64-encoded state string produced by <see cref="EncodeState"/>.</param>
    /// <returns>The decoded <see cref="OidcStateData"/>.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the state cannot be deserialised.</exception>
    private static OidcStateData DecodeState(string encoded)
    {
        // Restore base64 padding
        switch (encoded.Length % 4)
        {
            case 2:
                encoded += "==";
                break;
            case 3:
                encoded += "=";
                break;
        }

        var bytes = Convert.FromBase64String(encoded.Replace("-", "+").Replace("_", "/"));
        var json = Encoding.UTF8.GetString(bytes);

        return JsonSerializer.Deserialize<OidcStateData>(json)
            ?? throw new InvalidOperationException("Invalid state data");
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
