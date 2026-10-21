using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestampNotesServedWithTheirRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Note written beside a bolus, carb intake, BG check, device event, temp basal or
            // calculation is now served as that record's notes, and v3 history tombstones its own id
            // whenever its row moves (V4ToLegacyProjectionService.AssembleAsync). A client that synced
            // the Note as a treatment of its own still holds it, so each such Note's row is moved once
            // to send it the tombstone and the record with its notes. FORCE ROW LEVEL SECURITY binds
            // the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE notes n
                        SET sys_updated_at = now()
                        WHERE n.deleted_at IS NULL
                          AND n.legacy_id IS NOT NULL
                          AND n.legacy_id IN (
                              SELECT legacy_id FROM boluses WHERE deleted_at IS NULL
                              UNION SELECT legacy_id FROM carb_intakes WHERE deleted_at IS NULL
                              UNION SELECT legacy_id FROM bg_checks WHERE deleted_at IS NULL
                              UNION SELECT legacy_id FROM device_events WHERE deleted_at IS NULL
                              UNION SELECT legacy_id FROM temp_basals WHERE deleted_at IS NULL
                              UNION SELECT legacy_id FROM bolus_calculations WHERE deleted_at IS NULL);
                    END LOOP;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
