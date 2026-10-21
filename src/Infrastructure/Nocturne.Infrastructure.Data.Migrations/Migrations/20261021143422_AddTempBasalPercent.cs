using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTempBasalPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<double>(
                name: "rate",
                table: "temp_basals",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddColumn<double>(
                name: "percent",
                table: "temp_basals",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "rate_from_schedule",
                table: "temp_basals",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "percent",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "rate_from_schedule",
                table: "temp_basals");

            // No backfill: a null rate is unknown, and 0 would read as a suspension. Restoring NOT NULL
            // fails while any row still holds one.
            migrationBuilder.AlterColumn<double>(
                name: "rate",
                table: "temp_basals",
                type: "double precision",
                nullable: false,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);
        }
    }
}
