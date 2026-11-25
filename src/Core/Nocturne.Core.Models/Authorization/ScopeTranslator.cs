namespace Nocturne.Core.Models.Authorization;

/// <summary>
/// Translates between legacy Nightscout Shiro-style trie permissions and
/// the new OAuth 2.0 scope model. This enables backward compatibility:
/// requests using legacy api-secret or access tokens get translated to
/// equivalent scopes so controllers only need to check scopes.
/// </summary>
/// <seealso cref="Scope"/>
/// <seealso cref="Scope"/>
/// <seealso cref="Role"/>
public static class ScopeTranslator
{
    private static readonly string[] ReadEverything =
    [
        Scope.GlucoseRead,
        Scope.TreatmentsRead,
        Scope.DevicesRead,
        Scope.TherapyRead,
        Scope.FoodRead,
        Scope.AlertsRead,
        Scope.ReportsRead,
        Scope.IdentityRead,
        Scope.HeartRateRead,
        Scope.StepCountRead,
        Scope.SleepRead,
    ];

    private static readonly string[] WriteEverything =
    [
        Scope.GlucoseReadWrite,
        Scope.TreatmentsReadWrite,
        Scope.DevicesReadWrite,
        Scope.TherapyReadWrite,
        Scope.FoodReadWrite,
        Scope.AlertsReadWrite,
        Scope.SharingReadWrite,
    ];

    /// <summary>
    /// One Shiro resource (<c>api:&lt;resource&gt;:&lt;verb&gt;</c>) and the scopes its verbs carry.
    /// <see cref="FromPermissions"/> and <see cref="ToPermissions"/> both read <see cref="Collections"/>,
    /// so a scope's Shiro form always translates back to it.
    /// </summary>
    /// <param name="Resource">The middle segment of the permission string.</param>
    /// <param name="Read">The scopes the read verb carries; empty when the category has no read tier.</param>
    /// <param name="ReadWrite">The scopes any write verb carries; empty when the category is read-only.</param>
    private sealed record Collection(string Resource, string[] Read, string[] ReadWrite);

    private static readonly string[] WriteVerbs = ["create", "update", "delete"];

    private const string NightscoutAck = "notifications:*:ack";

    /// <summary>
    /// Collapsing create/update/delete into the collection's readwrite scope is intentional:
    /// <see cref="Scope"/> makes readwrite the whole write authority over one category, so a
    /// subject holding any one write verb on a collection gets all three. Nightscout grants its own
    /// write verbs the same way: the seeded careportal role is "api:treatments:*".
    /// </summary>
    private static readonly Collection[] Collections =
    [
        new("entries", [Scope.GlucoseRead], [Scope.GlucoseReadWrite]),
        new("treatments", [Scope.TreatmentsRead], [Scope.TreatmentsReadWrite]),
        new("devicestatus", [Scope.DevicesRead], [Scope.DevicesReadWrite]),
        new("food", [Scope.FoodRead], [Scope.FoodReadWrite]),
        new("profile", [Scope.TherapyRead], [Scope.TherapyReadWrite]),

        // Nightscout has no resource for these. Each takes its own, because a scope that shared a
        // Shiro form with another would translate back as both.
        new("heartrate", [Scope.HeartRateRead], [Scope.HeartRateReadWrite]),
        new("stepcount", [Scope.StepCountRead], [Scope.StepCountReadWrite]),
        new("sleep", [Scope.SleepRead], [Scope.SleepReadWrite]),
        new("notifications", [Scope.AlertsRead], [Scope.AlertsReadWrite]),
        new("reports", [Scope.ReportsRead], []),
        new("identity", [Scope.IdentityRead], []),
        new("sharing", [], [Scope.SharingReadWrite]),
        new("connectors", [Scope.ConnectorsRead], [Scope.ConnectorsReadWrite]),

        // The legacy activity collection is the merged read plane over four Nocturne storages, so
        // it carries all three dedicated categories, and a grant holds it only when it holds all
        // three. StateSpan-backed activities read under treatments, which "api:treatments:read"
        // grants separately: mapping it here as well would let an activity-only permission read
        // /api/v1/treatments. The "readable" and "public" seed roles both grant "api:activity:read".
        new(
            "activity",
            [Scope.HeartRateRead, Scope.StepCountRead, Scope.SleepRead],
            [Scope.HeartRateReadWrite, Scope.StepCountReadWrite, Scope.SleepReadWrite]),
    ];

    /// <summary>
    /// The seeded roles a permission list may name outright. Nightscout finds a role by its exact
    /// name, so unlike the permission strings in <see cref="TrieToScopes"/> these match ordinally.
    /// </summary>
    private static readonly Dictionary<string, string[]> NamedRoleScopes = new(StringComparer.Ordinal)
    {
        [GlobalRoles.Admin] = [Scope.FullAccess],
        ["readable"] = ReadEverything,
    };

