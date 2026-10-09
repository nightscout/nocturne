using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets the v3 history reads return soft-deleted rows: the history-page indexes lose their
    /// <c>deleted_at IS NULL</c> predicate. Each wide index is built through
    /// <see cref="ConcurrentIndexBuilder"/> before the partial one it replaces is dropped, so history
    /// reads stay indexed throughout. The migration holds only that concurrent, idempotent work, so an
    /// interrupted run can be repeated; the transactional part is <see cref="AddFoodSoftDelete"/>.
    /// </summary>
    public partial class ReportDeletionsInV3History : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.V4HistoryPagedEntities</c>.</summary>
        private static readonly string[] HistoryPagedTables =
        [
            "aps_snapshots",
            "basal_schedules",
            "bg_checks",
            "bolus_calculations",
            "boluses",
            "calibrations",
            "carb_intakes",
            "carb_ratio_schedules",
            "device_events",
            "meter_glucose",
            "notes",
            "sensitivity_schedules",
            "sensor_glucose",
            "target_range_schedules",
            "temp_basals",
            "therapy_settings",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_history",
                    $"ON {table} (tenant_id, sys_updated_at, id)");
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_sys_updated_at");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in HistoryPagedTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_sys_updated_at",
                    $"ON {table} (tenant_id, sys_updated_at, id) WHERE deleted_at IS NULL");
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_history");
            }
        }
    }
}
