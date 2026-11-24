namespace Nocturne.Core.Contracts.Auth;

/// <summary>
/// The details key under which an <c>auth_audit_log</c> row names whom its change belongs to, and the
/// values it takes. Deleting a subject clears the details of every row it acted on or is the target of,
/// so a row of a change that belongs to no tenant says <see cref="Global"/> and carries no tenant id:
/// once its details are gone, no tenant's member history can read it.
/// </summary>
public static class AuthAuditKind
{
    public const string Key = "kind";

    /// <summary>A change to a role, or a role assignment, that holds across every tenant.</summary>
    public const string Global = "global";

    /// <summary>A change to one tenant's member, role or access.</summary>
    public const string Tenant = "tenant";

    /// <summary>A change to a team.</summary>
    public const string Team = "team";

    /// <summary>A team's access to a site given or stopped, which the site's member history reads.</summary>
    public const string TeamAccess = "team_access";
}
