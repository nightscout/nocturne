using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Truncates to the millisecond every record time a V4 record table already stores with a part
    /// below it, as the save now stores each one (<see cref="Nocturne.Core.Models.RecordTime.AtMillisecond"/>). A tenant's
    /// stale rows are found once and written a page at a time by id, each page committed.
    /// </summary>
    /// <remarks>
    /// No unique index or key holds a record time, so a truncation cannot make two rows collide.
    /// The row stamps are left as they are: a legacy reader sees a record's time in milliseconds, which
    /// the truncation does not change, so the row has nothing new to send a history client. EF writes
    /// the history row only once the walk finishes, so a process killed during it runs it again on the
    /// next boot, and the walk finds only rows still stale.
    /// </remarks>
    public partial class TruncateRecordTimesToTheMillisecond : Migration
    {
        /// <summary>Each V4 record table and the columns of its record times when this migration was written.</summary>
        internal static readonly (string Table, string[] Columns)[] Tables =
        [
            ("sensor_glucose", ["timestamp"]),
            ("meter_glucose", ["timestamp"]),
            ("calibrations", ["timestamp"]),
            ("boluses", ["timestamp"]),
            ("carb_intakes", ["timestamp"]),
            ("bolus_calculations", ["timestamp"]),
            ("device_events", ["timestamp"]),
            ("bg_checks", ["timestamp"]),
            ("notes", ["timestamp"]),
            ("aps_snapshots", ["timestamp", "predicted_start_timestamp"]),
            ("pump_snapshots", ["timestamp"]),
            ("uploader_snapshots", ["timestamp"]),
            ("sensor_state_reports", ["timestamp"]),
            ("temp_basals", ["start_timestamp", "end_timestamp"]),
            ("basal_injections", ["timestamp"]),
            ("state_spans", ["start_timestamp", "end_timestamp"]),
            ("profile_activations", ["start_timestamp", "end_timestamp", "ended_at"]),
            ("heart_rates", ["timestamp"]),
            ("step_counts", ["timestamp"]),
            ("therapy_settings", ["timestamp"]),
            ("basal_schedules", ["timestamp"]),
            ("carb_ratio_schedules", ["timestamp"]),
            ("sensitivity_schedules", ["timestamp"]),
            ("target_range_schedules", ["timestamp"]),
        ];

        internal const int PageSize = 20_000;

        /// <summary>
        /// For each tenant and table, collects the ids of the stale rows off the tenant's own rows, then
        /// writes them <see cref="PageSize"/> at a time through the primary key. The tenant GUC is local
        /// to a transaction, so it is set again after every <c>COMMIT</c>.
        /// </summary>
        internal static string TruncateSql { get; } = $$"""
            DO $$
            DECLARE
                t RECORD;
                tbl RECORD;
                stale uuid[];
                i int;
            BEGIN
                FOR t IN SELECT id FROM tenants ORDER BY id LOOP
                    FOR tbl IN SELECT * FROM (VALUES {{string.Join(", ", Tables.Select(Row))}}) AS v(name, stale, truncated) LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);
                        EXECUTE format('SELECT array_agg(id ORDER BY id) FROM %I WHERE tenant_id = $1 AND (%s)', tbl.name, tbl.stale)
                        INTO stale
                        USING t.id;
                        CONTINUE WHEN stale IS NULL;

                        FOR i IN 1 .. array_length(stale, 1) BY {{PageSize}} LOOP
                            PERFORM set_config('app.current_tenant_id', t.id::text, true);
                            EXECUTE format(
                                'UPDATE %I SET %s WHERE tenant_id = $1 AND id = ANY($2) AND (%s)',
                                tbl.name, tbl.truncated, tbl.stale)
                            USING t.id, stale[i : i + {{PageSize - 1}}];
                            COMMIT;
                        END LOOP;
                    END LOOP;
                END LOOP;
            END $$;
            """;

        private static string Row((string Table, string[] Columns) table)
        {
            var stale = string.Join(" OR ", table.Columns.Select(c => $"\"{c}\" <> date_trunc(''milliseconds'', \"{c}\")"));
            var truncated = string.Join(", ", table.Columns.Select(c => $"\"{c}\" = date_trunc(''milliseconds'', \"{c}\")"));
            return $"('{table.Table}', '{stale}', '{truncated}')";
        }

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(TruncateSql, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
