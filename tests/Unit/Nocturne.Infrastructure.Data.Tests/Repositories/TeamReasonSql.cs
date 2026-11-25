namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// SQL for tests that write a team reason straight to the tables, run as the migrator: a team reason's
/// source must name a team's access to the member's tenant, and a reason no approved staff row stands on
/// is removed by the startup check (<see cref="Nocturne.Infrastructure.Data.Repositories.TeamReasonChanges"/>).
/// </summary>
internal static class TeamReasonSql
{
    /// <summary>
    /// Creates the team <paramref name="team"/> named <paramref name="name"/> and its access
    /// <paramref name="access"/> to <paramref name="tenant"/>, leaving the connection pinned to the tenant.
    /// </summary>
    public static string Access(Guid tenant, Guid team, Guid access, string name) => $"""
        SELECT set_config('app.current_tenant_id', '{tenant}', false);
        INSERT INTO teams (id, name) VALUES ('{team}', '{name}');
        INSERT INTO team_tenant_access (id, team_id, tenant_id, limit_to_24_hours)
        VALUES ('{access}', '{team}', '{tenant}', false);
        """;

    /// <summary>
    /// <see cref="Access"/> with a fresh team named <paramref name="name"/>, for a test that needs only
    /// the access; returns the SQL and the access's id.
    /// </summary>
    public static (string Sql, Guid Access) NewAccess(Guid tenant, string name)
    {
        var access = Guid.CreateVersion7();
        return (Access(tenant, Guid.CreateVersion7(), access, name), access);
    }

    /// <summary>
    /// Makes <paramref name="subject"/> approved staff of the team that holds <paramref name="access"/>,
    /// giving <paramref name="role"/> through it when set, so the team reason the access gives them is the
    /// one the test writes and the startup check keeps it. The connection must be pinned to the access's
    /// tenant.
    /// </summary>
    public static string Staff(Guid access, Guid subject, Guid? role, bool limitTo24Hours, DateTime? endsAt) =>
        StaffOf(access, $"'{subject}'", role, limitTo24Hours, endsAt);

    /// <summary><see cref="Staff"/> for the subject of the membership <paramref name="member"/>.</summary>
    public static string StaffOfMember(Guid access, Guid member, Guid? role, bool limitTo24Hours, DateTime? endsAt) =>
        StaffOf(access, $"(SELECT subject_id FROM tenant_members WHERE id = '{member}')", role, limitTo24Hours, endsAt);

    private static string StaffOf(Guid access, string subject, Guid? role, bool limitTo24Hours, DateTime? endsAt)
    {
        var member = Guid.CreateVersion7();
        var ends = endsAt is { } end ? $"'{end:O}'" : "NULL";
        var roleSql = role is { } id ? $"""
            INSERT INTO team_tenant_access_roles (id, team_tenant_access_id, tenant_role_id)
            SELECT '{Guid.CreateVersion7()}', '{access}', '{id}'
            WHERE NOT EXISTS (SELECT 1 FROM team_tenant_access_roles WHERE team_tenant_access_id = '{access}' AND tenant_role_id = '{id}');
            """ : string.Empty;
        return $"""
            {roleSql}
            INSERT INTO team_members (id, team_id, subject_id, kind, limit_to_24_hours, ends_at)
            SELECT '{member}', team_id, {subject}, 'staff', {(limitTo24Hours ? "true" : "false")}, {ends}
            FROM team_tenant_access WHERE id = '{access}';
            INSERT INTO team_site_staff (id, team_tenant_access_id, team_member_id, status)
            VALUES ('{Guid.CreateVersion7()}', '{access}', '{member}', 'approved');
            """;
    }
}
