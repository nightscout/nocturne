using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerNotificationThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracker_notification_thresholds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracker_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    urgency = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_notification_thresholds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_notification_thresholds_tracker_definitions_Definit~",
                        column: x => x.DefinitionId,
                        principalTable: "tracker_definitions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_notification_thresholds_def_order",
                table: "tracker_notification_thresholds",
                columns: new[] { "tracker_definition_id", "display_order" });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_notification_thresholds_definition_id",
                table: "tracker_notification_thresholds",
                column: "tracker_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_tracker_notification_thresholds_DefinitionId",
                table: "tracker_notification_thresholds",
                column: "DefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracker_notification_thresholds");
        }
    }
}
