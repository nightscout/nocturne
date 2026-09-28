using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSensorGlucoseRawFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "delta",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "filtered",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "unfiltered",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delta",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "filtered",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "unfiltered",
                table: "sensor_glucose");
        }
    }
}
