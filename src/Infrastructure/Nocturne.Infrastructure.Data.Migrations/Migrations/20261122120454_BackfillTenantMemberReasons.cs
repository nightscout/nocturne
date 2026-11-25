using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillTenantMemberReasons : Migration
    {
        /// <summary>
        /// Gives every membership with no reason one direct reason carrying the row's access, with the
        /// member's id as its id, and gives each of those reasons the member's role links, with each
        /// link's id. A membership that already has a reason is skipped, and a second run inserts
        /// nothing.
        /// </summary>
        internal const string BackfillSql = """
            INSERT INTO tenant_member_reasons
                (id, tenant_member_id, kind, source_id, direct_permissions, limit_to_24_hours,
                 access_ends_at, created_from_invite_id, sys_created_at, sys_updated_at)
            SELECT m.id, m.id, 'direct', NULL, m.direct_permissions, m.limit_to_24_hours,
                   m.access_ends_at, m.created_from_invite_id, m.sys_created_at, m.sys_updated_at
            FROM tenant_members m
            WHERE NOT EXISTS (SELECT 1 FROM tenant_member_reasons r WHERE r.tenant_member_id = m.id);

            INSERT INTO tenant_member_reason_roles (id, tenant_member_reason_id, tenant_role_id, sys_created_at)
            SELECT mr.id, r.id, mr.tenant_role_id, mr.sys_created_at
            FROM tenant_member_roles mr
            JOIN tenant_member_reasons r ON r.id = mr.tenant_member_id AND r.kind = 'direct'
            WHERE NOT EXISTS (SELECT 1 FROM tenant_member_reason_roles x WHERE x.id = mr.id);
            """;

        /// <inheritdoc />
        /// <remarks>
        /// No per-tenant loop: <c>tenant_members</c> carries only the <c>subject_pin</c> policy, which
        /// admits every row on the migrator's connection, as <see cref="AddLoginCodeIdToRefreshTokens"/>
        /// says of <c>refresh_tokens</c>, and the role links and reasons carry no policy.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        /// <remarks>The reasons are dropped with their tables by <see cref="AddTenantMemberReasons"/>.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
