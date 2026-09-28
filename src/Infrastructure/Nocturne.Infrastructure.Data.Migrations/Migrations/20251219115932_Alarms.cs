using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Alarms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "client_configuration",
                table: "alert_rules",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cooldown_minutes",
                table: "alert_rules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "forecast_lead_time_minutes",
                table: "alert_rules",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "client_configuration",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "cooldown_minutes",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "forecast_lead_time_minutes",
                table: "alert_rules");
        }
    }
}
