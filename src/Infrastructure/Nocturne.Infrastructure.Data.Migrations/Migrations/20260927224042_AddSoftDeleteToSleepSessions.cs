using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Makes sleep sessions soft-deletable, so a user's delete leaves the tombstone that keeps a
    /// connector resync from re-creating the session. Adds columns only: existing rows stay live.
    /// </summary>
    public partial class AddSoftDeleteToSleepSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "sleep_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "sleep_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "sleep_sessions");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "sleep_sessions");
        }
    }
}
