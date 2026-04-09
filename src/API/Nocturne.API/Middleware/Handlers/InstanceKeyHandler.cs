using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.Authorization;

namespace Nocturne.API.Middleware.Handlers;

/// <summary>
/// Authentication handler for instance key (infrastructure service authentication).
/// Validates the SHA1 hash of the instance key sent in the X-Instance-Key header.
/// Used by SvelteKit SSR and the WebSocket bridge to authenticate with the API.
/// Grants full admin (*) permissions.
/// </summary>
public class InstanceKeyHandler : IAuthHandler
{
    public int Priority => 55;

    public string Name => "InstanceKeyHandler";

    private readonly string _instanceKeyHash;
    private readonly ILogger<InstanceKeyHandler> _logger;

    public InstanceKeyHandler(IConfiguration configuration, ILogger<InstanceKeyHandler> logger)
    {
        _logger = logger;

        var instanceKey =
            configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
            ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
            ?? "";
        _instanceKeyHash = !string.IsNullOrEmpty(instanceKey) ? HashUtils.Sha1Hex(instanceKey) : "";
    }

    public Task<AuthResult> AuthenticateAsync(HttpContext context)
    {
        var header = context.Request.Headers["X-Instance-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(header))
            return Task.FromResult(AuthResult.Skip());

        if (string.IsNullOrEmpty(_instanceKeyHash))
        {
            _logger.LogWarning("X-Instance-Key header provided but no instance key configured");
            return Task.FromResult(AuthResult.Failure("Instance key not configured"));
        }

        if (!string.Equals(header, _instanceKeyHash, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid instance key provided");
            return Task.FromResult(AuthResult.Failure("Invalid instance key"));
        }

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
