using Nocturne.Core.Models.Authorization;
using Nocturne.Core.Models.Configuration;

namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// Service for handling OIDC authentication flows.
/// </summary>
/// <seealso cref="IOidcProviderService"/>
/// <seealso cref="IRefreshTokenService"/>
/// <seealso cref="IJwtService"/>
/// <seealso cref="ISubjectService"/>
public interface IOidcAuthService
{
    /// <summary>
    /// Generate an authorization URL for initiating OIDC login
    /// </summary>
    /// <param name="providerId">OIDC provider ID (null = use default)</param>
    /// <param name="returnUrl">URL to return to after login</param>
    /// <param name="tenantSlug">Tenant the login is being performed against</param>
    /// <returns>Authorization request containing URL and state</returns>
    /// <remarks>
    /// The state is always generated and cryptographically protected here. There is deliberately
    /// no caller-supplied override: the callback trusts the intent and subject the state carries.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when the provider is not found, not configured, or not enabled.</exception>
    Task<OidcAuthorizationRequest> GenerateAuthorizationUrlAsync(
        Guid? providerId,
        string? returnUrl = null,
        string? tenantSlug = null
    );

    /// <summary>
    /// Reads the tenant slug out of a protected state parameter, for routing a callback that
    /// landed on the apex to the subdomain the login was initiated from.
    /// </summary>
    /// <param name="state">The state parameter as received on the callback.</param>
    /// <returns>
    /// The tenant slug the login was initiated against, or <c>null</c> when the state was not
    /// issued by this instance, has been tampered with, or carries no slug (an apex login).
    /// </returns>
    /// <remarks>
    /// This is the only part of the state readable before the callback is handled, and it goes
    /// through the same protector as the rest of the payload — the redirect target is therefore
    /// derived from data this instance signed, not from a caller-supplied value. Besides
    /// <see cref="IsRecoveryState"/>, it exposes only the slug: nothing else in the state is safe
    /// to act on before the full callback validation in <see cref="HandleCallbackAsync"/> has run.
    /// </remarks>
    string? TryReadTenantSlug(string state);

    /// <summary>
    /// Whether a protected link state was issued by
    /// <see cref="GenerateRecoveryLinkAuthorizationUrlAsync"/>, so the link callback knows which
    /// proof of identity to require. It picks the validation only; the callback still runs all of it.
    /// </summary>
    bool IsRecoveryState(string state);

    /// <summary>
    /// Handle the OIDC callback - exchange code for tokens and create session
    /// </summary>
    /// <param name="code">Authorization code from provider</param>
    /// <param name="state">State parameter for CSRF verification</param>
    /// <param name="expectedState">Expected state value from cookie</param>
    /// <param name="ipAddress">Client IP address</param>
    /// <param name="currentTenantId">
    /// The tenant the login is being performed against (the resolved subdomain tenant).
    /// When set, the resolved subject must be a member of this tenant or no session is issued.
    /// </param>
    /// <param name="trustedDeviceToken">
    /// The trusted-device cookie the callback request carried, if any. See
    /// <see cref="ITotpService.GetSecondFactorRequirementAsync"/>.
    /// </param>
    /// <returns>Authentication result with session tokens</returns>
    Task<OidcCallbackResult> HandleCallbackAsync(
        string code,
        string state,
        string expectedState,
        string? ipAddress = null,
        string? userAgent = null,
        Guid? currentTenantId = null,
        string? trustedDeviceToken = null
    );

    /// <param name="ipAddress">Client IP address</param>
    /// <returns>New session tokens or null if refresh failed</returns>
    Task<OidcTokenResponse?> RefreshSessionAsync(
        string refreshToken,
        string? ipAddress = null,
        string? userAgent = null
    );

    /// <summary>
    /// Ends the sessions the caller signs out of, every token of each chain: the session the
    /// presented refresh token belongs to, whether that token is live, rotated out or revoked, and
    /// <paramref name="signedIn"/>. When both name one session it ends once. No other session of
    /// the subject is touched.
    /// </summary>
    /// <param name="refreshToken">The refresh token the caller presents, if any.</param>
    /// <param name="signedIn">The session the caller's access token names, if it is a session.</param>
    /// <param name="providerId">Provider ID for RP-initiated logout (optional)</param>
    /// <returns>Logout result with optional provider logout URL</returns>
    Task<OidcLogoutResult> LogoutAsync(string? refreshToken, RefreshTokenSession? signedIn, Guid? providerId = null);

