namespace Nocturne.Infrastructure.Data.Tests.Repositories;

/// <summary>
/// SQL for tests that write a team reason straight to the tables, run as the migrator: a team reason's
/// source must name a team's access to the member's tenant, which no product path gives yet.
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
}
