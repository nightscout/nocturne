using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDedupPruneCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "prune_cursor_created_at",
                table: "dedup_reconcile_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "prune_cursor_id",
                table: "dedup_reconcile_state",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "prune_cursor_reset_at",
                table: "dedup_reconcile_state",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "prune_cursor_created_at",
                table: "dedup_reconcile_state");

            migrationBuilder.DropColumn(
                name: "prune_cursor_id",
                table: "dedup_reconcile_state");

            migrationBuilder.DropColumn(
                name: "prune_cursor_reset_at",
                table: "dedup_reconcile_state");
        }
    }
}
