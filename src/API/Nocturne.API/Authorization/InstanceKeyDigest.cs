using Nocturne.Connectors.Core.Utilities;
using Nocturne.Core.Constants;

namespace Nocturne.API.Authorization;

/// <summary>
/// Resolves the SHA-256 hex digest of the configured instance key.
/// </summary>
/// <remarks>
/// Single source of truth for how the instance key is located in configuration and
/// hashed, shared by <see cref="InstanceKeyValidator"/> (which checks inbound
/// credentials) and by services that present the digest on outbound calls to other
/// Nocturne services (see
/// <see cref="Services.Alerts.Providers.ChatBotProvider"/>).
/// </remarks>
public static class InstanceKeyDigest
{
    /// <summary>
    /// Returns the digest of the configured instance key, or an empty string when no
    /// instance key is configured.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var instanceKey = ResolveKey(configuration);

        return !string.IsNullOrEmpty(instanceKey) ? HashUtils.Sha256Hex(instanceKey) : "";
    }

    /// <summary>
    /// Returns the configured instance key itself, or an empty string when none is configured.
    /// For the callers that need the key as signing material rather than as a credential to
    /// compare (see <see cref="RateLimiting.ClientRateLimitKey"/>).
    /// </summary>
    public static string ResolveKey(IConfiguration configuration) =>
        configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
        ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
        ?? "";

    /// <summary>
    /// The shortest instance key the API starts with. The key signs JWTs (HMAC-SHA256, which
    /// <see cref="Services.Auth.JwtService"/> refuses below this length) and seeds every other
    /// derived secret, so a short key is refused at startup rather than on the first sign-in.
    /// </summary>
    public const int MinimumLength = 32;

    /// <summary>
    /// Returns the configured instance key, throwing when it is missing or shorter than
    /// <see cref="MinimumLength"/>.
    /// </summary>
    public static string RequireKey(IConfiguration configuration)
    {
        var key = ResolveKey(configuration);
        if (key.Length == 0)
            throw new InvalidOperationException(
                $"The instance key is not configured. Set {ServiceNames.ConfigKeys.InstanceKey} (or Parameters:{ServiceNames.Parameters.InstanceKey}).");
        if (key.Length < MinimumLength)
            throw new InvalidOperationException(
                $"The instance key is {key.Length} characters; it must be at least {MinimumLength}. Set {ServiceNames.ConfigKeys.InstanceKey} to a long random value, e.g. `openssl rand -hex 32`.");
        return key;
    }

    /// <summary>
    /// Returns a stable identifier for the configured instance key suitable for an audit trail, or
    /// null when no instance key is configured. Two different keys yield two different values.
    /// </summary>
    /// <remarks>
    /// What <see cref="Resolve"/> returns is exactly what a caller presents in the
    /// <see cref="ServiceNames.Headers.InstanceKey"/> header, so the digest is itself bearer
    /// material and must never be recorded. See <see cref="AuditFingerprint"/> for what is
    /// recorded instead.
    /// </remarks>
    public static string? ResolveFingerprint(IConfiguration configuration) =>
        AuditFingerprint.Of(AuditFingerprint.InstanceKeyDomain, Resolve(configuration));
}
