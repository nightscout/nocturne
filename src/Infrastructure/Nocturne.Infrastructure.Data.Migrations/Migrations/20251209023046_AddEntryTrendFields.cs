using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEntryTrendFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_calibration",
                table: "entries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "trend",
                table: "entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "trend_rate",
                table: "entries",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_calibration",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "trend",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "trend_rate",
                table: "entries");
        }
    }
}
