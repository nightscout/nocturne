using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAapsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bolus_calculator_result",
                table: "treatments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "duration_in_milliseconds",
                table: "treatments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "end_id",
                table: "treatments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_basal_insulin",
                table: "treatments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_read_only",
                table: "treatments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_valid",
                table: "treatments",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_customized_name",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "original_duration",
                table: "treatments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "original_end",
                table: "treatments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "original_percentage",
                table: "treatments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "original_profile_name",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "original_timeshift",
                table: "treatments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "pump_id",
                table: "treatments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pump_serial",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pump_type",
                table: "treatments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "app",
                table: "entries",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_read_only",
                table: "entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_valid",
                table: "entries",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "units",
                table: "entries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_treatments_sys_updated_at",
                table: "treatments",
                column: "sys_updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_devicestatus_sys_updated_at",
                table: "devicestatus",
                column: "sys_updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_entries_sys_updated_at",
                table: "entries",
                column: "sys_updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_treatments_sys_updated_at",
                table: "treatments");

            migrationBuilder.DropIndex(
                name: "ix_devicestatus_sys_updated_at",
                table: "devicestatus");

            migrationBuilder.DropIndex(
                name: "ix_entries_sys_updated_at",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "bolus_calculator_result",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "duration_in_milliseconds",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "end_id",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "is_basal_insulin",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "is_read_only",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "is_valid",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_customized_name",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_duration",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_end",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_percentage",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_profile_name",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "original_timeshift",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "pump_id",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "pump_serial",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "pump_type",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "app",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "is_read_only",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "is_valid",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "units",
                table: "entries");
        }
    }
}
