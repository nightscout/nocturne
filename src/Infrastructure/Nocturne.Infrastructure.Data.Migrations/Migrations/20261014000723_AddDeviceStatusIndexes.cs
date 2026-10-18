using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives the pump, uploader and extras tables the history-page index the v3 device status history
    /// reads them through, and extras the newest-first tenant index the device status list walks
    /// beside the snapshots' own. Each table's plain tenant index then goes, as
    /// <see cref="DropRedundantLinkedRecordAndTenantIndexes"/> drops it from the other history-paged
    /// tables: ix_&lt;table&gt;_tenant_history leads with tenant_id unfiltered. Every statement goes
    /// through <see cref="ConcurrentIndexBuilder"/>, and the drops follow the builds, so tenant scans
    /// stay indexed throughout and an interrupted run can be repeated.
    /// </summary>
    public partial class AddDeviceStatusIndexes : Migration
    {
        private static readonly string[] Tables =
        [
            "device_status_extras",
            "pump_snapshots",
            "uploader_snapshots",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_history",
                    $"ON {table} (tenant_id, sys_updated_at, id)");
            }

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_device_status_extras_tenant_timestamp",
                "ON device_status_extras (tenant_id, timestamp DESC) WHERE deleted_at IS NULL");

            foreach (var table in Tables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"\"IX_{table}_tenant_id\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
                ConcurrentIndexBuilder.Build(migrationBuilder, $"\"IX_{table}_tenant_id\"", $"ON {table} (tenant_id)");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_device_status_extras_tenant_timestamp");

            foreach (var table in Tables)
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_history");
        }
    }
}
