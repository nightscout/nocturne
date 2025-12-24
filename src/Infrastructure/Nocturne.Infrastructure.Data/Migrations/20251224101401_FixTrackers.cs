using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixTrackers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "hazard_hours",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "info_hours",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "urgent_hours",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "warn_hours",
                table: "tracker_definitions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "hazard_hours",
                table: "tracker_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "info_hours",
                table: "tracker_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "urgent_hours",
                table: "tracker_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "warn_hours",
                table: "tracker_definitions",
                type: "integer",
                nullable: true);
        }
    }
}
