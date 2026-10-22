using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoldNotesIntoTheirRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A Note written beside a bolus, carb intake, BG check, device event, temp basal or
            // calculation is served as that record's notes, and v3 history tombstones the Note's own
            // id whenever its row moves (V4ToLegacyProjectionService.AssembleAsync). A client that
            // synced the Note as a treatment of its own still holds it, so each such Note's row moves
            // once to send it that tombstone and the record with its notes, at most
            // NocturneDbContext.SystemTimestampGroupSize rows a millisecond, as HistoryPage requires.
            // The tenant filter on every table keeps a role that bypasses RLS to the pinned tenant.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        WITH held AS (
                            SELECT legacy_id FROM boluses WHERE tenant_id = t.id AND deleted_at IS NULL
                            UNION SELECT legacy_id FROM carb_intakes WHERE tenant_id = t.id AND deleted_at IS NULL
                            UNION SELECT legacy_id FROM bg_checks WHERE tenant_id = t.id AND deleted_at IS NULL
                            UNION SELECT legacy_id FROM device_events WHERE tenant_id = t.id AND deleted_at IS NULL
                            UNION SELECT legacy_id FROM temp_basals WHERE tenant_id = t.id AND deleted_at IS NULL
                            UNION SELECT legacy_id FROM bolus_calculations WHERE tenant_id = t.id AND deleted_at IS NULL),
                        ranked AS (
                            SELECT n.id, row_number() OVER (ORDER BY n.id) - 1 AS k
                            FROM notes n
                            WHERE n.tenant_id = t.id
                              AND n.deleted_at IS NULL
                              AND n.legacy_id IN (SELECT legacy_id FROM held WHERE legacy_id IS NOT NULL))
                        UPDATE notes n
                        SET sys_updated_at = now() + (ranked.k / 1000) * interval '1 millisecond'
                        FROM ranked
                        WHERE ranked.id = n.id
                          AND n.tenant_id = t.id;
                    END LOOP;
                END $$;
                """);

            ConcurrentIndexBuilder.Build(
                migrationBuilder, "ix_notes_tenant_legacy_id_any", "ON notes (tenant_id, legacy_id)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_notes_tenant_legacy_id_any");
        }
    }
}
