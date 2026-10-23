using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Loop's automatic boluses were stored as manual boluses carrying <c>automatic = true</c>. The
    /// tenant is named in each statement as well as the GUC, so a migrator that bypasses RLS still
    /// updates each row once. <c>sys_updated_at</c> is left alone: the legacy wire shape of these rows
    /// does not change, so a v3 history client has nothing to re-fetch.
    /// </remarks>
    public partial class ReclassifyAutomaticBolusesAsAlgorithm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE boluses
                        SET bolus_kind = 'Algorithm'
                        WHERE tenant_id = t.id
                          AND automatic = true
                          AND bolus_kind = 'Manual';
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The kind an automatic bolus was stored with before this migration is not recorded,
            // and rows written since carry Algorithm by the decomposer's rule.
        }
    }
}
