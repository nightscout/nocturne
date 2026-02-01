using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "note_attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_attachments_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_checklist_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_completed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_checklist_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_checklist_items_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_state_span_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_span_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_state_span_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_state_span_links_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_state_span_links_state_spans_state_span_id",
                        column: x => x.state_span_id,
                        principalTable: "state_spans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_tracker_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tracker_definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_tracker_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_tracker_links_notes_note_id",
                        column: x => x.note_id,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_tracker_links_tracker_definitions_tracker_definition_id",
                        column: x => x.tracker_definition_id,
                        principalTable: "tracker_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "note_tracker_thresholds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_tracker_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hours_offset = table.Column<decimal>(type: "numeric", nullable: false),
                    urgency = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_tracker_thresholds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_tracker_thresholds_note_tracker_links_note_tracker_lin~",
                        column: x => x.note_tracker_link_id,
                        principalTable: "note_tracker_links",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_note_attachments_note_id",
                table: "note_attachments",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_checklist_items_note_id",
                table: "note_checklist_items",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_checklist_items_note_sort",
                table: "note_checklist_items",
                columns: new[] { "note_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_note_state_span_links_note_id",
                table: "note_state_span_links",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_state_span_links_state_span_id",
                table: "note_state_span_links",
                column: "state_span_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_tracker_links_note_id",
                table: "note_tracker_links",
                column: "note_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_tracker_links_tracker_definition_id",
                table: "note_tracker_links",
                column: "tracker_definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_note_tracker_thresholds_note_tracker_link_id",
                table: "note_tracker_thresholds",
                column: "note_tracker_link_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_category",
                table: "notes",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_notes_created_at",
                table: "notes",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_notes_is_archived",
                table: "notes",
                column: "is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_notes_occurred_at",
                table: "notes",
                column: "occurred_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_notes_user_archived",
                table: "notes",
                columns: new[] { "user_id", "is_archived" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_user_category",
                table: "notes",
                columns: new[] { "user_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ix_notes_user_id",
                table: "notes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notes_user_occurred_at",
                table: "notes",
                columns: new[] { "user_id", "occurred_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_attachments");

            migrationBuilder.DropTable(
                name: "note_checklist_items");

            migrationBuilder.DropTable(
                name: "note_state_span_links");

            migrationBuilder.DropTable(
                name: "note_tracker_thresholds");

            migrationBuilder.DropTable(
                name: "note_tracker_links");

            migrationBuilder.DropTable(
                name: "notes");
        }
    }
}
