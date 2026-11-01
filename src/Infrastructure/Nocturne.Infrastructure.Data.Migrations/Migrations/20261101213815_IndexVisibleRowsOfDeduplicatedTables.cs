using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Narrows each deduplicated table's list index to the rows reads show (<c>NOT is_duplicate</c>),
    /// and gives bolus calculations and temp basals the list index they lacked. See
    /// <c>NocturneDbContext</c> for why. A replacement is built under a new name before the old index
    /// is dropped: <see cref="ConcurrentIndexBuilder"/> skips a name that exists, so rebuilding in
    /// place would leave the old definition behind on a re-run.
    /// </summary>
    public partial class IndexVisibleRowsOfDeduplicatedTables : Migration
    {
        /// <summary>The tables with a <c>(tenant_id, timestamp, id)</c> list index today.</summary>
        internal static readonly string[] Replaced =
            ["sensor_glucose", "boluses", "carb_intakes", "bg_checks", "notes", "device_events"];

        /// <summary>The tables whose lists had no tenant-leading sort index.</summary>
        internal static readonly (string Table, string Timestamp)[] Added =
            [("bolus_calculations", "\"timestamp\""), ("temp_basals", "start_timestamp")];

        private const string Visible = "deleted_at IS NULL AND NOT is_duplicate";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Replaced)
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_visible_timestamp_id",
                    $"ON {table} (tenant_id, \"timestamp\" DESC, id DESC) WHERE {Visible}");

            foreach (var (table, timestamp) in Added)
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_visible_timestamp_id",
                    $"ON {table} (tenant_id, {timestamp} DESC, id DESC) WHERE {Visible}");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_visible_start_id",
                $"ON state_spans (tenant_id, start_timestamp DESC, id DESC) WHERE {Visible}");

            foreach (var table in Replaced)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_timestamp_id");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_start_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Replaced)
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_timestamp_id",
                    $"ON {table} (tenant_id, \"timestamp\" DESC, id DESC) WHERE deleted_at IS NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_start_id",
                "ON state_spans (tenant_id, start_timestamp DESC, id DESC) WHERE deleted_at IS NULL");

            foreach (var table in Replaced)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_visible_timestamp_id");

            foreach (var (table, _) in Added)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_visible_timestamp_id");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_visible_start_id");
        }
    }
}
