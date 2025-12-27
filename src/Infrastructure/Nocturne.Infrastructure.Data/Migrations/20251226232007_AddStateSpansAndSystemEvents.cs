using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStateSpansAndSystemEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "blood_glucose_input",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "blood_glucose_input_source",
                table: "treatments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "calculation_type",
                table: "treatments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_delivered",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_on_board",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_programmed",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_recommendation_for_carbs",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_recommendation_for_correction",
                table: "treatments",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "state_spans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    start_mills = table.Column<long>(type: "bigint", nullable: false),
                    end_mills = table.Column<long>(type: "bigint", nullable: true),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    original_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_state_spans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    original_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_category",
                table: "state_spans",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_category_start",
                table: "state_spans",
                columns: new[] { "category", "start_mills" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_end_mills",
                table: "state_spans",
                column: "end_mills",
                filter: "end_mills IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_original_id",
                table: "state_spans",
                column: "original_id");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_source",
                table: "state_spans",
                column: "source");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_start_mills",
                table: "state_spans",
                column: "start_mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_system_events_category",
                table: "system_events",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_category_mills",
                table: "system_events",
                columns: new[] { "category", "mills" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_system_events_event_type",
                table: "system_events",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_mills",
                table: "system_events",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_system_events_original_id",
                table: "system_events",
                column: "original_id");

            migrationBuilder.CreateIndex(
                name: "ix_system_events_source",
                table: "system_events",
                column: "source");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "state_spans");

            migrationBuilder.DropTable(
                name: "system_events");

            migrationBuilder.DropColumn(
                name: "blood_glucose_input",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "blood_glucose_input_source",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "calculation_type",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "insulin_delivered",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "insulin_on_board",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "insulin_programmed",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "insulin_recommendation_for_carbs",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "insulin_recommendation_for_correction",
                table: "treatments");
        }
    }
}
