using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the deleted-row legacy-id indexes, all through <see cref="ConcurrentIndexBuilder"/>. See
    /// there for why CONCURRENTLY, and for what an interrupted build leaves behind. Why they exist is
    /// at their declaration in <c>ModelConventions.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddDeletedLegacyIdIndexes : Migration
    {
        /// <summary><c>NocturneDbContext.DeletedLegacyIdLookupEntities</c>.</summary>
        private static readonly string[] Tables =
        [
            "uploader_snapshots",
            "temp_basals",
            "sensor_glucose",
            "pump_snapshots",
            "profile_activations",
            "meter_glucose",
            "device_events",
            "carb_intakes",
            "calibrations",
            "boluses",
            "bolus_calculations",
            "bg_checks",
            "aps_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_legacy_id_deleted",
                    $"ON {table} (tenant_id, legacy_id) WHERE legacy_id IS NOT NULL AND deleted_at IS NOT NULL");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_legacy_id_deleted");
            }
        }
    }
}
