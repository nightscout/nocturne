using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRedundantV4Columns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "mmol",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "trend",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "mmol",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "mgdl",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "mmol",
                table: "bg_checks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "mmol",
                table: "sensor_glucose",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "trend",
                table: "sensor_glucose",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "mmol",
                table: "meter_glucose",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "mgdl",
                table: "bg_checks",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "mmol",
                table: "bg_checks",
                type: "double precision",
                nullable: true);
        }
    }
}