    /// <returns>User info or null if not found</returns>
    Task<OidcUserInfo?> GetUserInfoAsync(Guid subjectId);

    /// <summary>
    /// Validate a session (check if refresh token is valid)
    /// </summary>
    /// <returns>Subject ID if valid, null otherwise</returns>
    Task<Guid?> ValidateSessionAsync(string refreshToken);

    /// <summary>
    /// Generate an authorization URL for linking an additional OIDC identity to an already-authenticated subject.
    /// </summary>
    /// <param name="subjectId">The currently authenticated subject's ID.</param>
    /// <param name="returnUrl">URL to return to after the linking flow completes.</param>
    /// <param name="tenantSlug">Optional tenant slug for subdomain-scoped deployments.</param>
    /// <returns>Authorization request containing URL and state for CSRF verification.</returns>
    Task<OidcAuthorizationRequest> GenerateLinkAuthorizationUrlAsync(
        Guid providerId, Guid subjectId, string? returnUrl = null, string? tenantSlug = null);

    /// <summary>
    /// Handle the OIDC callback for an account-linking flow initiated by <see cref="GenerateLinkAuthorizationUrlAsync"/>.
    /// </summary>
    /// <param name="code">Authorization code from the provider.</param>
    /// <param name="state">State parameter returned by the provider.</param>
    /// <param name="expectedState">Expected state value stored in the session cookie.</param>
    /// <param name="authenticatedSubjectId">The subject ID of the currently authenticated user.</param>
    /// <param name="ipAddress">Client IP address for audit logging.</param>
    /// <param name="userAgent">Client user-agent string for audit logging.</param>
    /// <returns>An <see cref="OidcLinkResult"/> indicating success or failure of the link attempt.</returns>
    Task<OidcLinkResult> HandleLinkCallbackAsync(
        string code, string state, string expectedState,
        Guid authenticatedSubjectId,
        string? ipAddress = null, string? userAgent = null);

    /// <summary>
    /// Generate an authorisation URL for linking an identity to <paramref name="subjectId"/> under
    /// the recovery session that names it. The callback is the link callback, and its state
    /// completes only with that recovery session (<see cref="HandleRecoveryLinkCallbackAsync"/>).
    /// </summary>
    /// <param name="providerId">The provider to link; it must be enabled.</param>
    /// <param name="subjectId">The subject the recovery session is for.</param>
    /// <param name="returnUrl">URL to return to after the flow completes.</param>
    /// <param name="tenantSlug">The tenant the recovery session belongs to.</param>
    Task<OidcAuthorizationRequest> GenerateRecoveryLinkAuthorizationUrlAsync(
        Guid providerId, Guid subjectId, string? returnUrl = null, string? tenantSlug = null);

    /// <summary>
    /// Handle the link callback of a flow started by <see cref="GenerateRecoveryLinkAuthorizationUrlAsync"/>:
    /// attaches the identity to <paramref name="recoverySubjectId"/>, the subject of the recovery
    /// session the callback presents, when the state was issued for a recovery link for that
    /// subject. An identity already linked to another subject is refused.
    /// </summary>
    Task<OidcLinkResult> HandleRecoveryLinkCallbackAsync(
        string code, string state, string expectedState, Guid recoverySubjectId);

    /// <summary>
    /// Generate an authorization URL for OIDC-based owner creation during setup.
    /// Encodes the pre-created subject ID in state so the callback can link the identity.
    /// </summary>
    Task<OidcAuthorizationRequest> GenerateSetupAuthorizationUrlAsync(
        Guid providerId, Guid subjectId, string? tenantSlug = null);

