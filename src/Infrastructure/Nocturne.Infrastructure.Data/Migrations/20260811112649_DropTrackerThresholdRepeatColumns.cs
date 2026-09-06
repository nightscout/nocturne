using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTrackerThresholdRepeatColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_repeats",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "repeat_interval_mins",
                table: "tracker_notification_thresholds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_repeats",
                table: "tracker_notification_thresholds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "repeat_interval_mins",
                table: "tracker_notification_thresholds",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
