using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlignSnapshotDataSourceLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These three tables are the only ones whose data_source was ever unbounded, so a plain
            // narrowing cast would abort the chain — and crash-loop the API — on any instance that
            // accepted a longer value. USING left(...) makes the cast total; MigrationBuilder
            // cannot express it. The truncation can also collide two rows under the filtered unique
            // ix_<table>_tenant_source_sync_id index (values differing only past char 256), which the
            // rewrite's index rebuild would then refuse — so the losers of each post-truncation key
            // are soft-deleted first, newest row wins, in the idiom of
            // 20260818102940_AddTenantScopedLegacyIdIndexesToSnapshots (FORCE ROW LEVEL SECURITY
            // requires the per-tenant set_config loop).
            foreach (var table in new[] { "uploader_snapshots", "pump_snapshots", "aps_snapshots" })
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
                                               PARTITION BY tenant_id, left(data_source, 256), sync_identifier
                                               ORDER BY sys_created_at DESC, id DESC) AS rn
                                    FROM {table}
                                    WHERE sync_identifier IS NOT NULL AND data_source IS NOT NULL AND deleted_at IS NULL
                                ) ranked
                                WHERE ranked.rn > 1);
                        END LOOP;
                    END $$;
                    """);

                migrationBuilder.Sql($"""
                    ALTER TABLE "{table}"
                        ALTER COLUMN "data_source" TYPE character varying(256)
                        USING left("data_source", 256);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "data_source",
                table: "uploader_snapshots",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "data_source",
                table: "pump_snapshots",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "data_source",
                table: "aps_snapshots",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);
        }
    }
}
