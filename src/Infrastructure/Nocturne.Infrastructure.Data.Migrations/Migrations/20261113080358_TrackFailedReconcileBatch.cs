using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackFailedReconcileBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "failed_batch_attempts",
                table: "dedup_reconcile_state",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "failed_batch_cursor_created_at",
                table: "dedup_reconcile_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "failed_batch_cursor_id",
                table: "dedup_reconcile_state",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "failed_batch_attempts",
                table: "dedup_reconcile_state");

            migrationBuilder.DropColumn(
                name: "failed_batch_cursor_created_at",
                table: "dedup_reconcile_state");

            migrationBuilder.DropColumn(
                name: "failed_batch_cursor_id",
                table: "dedup_reconcile_state");
        }
    }
}
