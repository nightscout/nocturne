using Microsoft.AspNetCore.Authorization;

namespace Nocturne.API.Tests.Authorization;

/// <summary>
/// V4 controllers exempt from both <see cref="ReadEndpointScopeEnforcementTests"/> and
/// <see cref="V4WriteScopeGatingTests"/> for the same reason, so each is named once.
/// </summary>
internal static class V4ScopeGuardExemptions
{
    /// <summary>Platform operator surface; the class carries [Authorize(Roles = "platform_admin")].</summary>
    public const string PlatformAdminRole = "platform-admin role, not a data scope";

    /// <summary>Service-to-service surface; the class carries [RequireInstanceKeyAuth].</summary>
    public const string InstanceKey = "instance-key service credential, not a data scope";

    public static readonly IReadOnlyDictionary<string, string> Controllers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessRequestController"] = PlatformAdminRole,
            ["ConnectorAdminController"] = PlatformAdminRole,
            ["DemoAdminController"] = PlatformAdminRole,
            ["OidcProviderAdminController"] = PlatformAdminRole,
            ["PlatformSettingsController"] = PlatformAdminRole,
            ["TenantController"] = PlatformAdminRole,
            ["TenantDirectGrantController"] = PlatformAdminRole,
            ["ChatIdentityDirectoryController"] = InstanceKey,
        };

    /// <summary>Whether <paramref name="attributes"/> carry the mechanism <paramref name="reason"/> names.</summary>
    public static bool Carries(object[] attributes, string reason) => reason switch
    {
        PlatformAdminRole => attributes.OfType<AuthorizeAttribute>()
            .Any(a => a.Roles?.Contains("platform_admin", StringComparison.Ordinal) == true),
        InstanceKey => attributes.Any(a => a.GetType().Name == "RequireInstanceKeyAuthAttribute"),
        _ => false,
    };
}
