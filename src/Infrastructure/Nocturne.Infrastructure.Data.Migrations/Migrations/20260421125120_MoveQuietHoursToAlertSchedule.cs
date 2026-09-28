using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveQuietHoursToAlertSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quiet_hours_end",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quiet_hours_override_critical",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quiet_hours_start",
                table: "tenants");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_hours_end",
                table: "alert_schedules",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "quiet_hours_override_critical",
                table: "alert_schedules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_hours_start",
                table: "alert_schedules",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "quiet_hours_end",
                table: "alert_schedules");

            migrationBuilder.DropColumn(
                name: "quiet_hours_override_critical",
                table: "alert_schedules");

            migrationBuilder.DropColumn(
                name: "quiet_hours_start",
                table: "alert_schedules");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_hours_end",
                table: "tenants",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "quiet_hours_override_critical",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_hours_start",
                table: "tenants",
                type: "time without time zone",
                nullable: true);
        }
    }
}
