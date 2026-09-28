using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveCarbIntakeRedundantFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "absorption_time",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "fat",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "food_type",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "protein",
                table: "carb_intakes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "absorption_time",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "fat",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "food_type",
                table: "carb_intakes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "protein",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);
        }
    }
}
