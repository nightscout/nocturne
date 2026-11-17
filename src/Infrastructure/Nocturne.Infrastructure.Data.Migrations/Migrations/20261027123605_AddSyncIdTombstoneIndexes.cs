using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the user-tombstone sync-key indexes, all through
    /// <see cref="ConcurrentIndexBuilder"/>. See there for why CONCURRENTLY, and for what an
    /// interrupted build leaves behind. Why they exist is at their declaration in
    /// <c>NocturneDbContext.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddSyncIdTombstoneIndexes : Migration
    {
        /// <summary>Mirrors <c>NocturneDbContext.SyncDedupedEntities</c>.</summary>
        private static readonly string[] SyncKeyTables =
        [
            "step_counts",
            "heart_rates",
            "body_weights",
            "sensor_glucose",
            "boluses",
            "basal_injections",
            "carb_intakes",
            "temp_basals",
            "profile_activations",
            "bg_checks",
            "notes",
            "device_events",
            "aps_snapshots",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in SyncKeyTables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_source_sync_id_user_deleted",
                    $"ON {table} (tenant_id, data_source, sync_identifier) WHERE sync_identifier IS NOT NULL AND deleted_by_user");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in SyncKeyTables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_source_sync_id_user_deleted");
            }
        }
    }
}
