using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the history-page indexes the v3 entries, profile and food history reads page through, all
    /// through <see cref="ConcurrentIndexBuilder"/>. Why they exist is at their declaration in
    /// <c>NocturneDbContext</c>.
    /// </summary>
    public partial class AddEntryFoodProfileHistoryIndexes : Migration
    {
        /// <summary>The tables this migration adds to <c>NocturneDbContext.V4HistoryPagedEntities</c>.</summary>
        private static readonly string[] HistoryPagedTables =
        [
            "sensor_glucose",
            "meter_glucose",
            "calibrations",
            "therapy_settings",
            "basal_schedules",
            "carb_ratio_schedules",
            "sensitivity_schedules",
            "target_range_schedules",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_sys_updated_at",
                    $"ON {table} (tenant_id, sys_updated_at, id) WHERE deleted_at IS NULL");
            }

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_foods_tenant_sys_updated_at",
                "ON foods (tenant_id, sys_updated_at, id)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_foods_tenant_sys_updated_at");

            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_sys_updated_at");
            }
        }
    }
}
