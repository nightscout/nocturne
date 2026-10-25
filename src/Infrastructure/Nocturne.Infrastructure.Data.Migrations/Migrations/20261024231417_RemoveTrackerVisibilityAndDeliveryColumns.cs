using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTrackerVisibilityAndDeliveryColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tracker_presets_user_id",
                table: "tracker_presets");

            migrationBuilder.DropIndex(
                name: "ix_tracker_instances_user_completed",
                table: "tracker_instances");

            migrationBuilder.DropIndex(
                name: "ix_tracker_instances_user_id",
                table: "tracker_instances");

            migrationBuilder.DropIndex(
                name: "ix_tracker_definitions_is_favorite",
                table: "tracker_definitions");

            migrationBuilder.DropIndex(
                name: "ix_tracker_definitions_user_category",
                table: "tracker_definitions");

            migrationBuilder.DropIndex(
                name: "ix_tracker_definitions_user_id",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "audio_enabled",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "audio_sound",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "push_enabled",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "respect_quiet_hours",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "vibrate_enabled",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "tracker_definitions");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "tracker_presets",
                newName: "created_by");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "tracker_instances",
                newName: "created_by");

            migrationBuilder.RenameColumn(
                name: "user_id",
                table: "tracker_definitions",
                newName: "created_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "created_by",
                table: "tracker_presets",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "created_by",
                table: "tracker_instances",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "created_by",
                table: "tracker_definitions",
                newName: "user_id");

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

            migrationBuilder.AddColumn<bool>(
                name: "push_enabled",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "respect_quiet_hours",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "vibrate_enabled",
                table: "tracker_notification_thresholds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "visibility",
                table: "tracker_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "ix_tracker_presets_user_id",
                table: "tracker_presets",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_user_completed",
                table: "tracker_instances",
                columns: new[] { "user_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_user_id",
                table: "tracker_instances",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_definitions_is_favorite",
                table: "tracker_definitions",
                column: "is_favorite");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_definitions_user_category",
                table: "tracker_definitions",
                columns: new[] { "user_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_definitions_user_id",
                table: "tracker_definitions",
                column: "user_id");
        }
    }
}