    /// <summary>
    /// Handle the OIDC callback for setup owner creation: links the proved identity to the
    /// pre-created subject, or adopts the subject that already holds it. It issues no session;
    /// the caller finishes setup and then the sign-in.
    /// </summary>
    /// <param name="tenantId">
    /// The tenant being set up, whose setup the link or adoption claims. Fails with
    /// <c>setup_already_complete</c> when another completion has claimed it first.
    /// </param>
    Task<OidcSetupCallbackResult> HandleSetupCallbackAsync(string code, string state, string expectedState, Guid tenantId);
}

/// <summary>
/// Result of an OIDC account-linking callback
/// </summary>
public class OidcLinkResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }
    public Guid? IdentityId { get; set; }
    public string? ReturnUrl { get; set; }

    /// <summary>The provider the linked identity belongs to; set on success.</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>Whether the link added the identity, rather than finding it already on the account.</summary>
    public bool Added { get; set; }

    public static OidcLinkResult Succeeded(
        Guid identityId, string? returnUrl = null, Guid? providerId = null, bool added = true)
        => new() { Success = true, IdentityId = identityId, ReturnUrl = returnUrl, ProviderId = providerId, Added = added };
    public static OidcLinkResult Failed(string error, string? description = null)
        => new() { Success = false, Error = error, ErrorDescription = description };
}

public class OidcAuthorizationRequest
{
    /// <summary>
    /// Full authorization URL to redirect to
    /// </summary>
    public string AuthorizationUrl { get; set; } = string.Empty;

    /// <summary>
    /// State parameter (should be stored in cookie for verification)
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Nonce value (for ID token verification)
    /// </summary>
    public string? Nonce { get; set; }

    public Guid ProviderId { get; set; }

    /// <summary>
    /// Return URL after authentication
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// State expiration time
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

public class OidcCallbackResult
{
    public bool Success { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Error description if failed
    /// </summary>
    public string? ErrorDescription { get; set; }

    /// <summary>
    /// Session tokens if successful
    /// </summary>
    public OidcTokenResponse? Tokens { get; set; }

    /// <summary>
    /// User information if successful
    /// </summary>
    public OidcUserInfo? UserInfo { get; set; }

    /// <summary>
    /// Return URL extracted from state
    /// </summary>
    public string? ReturnUrl { get; set; }

    /// <summary>
    /// True when authentication succeeded but the subject is not a member of the tenant
    /// being logged into. No session is issued; the caller should redirect to the tenant's
    /// login page (where the request-membership option is offered when the tenant allows it).
    /// </summary>
    public bool IsAccessDenied { get; set; }

    /// <summary>
    /// True when the sign-in completion refused the subject and already wrote the refusal's
    /// <c>FailedAuth</c> row, so the caller writes none.
    /// </summary>
    public bool RefusalAudited { get; set; }

    /// <summary>
    /// Subject resolved from the OIDC identity, when known. Set for successful and
    /// access-denied results so the caller can write an accurate audit entry.
    /// </summary>
    public Guid? SubjectId { get; set; }

    /// <summary>
    /// The provider that authenticated the identity. <see cref="OidcUserInfo.ProviderName"/> is
    /// for display; this is the id the login form keys its buttons by.
    /// </summary>
    public Guid? ProviderId { get; set; }

    /// <summary>
    /// Set when the identity was accepted but the subject has an authenticator enrolled. No
    /// session is issued; the token is redeemed with a code at <c>POST /api/auth/totp/login</c>,
    /// as after a passkey assertion.
    /// </summary>
    public string? StepUpToken { get; set; }

    /// <summary>
    /// True when the subject has an authenticator enrolled and the session was issued without a
    /// code because the callback came from a device the subject trusted.
    /// </summary>
    public bool UsedTrustedDevice { get; set; }

    public static OidcCallbackResult Succeeded(
        OidcTokenResponse tokens,
        OidcUserInfo userInfo,
        string? returnUrl = null,
        Guid? providerId = null,
        bool usedTrustedDevice = false
    ) =>
        new()
        {
            Success = true,
            Tokens = tokens,
            UserInfo = userInfo,
            ReturnUrl = returnUrl,
            SubjectId = tokens.SubjectId,
            ProviderId = providerId,
            UsedTrustedDevice = usedTrustedDevice,
        };

    public static OidcCallbackResult Failed(string error, string? description = null) =>
        new()
        {
            Success = false,
            Error = error,
            ErrorDescription = description,
        };

    public static OidcCallbackResult SecondFactorRequired(
        Guid subjectId, string stepUpToken, string? returnUrl, Guid providerId) =>
        new()
        {
            Success = false,
            Error = "totp_required",
            SubjectId = subjectId,
            StepUpToken = stepUpToken,
            ReturnUrl = returnUrl,
            ProviderId = providerId,
        };

    /// <summary>
    /// SHA-256 hex digest of the issuer and sub claim of an identity refused as not a member
    /// that has no subject to name. Null when <see cref="SubjectId"/> names the person.
    /// </summary>
    public string? ExternalIdentityDigest { get; set; }

    /// <summary>
    /// Create an access-denied result for an authenticated identity that is not a member
    /// of the tenant being logged into. No session is issued, and an identity the instance has
    /// not seen has no subject, so <paramref name="subjectId"/> is null for it.
    /// </summary>
    public static OidcCallbackResult NotAMember(
        Guid? subjectId, Guid providerId, string? externalIdentityDigest, string? returnUrl = null) =>
        new()
        {
            Success = false,
            IsAccessDenied = true,
            SubjectId = subjectId,
            ProviderId = providerId,
            ExternalIdentityDigest = externalIdentityDigest,
            Error = "not_a_member",
            ErrorDescription = "You are not a member of this account.",
            ReturnUrl = returnUrl,
        };
}

/// <summary>
/// OIDC token response (our session tokens, not provider tokens)
/// </summary>
public class OidcTokenResponse
{
    /// <summary>
    /// Access token (short-lived JWT)
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>
    /// Refresh token (long-lived, for session continuity)
    /// </summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>
    /// Token type (always "Bearer")
    /// </summary>
    public string TokenType { get; set; } = "Bearer";

