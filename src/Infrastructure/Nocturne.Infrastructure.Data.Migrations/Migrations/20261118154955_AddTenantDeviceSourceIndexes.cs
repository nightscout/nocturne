using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes each counted record table by tenant, device and data source, for a source's stats. See
    /// <c>ModelConventions.ConfigureSharedRecordIndexes</c> for why.
    /// </summary>
    public partial class AddTenantDeviceSourceIndexes : Migration
    {
        /// <summary>The record tables a source's stats count.</summary>
        private static readonly string[] Tables =
        [
            "sensor_glucose", "meter_glucose", "boluses", "carb_intakes", "bolus_calculations", "notes",
            "device_events", "bg_checks", "temp_basals", "aps_snapshots", "basal_injections",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_device_source",
                    $"ON {table} (tenant_id, device, data_source) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_device_source");
        }
    }
}
