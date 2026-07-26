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
        var instanceKey =
            configuration[$"Parameters:{ServiceNames.Parameters.InstanceKey}"]
            ?? configuration[ServiceNames.ConfigKeys.InstanceKey]
            ?? "";

        return !string.IsNullOrEmpty(instanceKey) ? HashUtils.Sha256Hex(instanceKey) : "";
    }
}
