using Microsoft.AspNetCore.DataProtection;
using Nocturne.API.Services.Auth;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;
using Nocturne.API.Extensions;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for guest session cookies. Validates an encrypted
/// grant ID stored in the <c>nocturne-guest-session</c> cookie against
/// <see cref="IGuestLinkService.ValidateSessionAsync"/>, via
/// <see cref="GuestSessionCacheService"/> to avoid per-request database hits.
/// The resolved grant's tenant must match the tenant resolved for the request.
/// </summary>
public class GuestSessionHandler : IAuthHandler
{
    private const string CookieName = "nocturne-guest-session";
    private const string ProtectorPurpose = "GuestSession";
    private const string InvalidSessionError = "Guest session expired or revoked";

    /// <inheritdoc />
    public int Priority => 52;

    /// <inheritdoc />
    public string Name => "GuestSessionHandler";

    private readonly IDataProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GuestSessionCacheService _sessionCache;
    private readonly ILogger<GuestSessionHandler> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="GuestSessionHandler"/>.
    /// </summary>
    public GuestSessionHandler(
        IDataProtectionProvider dataProtectionProvider,
        IServiceScopeFactory scopeFactory,
        GuestSessionCacheService sessionCache,
        ILogger<GuestSessionHandler> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _scopeFactory = scopeFactory;
        _sessionCache = sessionCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var cookie = context.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(cookie))
            return AuthResult.Skip();

        // Decrypt the cookie to get the grant ID
        Guid grantId;
        try
        {
            var decrypted = _protector.Unprotect(cookie);
            if (!Guid.TryParse(decrypted, out grantId))
            {
                _logger.LogDebug("Guest session cookie contained invalid GUID, clearing");
                ClearGuestSessionCookie(context);
                return AuthResult.Skip();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Guest session cookie decryption failed, clearing");
            ClearGuestSessionCookie(context);
            return AuthResult.Skip();
        }

        // A guest grant belongs to exactly one tenant, so a session cannot be validated
        // without a resolved tenant to validate it against.
        if (context.GetTenantContext() is not { } tenantCtx)
        {
            _logger.LogDebug("Guest session {GrantId} presented with no resolved tenant, clearing cookie", grantId);
            ClearGuestSessionCookie(context);
            return AuthResult.Failure(InvalidSessionError);
        }

        // Check cache first, then validate against the database
        if (!_sessionCache.TryGet(tenantCtx.TenantId, grantId, out var session))
        {
            using var scope = _scopeFactory.CreateScope();

            // Propagate tenant context into the child scope so RLS allows
            // the oauth_grants query. Without this, the scoped DbContext has
            // TenantId = Guid.Empty and RLS silently filters out the row.
            var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
            tenantAccessor.SetTenant(tenantCtx);

            var guestLinkService = scope.ServiceProvider.GetRequiredService<IGuestLinkService>();
            session = await guestLinkService.ValidateSessionAsync(grantId);
            _sessionCache.Set(tenantCtx.TenantId, grantId, session);
        }

        if (session is null)
        {
            _logger.LogDebug("Guest session {GrantId} is no longer valid, clearing cookie", grantId);
            ClearGuestSessionCookie(context);
            return AuthResult.Failure(InvalidSessionError);
        }

        // Re-bind the session to the resolved tenant independently of the cache key: the grant
        // carries its own tenant, so a resolution reached by any route is still checked here.
        if (session.TenantId != tenantCtx.TenantId)
        {
            _logger.LogWarning(
                "Guest session {GrantId} belongs to tenant {GrantTenantId} but was presented to tenant {ResolvedTenantId}",
                grantId, session.TenantId, tenantCtx.TenantId);
            ClearGuestSessionCookie(context);
            return AuthResult.Failure(InvalidSessionError);
        }

        var authContext = new AuthContext
        {
            IsAuthenticated = true,
            AuthType = AuthType.Guest,
            SubjectId = null,
            ActingAsSubjectId = session.DataOwnerSubjectId,
            Scopes = session.Scopes.ToList(),
            TokenId = session.GrantId,
            ExpiresAt = new DateTimeOffset(session.ExpiresAt, TimeSpan.Zero),
        };

        return AuthResult.Success(authContext);
    }

    /// <summary>
    /// Encrypts the grant ID and sets the guest session cookie on the response.
    /// </summary>
    public void SetGuestSessionCookie(HttpContext context, Guid grantId, DateTime expiresAt)
    {
        var encrypted = _protector.Protect(grantId.ToString());
        context.Response.Cookies.Append(CookieName, encrypted, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero),
        });
    }

    /// <summary>
    /// Removes the guest session cookie from the response.
    /// </summary>
    public static void ClearGuestSessionCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            Path = "/",
        });
    }
}
