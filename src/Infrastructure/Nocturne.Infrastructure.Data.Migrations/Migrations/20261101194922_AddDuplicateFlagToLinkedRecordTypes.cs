using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds <c>is_duplicate</c> to every table deduplication links
    /// (<see cref="Entities.IDuplicateFlagged"/>) and sets it on each row whose link is not its group's
    /// primary, a tenant and a page of links at a time, committing each page so no transaction spans a
    /// tenant's history.
    /// </summary>
    /// <remarks>
    /// EF writes the history row only once the backfill finishes, so a process killed during it runs the
    /// whole migration again on the next boot. Every step is re-runnable for that: a column is added
    /// only where it is missing, and the backfill sets only flags still false.
    /// </remarks>
    public partial class AddDuplicateFlagToLinkedRecordTypes : Migration
    {
        /// <summary>Each table and the <c>linked_records.record_type</c> its rows are linked under.</summary>
        internal static readonly (string Table, string RecordType)[] Tables =
        [
            ("sensor_glucose", "sensorglucose"),
            ("state_spans", "statespan"),
            ("temp_basals", "tempbasal"),
            ("boluses", "bolus"),
            ("carb_intakes", "carbintake"),
            ("bg_checks", "bgcheck"),
            ("device_events", "deviceevent"),
            ("notes", "note"),
            ("bolus_calculations", "boluscalculation"),
        ];

        internal const int BackfillPageSize = 20_000;

        /// <summary>
        /// Pages each tenant's non-primary links of each type on <c>record_id</c>, which
        /// <c>ix_linked_records_tenant_type_not_primary</c> serves, and flags the page's rows. The
        /// tenant GUC is local to a transaction, so it is set again after every <c>COMMIT</c>.
        /// </summary>
        internal static string BackfillSql { get; } = $$"""
            DO $$
            DECLARE
                t RECORD;
                tbl RECORD;
                after_id uuid;
                ids uuid[];
            BEGIN
                FOR t IN SELECT id FROM tenants ORDER BY id LOOP
                    FOR tbl IN SELECT * FROM (VALUES {{string.Join(", ", Tables.Select(t => $"('{t.Table}', '{t.RecordType}')"))}}) AS v(name, record_type) LOOP
                        after_id := '00000000-0000-0000-0000-000000000000';
                        LOOP
                            PERFORM set_config('app.current_tenant_id', t.id::text, true);

                            SELECT array_agg(record_id ORDER BY record_id) INTO ids
                            FROM (
                                SELECT record_id FROM linked_records
                                WHERE tenant_id = t.id AND record_type = tbl.record_type AND NOT is_primary
                                  AND record_id > after_id
                                ORDER BY record_id
                                LIMIT {{BackfillPageSize}}) page;
                            EXIT WHEN ids IS NULL;

                            EXECUTE format(
                                'UPDATE %I SET is_duplicate = true WHERE tenant_id = $1 AND id = ANY($2) AND NOT is_duplicate',
                                tbl.name)
                            USING t.id, ids;

                            after_id := ids[array_length(ids, 1)];
                            COMMIT;
                        END LOOP;
                    END LOOP;
                END LOOP;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in Tables)
            {
                migrationBuilder.Sql(
                    $"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS is_duplicate boolean NOT NULL DEFAULT false;");
            }

            migrationBuilder.Sql(BackfillSql, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, _) in Tables)
                migrationBuilder.DropColumn(name: "is_duplicate", table: table);
        }
    }
}
