using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the unfiltered tenant timestamp indexes the entry and device status deleted-inclusive window
    /// reads need, and lifts the <c>deleted_at</c> filter off the device status extras one, all through
    /// <see cref="ConcurrentIndexBuilder"/>. See there for why CONCURRENTLY, and for what an
    /// interrupted build leaves behind. Why they exist, and why the sort-list tables carry the
    /// <c>id</c> column, is at their declaration in <c>ModelConventions.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddEntryAndDeviceStatusDeletedInclusiveWindowIndexes : Migration
    {
        private static readonly string[] Tables =
            ["meter_glucose", "calibrations", "aps_snapshots", "pump_snapshots", "uploader_snapshots"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_device_status_extras_tenant_timestamp");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_device_status_extras_tenant_timestamp",
                "ON device_status_extras (tenant_id, timestamp DESC)");

            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_timestamp_id_any",
                    $"ON {table} (tenant_id, timestamp DESC, id DESC)");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_timestamp_id_any");
            }

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_device_status_extras_tenant_timestamp");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_device_status_extras_tenant_timestamp",
                "ON device_status_extras (tenant_id, timestamp DESC) WHERE deleted_at IS NULL");
        }
    }
}
