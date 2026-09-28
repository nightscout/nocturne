using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Nocturne.API.Authorization;

/// <summary>
/// Decides whether the <c>.DevOnly</c> controllers (seed-tenant, dev login, snapshots) are
/// registered: always in Development, and otherwise only when the operator sets
/// <see cref="EnableVariable"/> to <c>true</c>.
/// </summary>
/// <remarks>
/// The opt-in exists for the end-to-end suite (<c>e2e/</c>), which runs the production image in
/// Production so that what it tests is what ships, yet has to seed tenants and mint sessions
/// without a passkey ceremony. Those endpoints are unauthenticated and issue owner sessions for
/// any tenant, so the switch is an explicit environment variable read on its own: nothing in the
/// shipped appsettings sets it, and anything but a literal <c>true</c> leaves it off.
/// </remarks>
public static class DevOnlyEndpoints
{
    /// <summary>Environment variable that enables the dev-only controllers outside Development.</summary>
    public const string EnableVariable = "NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS";

    /// <summary>Whether the dev-only controllers are registered for this host.</summary>
    public static bool AreEnabled(IHostEnvironment environment, IConfiguration configuration) =>
        environment.IsDevelopment() || IsOptedIn(configuration);

    /// <summary>Whether <see cref="EnableVariable"/> is set to <c>true</c>.</summary>
    public static bool IsOptedIn(IConfiguration configuration) =>
        string.Equals(configuration[EnableVariable]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
