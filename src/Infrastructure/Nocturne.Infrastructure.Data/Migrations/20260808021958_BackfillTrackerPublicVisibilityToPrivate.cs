using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackfillTrackerPublicVisibilityToPrivate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The tracker visibility default flipped from Public (0) to Private (1): a tracker is
            // never made Public by omission, and the anonymous read path is closed, so existing
            // Public rows carry a value the tenant did not intend to expose. Convert every
            // remaining Public row to Private; an owner who wants a Public tracker opts back in.
            // visibility is an integer column (see AddTrackerVisibility); Public = 0, Private = 1.
            // Runs after RetireTrackerRoleRestrictedVisibility, so value 2 (RoleRestricted) is
            // already gone. tracker_definitions is tenant-scoped under FORCE ROW LEVEL SECURITY, so
            // the tenant context must be set per tenant or the UPDATE matches zero rows.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE tracker_definitions
                        SET visibility = 1
                        WHERE visibility = 0;
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The visibility conversion is not reversed: a Private tracker cannot be told apart
            // from one that was Public before the conversion.
        }
    }
}
