using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackerEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tracker_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    category = table.Column<int>(type: "integer", nullable: false),
                    icon = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    trigger_event_types = table.Column<string>(type: "jsonb", nullable: false),
                    trigger_notes_contains = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    lifespan_hours = table.Column<int>(type: "integer", nullable: true),
                    info_hours = table.Column<int>(type: "integer", nullable: true),
                    warn_hours = table.Column<int>(type: "integer", nullable: true),
                    hazard_hours = table.Column<int>(type: "integer", nullable: true),
                    urgent_hours = table.Column<int>(type: "integer", nullable: true),
                    is_favorite = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tracker_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    start_treatment_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    complete_treatment_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    start_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completion_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    completion_reason = table.Column<int>(type: "integer", nullable: true),
                    last_acked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ack_snooze_mins = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_instances_tracker_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "tracker_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tracker_presets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_start_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_presets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tracker_presets_tracker_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "tracker_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_definitions_created_at",
                table: "tracker_definitions",
                column: "created_at");

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

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_completed_at",
                table: "tracker_instances",
                column: "completed_at",
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_definition_id",
                table: "tracker_instances",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_started_at",
                table: "tracker_instances",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_user_completed",
                table: "tracker_instances",
                columns: new[] { "user_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tracker_instances_user_id",
                table: "tracker_instances",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_presets_definition_id",
                table: "tracker_presets",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_tracker_presets_user_id",
                table: "tracker_presets",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tracker_instances");

            migrationBuilder.DropTable(
                name: "tracker_presets");

            migrationBuilder.DropTable(
                name: "tracker_definitions");
        }
    }
}
