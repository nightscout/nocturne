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
    private const string FingerprintDomain = "nocturne/instance-key-audit-fingerprint/";
    private const int FingerprintLength = 16;

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
    /// Returns a stable identifier for the configured instance key suitable for an audit trail, or
    /// null when no instance key is configured. Two different keys yield two different values.
    /// </summary>
    /// <remarks>
    /// What <see cref="Resolve"/> returns is exactly what a caller presents in the
    /// <see cref="ServiceNames.Headers.InstanceKey"/> header, so the digest is itself bearer
    /// material and must never be recorded. This identifier is a second, domain-separated SHA-256
    /// over that digest: it cannot be replayed as the header, and recovering the key or its digest
    /// from it means inverting SHA-256.
    /// </remarks>
    public static string? ResolveFingerprint(IConfiguration configuration)
    {
        var digest = Resolve(configuration);
        return digest.Length == 0
            ? null
            : HashUtils.Sha256Hex(FingerprintDomain + digest)[..FingerprintLength];
    }
}
