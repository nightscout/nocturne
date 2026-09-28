using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceStatusSnapshotSyncIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app",
                table: "uploader_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "uploader_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app",
                table: "pump_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "pump_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app",
                table: "aps_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "aps_snapshots",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_tenant_source_sync_id",
                table: "uploader_snapshots",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_tenant_source_sync_id",
                table: "pump_snapshots",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_tenant_source_sync_id",
                table: "aps_snapshots",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_tenant_source_sync_id",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_tenant_source_sync_id",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_tenant_source_sync_id",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "app",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "app",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "app",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "aps_snapshots");
        }
    }
}
