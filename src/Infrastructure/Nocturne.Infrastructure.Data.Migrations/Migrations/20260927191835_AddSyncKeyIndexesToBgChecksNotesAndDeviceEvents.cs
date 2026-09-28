using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Backs the sync key of bg_checks, notes and device_events with the partial unique index every
    /// other <c>NocturneDbContext.SyncDedupedEntities</c> table carries, now that their creates
    /// upsert on it. Built through <see cref="ConcurrentIndexBuilder"/>, whose remarks cover why
    /// CONCURRENTLY and what an interrupted build leaves behind.
    /// </summary>
    public partial class AddSyncKeyIndexesToBgChecksNotesAndDeviceEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Until now nothing stopped these tables holding one live sync key twice, and a unique
            // index built over such a pair fails the migration. Soft-delete every loser (newest
            // insert wins); the delete carries no auth context, so the key stays re-importable. A
            // null data source never collides in the index, so those rows are left alone. FORCE
            // ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE bg_checks
                        SET deleted_at = now()
                        WHERE id IN (
                            SELECT id
                            FROM (
                                SELECT id,
                                       row_number() OVER (
                                           PARTITION BY tenant_id, data_source, sync_identifier
                                           ORDER BY sys_created_at DESC, id DESC) AS rn
                                FROM bg_checks
                                WHERE sync_identifier IS NOT NULL
                                  AND data_source IS NOT NULL
                                  AND deleted_at IS NULL
                            ) ranked
                            WHERE ranked.rn > 1);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE notes
                        SET deleted_at = now()
                        WHERE id IN (
                            SELECT id
                            FROM (
                                SELECT id,
                                       row_number() OVER (
                                           PARTITION BY tenant_id, data_source, sync_identifier
                                           ORDER BY sys_created_at DESC, id DESC) AS rn
                                FROM notes
                                WHERE sync_identifier IS NOT NULL
                                  AND data_source IS NOT NULL
                                  AND deleted_at IS NULL
                            ) ranked
                            WHERE ranked.rn > 1);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        UPDATE device_events
                        SET deleted_at = now()
                        WHERE id IN (
                            SELECT id
                            FROM (
                                SELECT id,
                                       row_number() OVER (
                                           PARTITION BY tenant_id, data_source, sync_identifier
                                           ORDER BY sys_created_at DESC, id DESC) AS rn
                                FROM device_events
                                WHERE sync_identifier IS NOT NULL
                                  AND data_source IS NOT NULL
                                  AND deleted_at IS NULL
                            ) ranked
                            WHERE ranked.rn > 1);
                    END LOOP;
                END $$;
                """);

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_bg_checks_tenant_source_sync_id",
                "ON bg_checks (tenant_id, data_source, sync_identifier) "
                + "WHERE sync_identifier IS NOT NULL AND deleted_at IS NULL");

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_notes_tenant_source_sync_id",
                "ON notes (tenant_id, data_source, sync_identifier) "
                + "WHERE sync_identifier IS NOT NULL AND deleted_at IS NULL");

            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_device_events_tenant_source_sync_id",
                "ON device_events (tenant_id, data_source, sync_identifier) "
                + "WHERE sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_bg_checks_tenant_source_sync_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_notes_tenant_source_sync_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_device_events_tenant_source_sync_id");
        }
    }
}
