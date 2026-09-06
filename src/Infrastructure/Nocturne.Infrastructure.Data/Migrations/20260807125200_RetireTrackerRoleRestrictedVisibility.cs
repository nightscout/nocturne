using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetireTrackerRoleRestrictedVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TrackerVisibility.RoleRestricted (2) was settable through the API but never had a
            // view rule, so such a tracker was visible only to its owner and admins. It is now
            // rejected on create and update; convert the rows that already carry it to
            // Private (1), which is what they behaved as, so no row is left holding a value the
            // API will not accept back on the next save.
            // visibility is an integer column (see AddTrackerVisibility).
            // tracker_definitions is tenant-scoped under FORCE ROW LEVEL SECURITY, so the tenant
            // context must be set per tenant or the UPDATE matches zero rows.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE tracker_definitions
                        SET visibility = 1
                        WHERE visibility = 2;
                    END LOOP;
                END $$;
                """);

            // required_roles was added alongside the visibility column to hold the role names for
            // RoleRestricted. Nothing ever read or wrote it: no DTO, mapper, service or UI field.
            migrationBuilder.DropColumn(
                name: "required_roles",
                table: "tracker_definitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The visibility conversion is not reversed: a Private tracker cannot be told apart
            // from one that was RoleRestricted before the conversion.
            migrationBuilder.AddColumn<string>(
                name: "required_roles",
                table: "tracker_definitions",
                type: "jsonb",
                nullable: true);
        }
    }
}
