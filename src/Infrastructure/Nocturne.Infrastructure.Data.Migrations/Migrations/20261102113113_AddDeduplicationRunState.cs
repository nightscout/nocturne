using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeduplicationRunState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "run",
                table: "dedup_reconcile_state",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "run_cancel_requested_at",
                table: "dedup_reconcile_state",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "run",
                table: "dedup_reconcile_state");

            migrationBuilder.DropColumn(
                name: "run_cancel_requested_at",
                table: "dedup_reconcile_state");
        }
    }
}
