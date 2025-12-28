using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "required_roles",
                table: "tracker_definitions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "visibility",
                table: "tracker_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "required_roles",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "tracker_definitions");
        }
    }
}
