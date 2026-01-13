using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixTrackerNotificationThresholdsForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // First, delete any orphaned notification thresholds that reference non-existent definitions
            migrationBuilder.Sql(@"
                DELETE FROM tracker_notification_thresholds
                WHERE tracker_definition_id NOT IN (SELECT ""Id"" FROM tracker_definitions)
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_tracker_notification_thresholds_tracker_definitions_Definit~",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropIndex(
                name: "IX_tracker_notification_thresholds_DefinitionId",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "DefinitionId",
                table: "tracker_notification_thresholds");

            migrationBuilder.AddForeignKey(
                name: "FK_tracker_notification_thresholds_tracker_definitions_tracker~",
                table: "tracker_notification_thresholds",
                column: "tracker_definition_id",
                principalTable: "tracker_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tracker_notification_thresholds_tracker_definitions_tracker~",
                table: "tracker_notification_thresholds");

            migrationBuilder.AddColumn<Guid>(
                name: "DefinitionId",
                table: "tracker_notification_thresholds",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracker_notification_thresholds_DefinitionId",
                table: "tracker_notification_thresholds",
                column: "DefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_tracker_notification_thresholds_tracker_definitions_Definit~",
                table: "tracker_notification_thresholds",
                column: "DefinitionId",
                principalTable: "tracker_definitions",
                principalColumn: "Id");
        }
    }
}
