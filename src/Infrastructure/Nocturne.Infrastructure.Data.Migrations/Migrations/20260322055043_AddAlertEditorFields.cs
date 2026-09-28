using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertEditorFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                defaultValue: false);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "quiet_hours_start",
                table: "tenants",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_configuration",
                table: "alert_rules",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "severity",
                table: "alert_rules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "normal");

            migrationBuilder.AddColumn<int>(
                name: "snooze_count",
                table: "alert_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "snoozed_until",
                table: "alert_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alert_custom_sounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    mime_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    file_size = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_custom_sounds", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_custom_sounds");

            migrationBuilder.DropColumn(
                name: "quiet_hours_end",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quiet_hours_override_critical",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "quiet_hours_start",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "client_configuration",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "severity",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "snooze_count",
                table: "alert_instances");

            migrationBuilder.DropColumn(
                name: "snoozed_until",
                table: "alert_instances");
        }
    }
}
