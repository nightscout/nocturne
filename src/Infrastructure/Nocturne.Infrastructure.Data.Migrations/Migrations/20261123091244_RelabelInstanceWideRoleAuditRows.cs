using Microsoft.EntityFrameworkCore.Migrations;
using Nocturne.Core.Contracts.Auth;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RelabelInstanceWideRoleAuditRows : Migration
    {
        /// <summary>
        /// Says <see cref="AuthAuditKind.Global"/> on every role row whose details name no kind and takes
        /// its tenant id off, and takes the tenant id off a global role's permission change, which already
        /// says it. The member writer always names a kind, so a role row without one is an instance-wide
        /// subject role. A row whose details were cleared already cannot be told from a member's, and is
        /// left as it is. A second run changes nothing.
        /// </summary>
        internal const string RelabelSql = """
            UPDATE auth_audit_log
            SET details = details || '{"kind":"global"}'::jsonb, tenant_id = NULL
            WHERE event_type IN ('role_assigned', 'role_removed')
              AND details IS NOT NULL
              AND NOT (details ? 'kind');

            UPDATE auth_audit_log
            SET tenant_id = NULL
            WHERE event_type = 'role_permissions_changed'
              AND details ->> 'kind' = 'global'
              AND tenant_id IS NOT NULL;
            """;

        /// <inheritdoc />
        /// <remarks>
        /// No per-tenant loop: <c>auth_audit_log</c> is not tenant-scoped and carries no row level
        /// security policy.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RelabelSql);
        }

        /// <inheritdoc />
        /// <remarks>The relabel is not reversed: the tenant id it removed is not recorded.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
