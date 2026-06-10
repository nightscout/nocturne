using Nocturne.API.Authorization;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for instance key (infrastructure service authentication).
/// Validates the SHA1 hash of the instance key sent in the X-Instance-Key header
/// (with an X-Instance-Service marker) via <see cref="IInstanceKeyValidator"/>.
/// Used by internal services (SSR, WebSocket bridge) and trusted automation to
/// authenticate with the API. Grants full admin (*) permissions.
/// </summary>
public class InstanceKeyHandler : IAuthHandler
{
    public int Priority => 55;

    public string Name => "InstanceKeyHandler";

    private readonly IInstanceKeyValidator _validator;
    private readonly ILogger<InstanceKeyHandler> _logger;

    public InstanceKeyHandler(IInstanceKeyValidator validator, ILogger<InstanceKeyHandler> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        switch (_validator.Classify(context))
        {
            case InstanceKeyRequestKind.Absent:
                // No header, or a bare key with no service marker — fall through to
                // public-access / unauthenticated handling.
                return Task.FromResult(AuthResult.Skip());

            case InstanceKeyRequestKind.NotConfigured:
                _logger.LogWarning("X-Instance-Key header provided but no instance key configured");
                return Task.FromResult(AuthResult.Failure("Instance key not configured"));

            case InstanceKeyRequestKind.Invalid:
                _logger.LogWarning("Invalid instance key provided");
                return Task.FromResult(AuthResult.Failure("Invalid instance key"));

            default:
                _logger.LogDebug("Instance key authentication successful");
                return Task.FromResult(AuthResult.Success(new AuthContext
                {
                    IsAuthenticated = true,
                    AuthType = AuthType.InstanceKey,
                    SubjectName = "instance-service",
                    Permissions = ["*"],
                    Roles = ["admin"],
                    // The instance key is the highest-trust service credential in
                    // the system (shared only with trusted in-cluster services). It
                    // already skips tenant membership checks and grants permission
                    // wildcard, so it must also carry platform_admin so that cross-
                    // tenant admin endpoints (e.g. /api/v4/admin/tenants/provision)
                    // are callable by provisioners. Without this, external admin
                    // calls authenticated via X-Instance-Key get 403 Forbidden.
                    IsPlatformAdmin = true,
                }));
        }
    }
}
