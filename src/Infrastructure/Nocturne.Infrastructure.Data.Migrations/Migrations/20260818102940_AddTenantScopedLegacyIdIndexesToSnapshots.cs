using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantScopedLegacyIdIndexesToSnapshots : Migration
    {
        private static readonly string[] SnapshotTables =
            ["aps_snapshots", "pump_snapshots", "uploader_snapshots"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The unique index below fails on any legacy id these tables already hold twice, and a
            // failed migration crash-loops the API. Soft-delete every loser (newest insert wins) so
            // the survivor keeps serving reads; the delete carries no auth context, so
            // SoftDeleteDedupExtensions.GetBlockingLegacyIdsAsync leaves the id re-importable.
            // FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            foreach (var table in SnapshotTables)
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
                                               PARTITION BY tenant_id, legacy_id
                                               ORDER BY sys_created_at DESC, id DESC) AS rn
                                    FROM {table}
                                    WHERE legacy_id IS NOT NULL AND deleted_at IS NULL
                                ) ranked
                                WHERE ranked.rn > 1);
                        END LOOP;
                    END $$;
                    """);
            }

            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_legacy_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_legacy_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_legacy_id",
                table: "aps_snapshots");

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_correlation_id",
                table: "uploader_snapshots",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_tenant_legacy_id",
                table: "uploader_snapshots",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_correlation_id",
                table: "pump_snapshots",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_tenant_legacy_id",
                table: "pump_snapshots",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_correlation_id",
                table: "aps_snapshots",
                column: "correlation_id");

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_tenant_legacy_id",
                table: "aps_snapshots",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_correlation_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_tenant_legacy_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_correlation_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_tenant_legacy_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_correlation_id",
                table: "aps_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_tenant_legacy_id",
                table: "aps_snapshots");

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_legacy_id",
                table: "uploader_snapshots",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_legacy_id",
                table: "pump_snapshots",
                column: "legacy_id");

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_legacy_id",
                table: "aps_snapshots",
                column: "legacy_id");
        }
    }
}
