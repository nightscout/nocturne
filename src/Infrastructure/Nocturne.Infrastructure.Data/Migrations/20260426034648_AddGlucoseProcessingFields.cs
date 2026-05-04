using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGlucoseProcessingFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "glucose_processing",
                table: "sensor_glucose",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "smoothed_mgdl",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "unsmoothed_mgdl",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "glucose_processing",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "smoothed_mgdl",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "unsmoothed_mgdl",
                table: "sensor_glucose");
        }
    }
}
