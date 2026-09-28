using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the user-tombstone legacy-id indexes, all through
    /// <see cref="ConcurrentIndexBuilder"/> — see there for why CONCURRENTLY, and for what an
    /// interrupted build leaves behind. Why they exist is at their declaration in
    /// <c>NocturneDbContext.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddLegacyIdTombstoneIndexes : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.V4LegacyIdRecordEntities</c>.</summary>
        private static readonly string[] LegacyIdTables =
        [
            "aps_snapshots",
            "basal_injections",
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
            "pump_snapshots",
            "sensitivity_schedules",
            "sensor_glucose",
            "target_range_schedules",
            "temp_basals",
            "therapy_settings",
            "uploader_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in LegacyIdTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_legacy_id_user_deleted",
                    $"ON {table} (tenant_id, legacy_id) WHERE legacy_id IS NOT NULL AND deleted_by_user");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in LegacyIdTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_legacy_id_user_deleted");
            }
        }
    }
}
