using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertTrackerNextChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "next_change_at",
                table: "alert_tracker_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "next_change_glucose",
                table: "alert_tracker_state",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "next_change_known",
                table: "alert_tracker_state",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "next_change_at",
                table: "alert_tracker_state");

            migrationBuilder.DropColumn(
                name: "next_change_glucose",
                table: "alert_tracker_state");

            migrationBuilder.DropColumn(
                name: "next_change_known",
                table: "alert_tracker_state");
        }
    }
}