    private static readonly Dictionary<string, string[]> TrieToScopes = BuildTrieToScopes();

    private static Dictionary<string, string[]> BuildTrieToScopes()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // Nightscout's alarm ack permission, which silences an alarm and grants nothing else.
            [NightscoutAck] = [Scope.AlertsAcknowledge],
            ["*:*:ack"] = [Scope.AlertsAcknowledge],

            // "*:*:read" is how Nightscout's own seeded "readable" role spells it.
            ["api:*:read"] = ReadEverything,
            ["*:*:read"] = ReadEverything,

            ["api:*:create"] = WriteEverything,
            ["api:*:update"] = WriteEverything,
            ["api:*:delete"] = WriteEverything,

            ["api:*"] = [Scope.FullAccess],
            ["*"] = [Scope.FullAccess],
        };

        foreach (var collection in Collections)
        {
            if (collection.Read.Length > 0)
            {
                map[$"api:{collection.Resource}:read"] = collection.Read;
            }

            if (collection.ReadWrite.Length == 0)
            {
                continue;
            }

            // The verb wildcard is how Nightscout's own seeded roles are written.
            foreach (var verb in WriteVerbs.Append("*"))
            {
                map[$"api:{collection.Resource}:{verb}"] = collection.ReadWrite;
            }
        }

        return map;
    }

    /// <summary>
    /// Whether <paramref name="grantedScopes"/> read everything the legacy <c>*:*:read</c> grants.
    /// </summary>
    public static bool GrantsReadEverything(IReadOnlySet<string> grantedScopes) =>
        ReadEverything.All(required => Scope.Satisfies(grantedScopes, required));

    /// <summary>
    /// Whether <paramref name="grantedScopes"/> write everything the legacy wildcard write verbs grant.
    /// </summary>
    public static bool GrantsWriteEverything(IReadOnlySet<string> grantedScopes) =>
        WriteEverything.All(required => Scope.Satisfies(grantedScopes, required));

    /// <summary>
    /// Translate a set of legacy Shiro-style permissions into OAuth scopes.
    /// This is used at the auth middleware level so controllers never see trie strings.
    /// </summary>
    /// <param name="permissions">Legacy permission strings from the PermissionTrie</param>
    /// <returns>Set of equivalent OAuth scopes</returns>
    public static IReadOnlySet<string> FromPermissions(IEnumerable<string> permissions)
    {
        var scopes = permissions
            .SelectMany(permission =>
                NamedRoleScopes.TryGetValue(permission, out var role) ? role
                : TrieToScopes.TryGetValue(permission, out var mapped) ? mapped
                : [])
            .ToHashSet();

        // If full access is granted, normalize to include everything
        if (scopes.Contains(Scope.FullAccess))
        {
            scopes.UnionWith(Scope.AllScopes);
        }

        return scopes;
    }

    /// <summary>
    /// Translate OAuth scopes back to legacy Shiro-style permissions, for the trie the
    /// HasPermissions policy checks and for the permission lists a Nightscout client reads.
    /// </summary>
    /// <remarks>
    /// <see cref="FromPermissions"/> takes the result back to the same authority for every scope
    /// but <see cref="Scope.MemberPersonalScopes"/>, which have no Shiro form. They drive the
    /// holder's own client devices, which Nightscout has no permission for, and the HasPermissions
    /// policy admits any credential whose trie is not empty: a form for them would let a credential
    /// holding nothing else through it.
    /// </remarks>
    /// <param name="scopes">OAuth scope strings</param>
    /// <returns>Set of equivalent legacy permission strings</returns>
    public static IReadOnlySet<string> ToPermissions(IEnumerable<string> scopes)
    {
        var granted = scopes.ToHashSet(StringComparer.Ordinal);

        if (granted.Contains(Scope.FullAccess))
        {
            return new HashSet<string> { "*" };
        }

        // The tenant-administration atoms are enforced against granted scopes only, and
        // tenant.settings satisfies the connector scopes, so they are dropped here rather than
        // reaching the trie through the satisfaction checks below.
        granted.IntersectWith(Scope.ValidRequestScopes);

        var permissions = new HashSet<string>();

        foreach (var collection in Collections)
        {
            if (collection.Read.Length > 0 && collection.Read.All(read => Scope.Satisfies(granted, read)))
            {
                permissions.Add($"api:{collection.Resource}:read");
            }

            if (collection.ReadWrite.Length > 0 && collection.ReadWrite.All(granted.Contains))
            {
                permissions.UnionWith(WriteVerbs.Select(verb => $"api:{collection.Resource}:{verb}"));
            }
        }

        if (Scope.Satisfies(granted, Scope.AlertsAcknowledge))
        {
            permissions.Add(NightscoutAck);
        }

        return permissions;
    }
}