    /// <summary>
    /// Access token expiration in seconds
    /// </summary>
    public int ExpiresIn { get; set; }

    /// <summary>
    /// Refresh token expiration in seconds
    /// </summary>
    public int RefreshExpiresIn { get; set; }

    /// <summary>
    /// Absolute expiration time
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public Guid SubjectId { get; set; }
}

public class OidcUserInfo
{
    public Guid SubjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool? EmailVerified { get; set; }

    public string? Picture { get; set; }

    public List<string> Roles { get; set; } = new();

    public List<string> Permissions { get; set; } = new();

    public string? ProviderName { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// User's preferred language code (e.g., "en", "fr", "de")
    /// </summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Per-user display preferences (units, time format, theme, chart style, etc.).
    /// </summary>
    public UserDisplayPreferences? Preferences { get; set; }

    /// <summary>
    /// URL to the subject's uploaded avatar image
    /// </summary>
    public string? AvatarUrl { get; set; }
}

public class OidcLogoutResult
{
    public bool Success { get; set; }

    /// <summary>
    /// URL for RP-initiated logout at the provider (optional)
    /// </summary>
    public string? ProviderLogoutUrl { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    public static OidcLogoutResult Succeeded(string? providerLogoutUrl = null) =>
        new() { Success = true, ProviderLogoutUrl = providerLogoutUrl };

    public static OidcLogoutResult Failed(string error) => new() { Success = false, Error = error };
}

public class OidcSetupCallbackResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? ErrorDescription { get; set; }

    /// <summary>The setup owner, holding the proved identity.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>The provider that proved the identity.</summary>
    public Guid? ProviderId { get; set; }

    /// <summary>The provider's session id (<c>sid</c>) from the ID token, for the session the sign-in issues.</summary>
    public string? OidcSessionId { get; set; }

    public string? ReturnUrl { get; set; }

    public static OidcSetupCallbackResult Succeeded(
        Guid subjectId, Guid providerId, string? oidcSessionId, string? returnUrl = null) =>
        new()
        {
            Success = true,
            SubjectId = subjectId,
            ProviderId = providerId,
            OidcSessionId = oidcSessionId,
            ReturnUrl = returnUrl,
        };

    public static OidcSetupCallbackResult Failed(string error, string? description = null) =>
        new() { Success = false, Error = error, ErrorDescription = description };
}
