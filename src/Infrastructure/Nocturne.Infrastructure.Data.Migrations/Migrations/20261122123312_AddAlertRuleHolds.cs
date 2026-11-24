using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertRuleHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "held_facts",
                table: "alert_tracker_state",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "held_since",
                table: "alert_tracker_state",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "held_since",
                table: "alert_excursions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "held_facts",
                table: "alert_tracker_state");

            migrationBuilder.DropColumn(
                name: "held_since",
                table: "alert_tracker_state");

            migrationBuilder.DropColumn(
                name: "held_since",
                table: "alert_excursions");
        }
    }
}
