using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameNotePrimaryKeyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Id",
                table: "notes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "note_tracker_thresholds",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "note_tracker_links",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "note_state_span_links",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "note_checklist_items",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "note_attachments",
                newName: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "id",
                table: "notes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "note_tracker_thresholds",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "note_tracker_links",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "note_state_span_links",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "note_checklist_items",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "note_attachments",
                newName: "Id");
        }
    }
}
