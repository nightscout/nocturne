using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Nocturne.Core.Contracts.Auth;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for guest session cookies. Validates an encrypted
/// grant ID stored in the <c>nocturne-guest-session</c> cookie against
/// <see cref="IGuestLinkService.ValidateSessionAsync"/>, with a 30-second
/// memory cache to avoid per-request database hits.
/// </summary>
public class GuestSessionHandler : IAuthHandler
{
    private const string CookieName = "nocturne-guest-session";
    private const string ProtectorPurpose = "GuestSession";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public int Priority => 52;

    /// <inheritdoc />
    public string Name => "GuestSessionHandler";

    private readonly IDataProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GuestSessionHandler> _logger;

    /// <summary>
    /// Creates a new instance of <see cref="GuestSessionHandler"/>.
    /// </summary>
    public GuestSessionHandler(
        IDataProtectionProvider dataProtectionProvider,
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ILogger<GuestSessionHandler> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _scopeFactory = scopeFactory;
        _cache = cache;
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

        // Check cache first, then validate against the database
        var cacheKey = $"guest-session:{grantId}";
        if (!_cache.TryGetValue(cacheKey, out GuestSessionInfo? session))
        {
            using var scope = _scopeFactory.CreateScope();

            // Propagate tenant context into the child scope so RLS allows
            // the oauth_grants query. Without this, the scoped DbContext has
            // TenantId = Guid.Empty and RLS silently filters out the row.
            if (context.Items["TenantContext"] is TenantContext tenantCtx)
            {
                var tenantAccessor = scope.ServiceProvider.GetRequiredService<ITenantAccessor>();
                tenantAccessor.SetTenant(tenantCtx);
            }

            var guestLinkService = scope.ServiceProvider.GetRequiredService<IGuestLinkService>();
            session = await guestLinkService.ValidateSessionAsync(grantId);
            _cache.Set(cacheKey, session, CacheDuration);
        }

        if (session is null)
        {
            _logger.LogDebug("Guest session {GrantId} is no longer valid, clearing cookie", grantId);
            ClearGuestSessionCookie(context);
            return AuthResult.Failure("Guest session expired or revoked");
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
