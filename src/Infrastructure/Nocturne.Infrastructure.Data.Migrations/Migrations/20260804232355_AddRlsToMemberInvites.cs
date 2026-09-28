using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRlsToMemberInvites : Migration
    {
        /// <summary>
        /// Enrols <c>member_invites</c> in the RLS regime now that <c>MemberInviteEntity</c>
        /// implements <c>ITenantScoped</c>. The table holds bearer credentials that grant
        /// membership of one tenant, and until now its only tenant bound was the LINQ predicate
        /// each call site wrote for itself.
        ///
        /// No schema change accompanies this: the tenant_id column, its index and its cascading
        /// foreign key already exist. The startup share-RLS reconciler adds the RESTRICTIVE
        /// share_category_read policy — the table is unclassified, so it stays hidden from public
        /// shares — but a restrictive policy alone denies every row. The permissive
        /// tenant_isolation policy created here is what lets a tenant read its own invites.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULLIF + missing_ok keeps the policy expression safe to evaluate when the GUC is
            // unset, matching every other tenant-scoped table.
            migrationBuilder.Sql("ALTER TABLE member_invites ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE member_invites FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON member_invites;
                CREATE POLICY tenant_isolation ON member_invites
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON member_invites;");
            migrationBuilder.Sql("ALTER TABLE member_invites NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE member_invites DISABLE ROW LEVEL SECURITY;");
        }
    }
}
