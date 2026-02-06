namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Translates between legacy Nightscout Shiro-style trie permissions and
/// the new OAuth 2.0 scope model. This enables backward compatibility:
/// requests using legacy api-secret or access tokens get translated to
/// equivalent scopes so controllers only need to check scopes.
/// </summary>
public static class ScopeTranslator
{
    /// <summary>
    /// Maps legacy trie permission strings to their equivalent OAuth scopes.
    /// Collapsing create/update into readwrite is intentional. The only lossy case:
    /// someone who had api:X:create but not api:X:delete gets readwrite (slightly more
    /// permissive but cannot delete since delete requires *).
    /// </summary>
    private static readonly Dictionary<string, string[]> TrieToScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Entries
        ["api:entries:read"] = [OAuthScopes.EntriesRead],
        ["api:entries:create"] = [OAuthScopes.EntriesReadWrite],
        ["api:entries:update"] = [OAuthScopes.EntriesReadWrite],
        ["api:entries:delete"] = [OAuthScopes.FullAccess],

        // Treatments
        ["api:treatments:read"] = [OAuthScopes.TreatmentsRead],
        ["api:treatments:create"] = [OAuthScopes.TreatmentsReadWrite],
        ["api:treatments:update"] = [OAuthScopes.TreatmentsReadWrite],
        ["api:treatments:delete"] = [OAuthScopes.FullAccess],

        // Device status
        ["api:devicestatus:read"] = [OAuthScopes.DeviceStatusRead],
        ["api:devicestatus:create"] = [OAuthScopes.DeviceStatusReadWrite],
        ["api:devicestatus:update"] = [OAuthScopes.DeviceStatusReadWrite],
        ["api:devicestatus:delete"] = [OAuthScopes.FullAccess],

        // Profile
        ["api:profile:read"] = [OAuthScopes.ProfileRead],
        ["api:profile:create"] = [OAuthScopes.ProfileReadWrite],
        ["api:profile:update"] = [OAuthScopes.ProfileReadWrite],
        ["api:profile:delete"] = [OAuthScopes.FullAccess],

        // Wildcard reads
        ["api:*:read"] = [
            OAuthScopes.EntriesRead,
            OAuthScopes.TreatmentsRead,
            OAuthScopes.DeviceStatusRead,
            OAuthScopes.ProfileRead,
            OAuthScopes.NotificationsRead,
            OAuthScopes.ReportsRead,
            OAuthScopes.IdentityRead,
        ],

        // Wildcard writes
        ["api:*:create"] = [
            OAuthScopes.EntriesReadWrite,
            OAuthScopes.TreatmentsReadWrite,
            OAuthScopes.DeviceStatusReadWrite,
            OAuthScopes.ProfileReadWrite,
            OAuthScopes.NotificationsReadWrite,
            OAuthScopes.SharingReadWrite,
        ],
        ["api:*:update"] = [
            OAuthScopes.EntriesReadWrite,
            OAuthScopes.TreatmentsReadWrite,
            OAuthScopes.DeviceStatusReadWrite,
            OAuthScopes.ProfileReadWrite,
            OAuthScopes.NotificationsReadWrite,
            OAuthScopes.SharingReadWrite,
        ],
        ["api:*:delete"] = [OAuthScopes.FullAccess],

        // Full wildcards
        ["api:*"] = [OAuthScopes.FullAccess],
        ["*"] = [OAuthScopes.FullAccess],

        // Named roles
        ["admin"] = [OAuthScopes.FullAccess],
        ["readable"] = [
            OAuthScopes.EntriesRead,
            OAuthScopes.TreatmentsRead,
            OAuthScopes.DeviceStatusRead,
            OAuthScopes.ProfileRead,
            OAuthScopes.NotificationsRead,
            OAuthScopes.ReportsRead,
            OAuthScopes.IdentityRead,
        ],
    };

    /// <summary>
    /// Translate a set of legacy Shiro-style permissions into OAuth scopes.
    /// This is used at the auth middleware level so controllers never see trie strings.
    /// </summary>
    /// <param name="permissions">Legacy permission strings from the PermissionTrie</param>
    /// <returns>Set of equivalent OAuth scopes</returns>
    public static IReadOnlySet<string> FromPermissions(IEnumerable<string> permissions)
    {
        var scopes = new HashSet<string>();

        foreach (var permission in permissions)
        {
            if (TrieToScopes.TryGetValue(permission, out var mapped))
            {
                foreach (var scope in mapped)
                {
                    scopes.Add(scope);
                }
            }
        }

        // If full access is granted, normalize to include everything
        if (scopes.Contains(OAuthScopes.FullAccess))
        {
            scopes.UnionWith(OAuthScopes.AllScopes);
        }

        return scopes;
    }

    /// <summary>
    /// Translate OAuth scopes back to legacy Shiro-style permissions.
    /// Used when legacy endpoints need to check permissions in the old format.
    /// </summary>
    /// <param name="scopes">OAuth scope strings</param>
    /// <returns>Set of equivalent legacy permission strings</returns>
    public static IReadOnlySet<string> ToPermissions(IEnumerable<string> scopes)
    {
        var permissions = new HashSet<string>();

        foreach (var scope in scopes)
        {
            switch (scope)
            {
                case OAuthScopes.FullAccess:
                    permissions.Add("*");
                    return permissions; // * covers everything

                case OAuthScopes.EntriesRead:
                    permissions.Add("api:entries:read");
                    break;
                case OAuthScopes.EntriesReadWrite:
                    permissions.Add("api:entries:read");
                    permissions.Add("api:entries:create");
                    permissions.Add("api:entries:update");
                    break;

                case OAuthScopes.TreatmentsRead:
                    permissions.Add("api:treatments:read");
                    break;
                case OAuthScopes.TreatmentsReadWrite:
                    permissions.Add("api:treatments:read");
                    permissions.Add("api:treatments:create");
                    permissions.Add("api:treatments:update");
                    break;

                case OAuthScopes.DeviceStatusRead:
                    permissions.Add("api:devicestatus:read");
                    break;
                case OAuthScopes.DeviceStatusReadWrite:
                    permissions.Add("api:devicestatus:read");
                    permissions.Add("api:devicestatus:create");
                    permissions.Add("api:devicestatus:update");
                    break;

                case OAuthScopes.ProfileRead:
                    permissions.Add("api:profile:read");
                    break;
                case OAuthScopes.ProfileReadWrite:
                    permissions.Add("api:profile:read");
                    permissions.Add("api:profile:create");
                    permissions.Add("api:profile:update");
                    break;

                case OAuthScopes.NotificationsRead:
                    permissions.Add("api:notifications:read");
                    break;
                case OAuthScopes.NotificationsReadWrite:
                    permissions.Add("api:notifications:read");
                    permissions.Add("api:notifications:create");
                    permissions.Add("api:notifications:update");
                    break;

                case OAuthScopes.ReportsRead:
                    permissions.Add("api:reports:read");
                    break;

                case OAuthScopes.IdentityRead:
                    permissions.Add("api:identity:read");
                    break;

                case OAuthScopes.SharingReadWrite:
                    permissions.Add("api:sharing:read");
                    permissions.Add("api:sharing:create");
                    permissions.Add("api:sharing:update");
                    break;
            }
        }

        return permissions;
    }
}
