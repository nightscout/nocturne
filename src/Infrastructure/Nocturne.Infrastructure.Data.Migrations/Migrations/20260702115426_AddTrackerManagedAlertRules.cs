using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerManagedAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "alert_rule_id",
                table: "tracker_notification_thresholds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "managed_by",
                table: "alert_rules",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_notification_thresholds_alert_rule_id",
                table: "tracker_notification_thresholds",
                column: "alert_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_managed_by",
                table: "alert_rules",
                column: "managed_by",
                filter: "managed_by IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_tracker_notification_thresholds_alert_rules_alert_rule_id",
                table: "tracker_notification_thresholds",
                column: "alert_rule_id",
                principalTable: "alert_rules",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tracker_notification_thresholds_alert_rules_alert_rule_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropIndex(
                name: "IX_tracker_notification_thresholds_alert_rule_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropIndex(
                name: "ix_alert_rules_managed_by",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "alert_rule_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "managed_by",
                table: "alert_rules");
        }
    }
}
