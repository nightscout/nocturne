using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Replaces the EF-conventional indexes on the mostly-null <c>SET NULL</c> foreign keys of the
    /// high-volume record tables with partial ones over the non-null rows, all through
    /// <see cref="ConcurrentIndexBuilder"/> — see there for why CONCURRENTLY. Each replacement is
    /// built before its predecessor is dropped, so the referential action always has an index.
    /// </summary>
    public partial class PartialSparseForeignKeyIndexes : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.SparseForeignKeys</c>.</summary>
        private static readonly (string Table, string Column)[] SparseForeignKeys =
        [
            ("aps_snapshots", "device_id"),
            ("aps_snapshots", "patient_device_id"),
            ("pump_snapshots", "device_id"),
            ("pump_snapshots", "patient_device_id"),
            ("uploader_snapshots", "device_id"),
            ("temp_basals", "device_id"),
            ("temp_basals", "patient_device_id"),
            ("temp_basals", "aps_snapshot_id"),
            ("boluses", "device_id"),
            ("boluses", "patient_device_id"),
            ("boluses", "aps_snapshot_id"),
            ("boluses", "bolus_calculation_id"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in SparseForeignKeys)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_{column}",
                    $"ON {table} ({column}) WHERE {column} IS NOT NULL");

                ConcurrentIndexBuilder.Drop(migrationBuilder, $"\"IX_{table}_{column}\"");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in SparseForeignKeys)
            {
                ConcurrentIndexBuilder.Build(migrationBuilder, $"\"IX_{table}_{column}\"", $"ON {table} ({column})");
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_{column}");
            }
        }
    }
}
