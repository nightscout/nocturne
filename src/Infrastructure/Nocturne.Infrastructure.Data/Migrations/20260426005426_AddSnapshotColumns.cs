using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "bolus_iob",
                table: "pump_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "iob",
                table: "pump_snapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "aid_version",
                table: "aps_snapshots",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "loop_json",
                table: "aps_snapshots",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bolus_iob",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "iob",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "aid_version",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "loop_json",
                table: "aps_snapshots");
        }
    }
}
