using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "battery_low_threshold",
                table: "notification_preferences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "calibration_reminder_hours",
                table: "notification_preferences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "data_gap_warning_minutes",
                table: "notification_preferences",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "push_enabled",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "sensor_expiration_warning_hours",
                table: "notification_preferences",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "battery_low_threshold",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "calibration_reminder_hours",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "data_gap_warning_minutes",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "push_enabled",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "sensor_expiration_warning_hours",
                table: "notification_preferences");
        }
    }
}
