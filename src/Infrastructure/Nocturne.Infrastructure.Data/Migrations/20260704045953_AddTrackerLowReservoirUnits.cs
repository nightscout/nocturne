using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerLowReservoirUnits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "low_reservoir_units",
                table: "tracker_definitions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "low_reservoir_urgency",
                table: "tracker_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "low_reservoir_units",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "low_reservoir_urgency",
                table: "tracker_definitions");
        }
    }
}
