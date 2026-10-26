using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RepairThirtyTwoHexEntryLegacyIds : Migration
    {
        private static readonly string[] EntryTables = ["sensor_glucose", "meter_glucose", "calibrations"];

        // The dashless UUID v7 an older POST /api/v1/entries gave an entry uploaded without an _id.
        // MongoObjectId.Coerce serves it as its first 24 chars, which no lookup resolves back to it.
        private const string AssignedUuid = "^[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}$";

        private const string ObjectId = "^[0-9a-f]{24}$";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The served id can already belong to a live row of the tenant, or to two assigned ids at
            // once. The row already stored under it wins, since a read by that id returns it today;
            // among assigned ids alone the newest insert wins. The wire shows every row of a group
            // under one id, so a loser was never reachable apart from its winner. A loser is
            // soft-deleted without auth context, which leaves its id re-importable
            // (SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync).
            // FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            foreach (var table in EntryTables)
            {
                migrationBuilder.Sql($"""
                    DO $$
                    DECLARE
                        t RECORD;
                    BEGIN
                        FOR t IN SELECT id FROM tenants LOOP
                            PERFORM set_config('app.current_tenant_id', t.id::text, true);

                            UPDATE {table}
                            SET deleted_at = now()
                            WHERE id IN (
                                SELECT id
                                FROM (
                                    SELECT id,
                                           row_number() OVER (
                                               PARTITION BY tenant_id, left(legacy_id, 24)
                                               ORDER BY legacy_id ~ '{AssignedUuid}', sys_created_at DESC, id DESC) AS rn
                                    FROM {table}
                                    WHERE deleted_at IS NULL
                                      AND (legacy_id ~ '{AssignedUuid}' OR legacy_id ~ '{ObjectId}')
                                ) ranked
                                WHERE ranked.rn > 1);

                            UPDATE {table}
                            SET legacy_id = left(legacy_id, 24)
                            WHERE legacy_id ~ '{AssignedUuid}';
                        END LOOP;
                    END $$;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
