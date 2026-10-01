using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameColumnsToSnakeCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "NSCLIENT_ID",
                table: "treatments",
                newName: "nsclient_id");

            migrationBuilder.RenameColumn(
                name: "utcOffset",
                table: "treatments",
                newName: "utc_offset");

            migrationBuilder.RenameColumn(
                name: "transmitterId",
                table: "treatments",
                newName: "transmitter_id");

            migrationBuilder.RenameColumn(
                name: "targetTop",
                table: "treatments",
                newName: "target_top");

            migrationBuilder.RenameColumn(
                name: "targetBottom",
                table: "treatments",
                newName: "target_bottom");

            migrationBuilder.RenameColumn(
                name: "isAnnouncement",
                table: "treatments",
                newName: "is_announcement");

            migrationBuilder.RenameColumn(
                name: "eventType",
                table: "treatments",
                newName: "event_type");

            migrationBuilder.RenameColumn(
                name: "eventTime",
                table: "treatments",
                newName: "event_time");

            migrationBuilder.RenameColumn(
                name: "enteredBy",
                table: "treatments",
                newName: "entered_by");

            migrationBuilder.RenameColumn(
                name: "utcOffset",
                table: "entries",
                newName: "utc_offset");

            migrationBuilder.RenameColumn(
                name: "sysTime",
                table: "entries",
                newName: "sys_time");

            migrationBuilder.RenameColumn(
                name: "dateString",
                table: "entries",
                newName: "date_string");

            migrationBuilder.RenameColumn(
                name: "utcOffset",
                table: "devicestatus",
                newName: "utc_offset");

            migrationBuilder.RenameColumn(
                name: "radioAdapter",
                table: "devicestatus",
                newName: "radio_adapter");

            migrationBuilder.RenameColumn(
                name: "isCharging",
                table: "devicestatus",
                newName: "is_charging");

            migrationBuilder.RenameColumn(
                name: "insulinPen",
                table: "devicestatus",
                newName: "insulin_pen");

            migrationBuilder.RenameColumn(
                name: "utcOffset",
                table: "activities",
                newName: "utc_offset");

            migrationBuilder.RenameColumn(
                name: "enteredBy",
                table: "activities",
                newName: "entered_by");

            migrationBuilder.RenameColumn(
                name: "dateString",
                table: "activities",
                newName: "date_string");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "nsclient_id",
                table: "treatments",
                newName: "NSCLIENT_ID");

            migrationBuilder.RenameColumn(
                name: "utc_offset",
                table: "treatments",
                newName: "utcOffset");

            migrationBuilder.RenameColumn(
                name: "transmitter_id",
                table: "treatments",
                newName: "transmitterId");

            migrationBuilder.RenameColumn(
                name: "target_top",
                table: "treatments",
                newName: "targetTop");

            migrationBuilder.RenameColumn(
                name: "target_bottom",
                table: "treatments",
                newName: "targetBottom");

            migrationBuilder.RenameColumn(
                name: "is_announcement",
                table: "treatments",
                newName: "isAnnouncement");

            migrationBuilder.RenameColumn(
                name: "event_type",
                table: "treatments",
                newName: "eventType");

            migrationBuilder.RenameColumn(
                name: "event_time",
                table: "treatments",
                newName: "eventTime");

            migrationBuilder.RenameColumn(
                name: "entered_by",
                table: "treatments",
                newName: "enteredBy");

            migrationBuilder.RenameColumn(
                name: "utc_offset",
                table: "entries",
                newName: "utcOffset");

            migrationBuilder.RenameColumn(
                name: "sys_time",
                table: "entries",
                newName: "sysTime");

            migrationBuilder.RenameColumn(
                name: "date_string",
                table: "entries",
                newName: "dateString");

            migrationBuilder.RenameColumn(
                name: "utc_offset",
                table: "devicestatus",
                newName: "utcOffset");

            migrationBuilder.RenameColumn(
                name: "radio_adapter",
                table: "devicestatus",
                newName: "radioAdapter");

            migrationBuilder.RenameColumn(
                name: "is_charging",
                table: "devicestatus",
                newName: "isCharging");

            migrationBuilder.RenameColumn(
                name: "insulin_pen",
                table: "devicestatus",
                newName: "insulinPen");

            migrationBuilder.RenameColumn(
                name: "utc_offset",
                table: "activities",
                newName: "utcOffset");

            migrationBuilder.RenameColumn(
                name: "entered_by",
                table: "activities",
                newName: "enteredBy");

            migrationBuilder.RenameColumn(
                name: "date_string",
                table: "activities",
                newName: "dateString");
        }
    }
}
