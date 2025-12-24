using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerAlertConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "audio_enabled",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "audio_sound",
                table: "tracker_notification_thresholds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_repeats",
                table: "tracker_notification_thresholds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "push_enabled",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "repeat_interval_mins",
                table: "tracker_notification_thresholds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "respect_quiet_hours",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "vibrate_enabled",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audio_enabled",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "audio_sound",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "max_repeats",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "push_enabled",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "repeat_interval_mins",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "respect_quiet_hours",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "vibrate_enabled",
                table: "tracker_notification_thresholds");
        }
    }
}
