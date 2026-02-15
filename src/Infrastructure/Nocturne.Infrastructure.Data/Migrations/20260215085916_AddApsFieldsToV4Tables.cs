using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApsFieldsToV4Tables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "notes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "device_events",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "carb_time",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "carb_intakes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "insulin_type",
                table: "boluses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_basal_insulin",
                table: "boluses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "pump_id",
                table: "boluses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pump_serial",
                table: "boluses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pump_type",
                table: "boluses",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "boluses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "unabsorbed",
                table: "boluses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "entered_insulin",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_programmed",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "insulin_recommendation_for_carbs",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "pre_bolus",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "split_ext",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "split_now",
                table: "bolus_calculations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "bg_checks",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "carb_time",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "insulin_type",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "is_basal_insulin",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "pump_id",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "pump_serial",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "pump_type",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "unabsorbed",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "entered_insulin",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "insulin_programmed",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "insulin_recommendation_for_carbs",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "pre_bolus",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "split_ext",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "split_now",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "bg_checks");
        }
    }
}
