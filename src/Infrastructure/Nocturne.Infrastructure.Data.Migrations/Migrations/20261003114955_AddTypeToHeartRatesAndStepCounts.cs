using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeToHeartRatesAndStepCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "type",
                table: "step_counts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "type",
                table: "heart_rates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "type",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "type",
                table: "heart_rates");
        }
    }
}
