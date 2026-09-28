using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class SwitchV4TimestampsToTimestamptz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_mills",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_mills",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_end_mills",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_start_mills",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_mills",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_category_start",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_end_mills",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_start_mills",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_mills",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_mills",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_mills",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_notes_mills",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_meter_glucose_mills",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_device_events_mills",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_mills",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_mills",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_calibrations_mills",
                table: "calibrations");

            migrationBuilder.DropIndex(
                name: "ix_boluses_mills",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_mills",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_mills",
                table: "bg_checks");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_mills",
                table: "basal_schedules");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_mills",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "end_mills",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "start_mills",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "end_mills",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "start_mills",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "first_seen_mills",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "last_seen_mills",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "mills",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "predicted_start_mills",
                table: "aps_snapshots");

            migrationBuilder.RenameIndex(
                name: "ix_treatments_event_type_mills",
                table: "treatments",
                newName: "ix_treatments_event_type_timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_system_events_category_mills",
                table: "system_events",
                newName: "ix_system_events_category_timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_entries_type_mills",
                table: "entries",
                newName: "ix_entries_type_timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_devicestatus_mills",
                table: "devicestatus",
                newName: "ix_devicestatus_timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_devicestatus_device_mills",
                table: "devicestatus",
                newName: "ix_devicestatus_device_timestamp");

            migrationBuilder.RenameIndex(
                name: "ix_activities_type_mills",
                table: "activities",
                newName: "ix_activities_type_timestamp");

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "uploader_snapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "therapy_settings",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "end_timestamp",
                table: "temp_basals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "start_timestamp",
                table: "temp_basals",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "target_range_schedules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "end_timestamp",
                table: "state_spans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "start_timestamp",
                table: "state_spans",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "sensor_glucose",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "sensitivity_schedules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "pump_snapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "first_seen_timestamp",
                table: "pump_devices",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "last_seen_timestamp",
                table: "pump_devices",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "notes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "meter_glucose",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "device_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "carb_ratio_schedules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "carb_intakes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "calibrations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "boluses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "bolus_calculations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "bg_checks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "basal_schedules",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "predicted_start_timestamp",
                table: "aps_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "timestamp",
                table: "aps_snapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_timestamp",
                table: "uploader_snapshots",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_timestamp",
                table: "therapy_settings",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_end_timestamp",
                table: "temp_basals",
                column: "end_timestamp");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_start_timestamp",
                table: "temp_basals",
                column: "start_timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_timestamp",
                table: "target_range_schedules",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_category_start",
                table: "state_spans",
                columns: new[] { "category", "start_timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_end_timestamp",
                table: "state_spans",
                column: "end_timestamp",
                filter: "end_timestamp IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_start_timestamp",
                table: "state_spans",
                column: "start_timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_timestamp",
                table: "sensor_glucose",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_timestamp",
                table: "sensitivity_schedules",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_timestamp",
                table: "pump_snapshots",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_notes_timestamp",
                table: "notes",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_timestamp",
                table: "meter_glucose",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_timestamp",
                table: "device_events",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_timestamp",
                table: "carb_ratio_schedules",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_timestamp",
                table: "carb_intakes",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_timestamp",
                table: "calibrations",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_boluses_timestamp",
                table: "boluses",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_timestamp",
                table: "bolus_calculations",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_timestamp",
                table: "bg_checks",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_timestamp",
                table: "basal_schedules",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_timestamp",
                table: "aps_snapshots",
                column: "timestamp",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_uploader_snapshots_timestamp",
                table: "uploader_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_timestamp",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_end_timestamp",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_start_timestamp",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_timestamp",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_category_start",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_end_timestamp",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_start_timestamp",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_timestamp",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_timestamp",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_pump_snapshots_timestamp",
                table: "pump_snapshots");

            migrationBuilder.DropIndex(
                name: "ix_notes_timestamp",
                table: "notes");

            migrationBuilder.DropIndex(
                name: "ix_meter_glucose_timestamp",
                table: "meter_glucose");

            migrationBuilder.DropIndex(
                name: "ix_device_events_timestamp",
                table: "device_events");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_timestamp",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_timestamp",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_calibrations_timestamp",
                table: "calibrations");

            migrationBuilder.DropIndex(
                name: "ix_boluses_timestamp",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_bolus_calculations_timestamp",
                table: "bolus_calculations");

            migrationBuilder.DropIndex(
                name: "ix_bg_checks_timestamp",
                table: "bg_checks");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_timestamp",
                table: "basal_schedules");

            migrationBuilder.DropIndex(
                name: "ix_aps_snapshots_timestamp",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "end_timestamp",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "start_timestamp",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "end_timestamp",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "start_timestamp",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "first_seen_timestamp",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "last_seen_timestamp",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "predicted_start_timestamp",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "timestamp",
                table: "aps_snapshots");

            migrationBuilder.RenameIndex(
                name: "ix_treatments_event_type_timestamp",
                table: "treatments",
                newName: "ix_treatments_event_type_mills");

            migrationBuilder.RenameIndex(
                name: "ix_system_events_category_timestamp",
                table: "system_events",
                newName: "ix_system_events_category_mills");

            migrationBuilder.RenameIndex(
                name: "ix_entries_type_timestamp",
                table: "entries",
                newName: "ix_entries_type_mills");

            migrationBuilder.RenameIndex(
                name: "ix_devicestatus_timestamp",
                table: "devicestatus",
                newName: "ix_devicestatus_mills");

            migrationBuilder.RenameIndex(
                name: "ix_devicestatus_device_timestamp",
                table: "devicestatus",
                newName: "ix_devicestatus_device_mills");

            migrationBuilder.RenameIndex(
                name: "ix_activities_type_timestamp",
                table: "activities",
                newName: "ix_activities_type_mills");

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "uploader_snapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "therapy_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "end_mills",
                table: "temp_basals",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "start_mills",
                table: "temp_basals",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "target_range_schedules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "end_mills",
                table: "state_spans",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "start_mills",
                table: "state_spans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "sensor_glucose",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "sensitivity_schedules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "pump_snapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "first_seen_mills",
                table: "pump_devices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "last_seen_mills",
                table: "pump_devices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "notes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "meter_glucose",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "device_events",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "carb_ratio_schedules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "carb_intakes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "calibrations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "boluses",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "bolus_calculations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "bg_checks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "basal_schedules",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "mills",
                table: "aps_snapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "predicted_start_mills",
                table: "aps_snapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_uploader_snapshots_mills",
                table: "uploader_snapshots",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_mills",
                table: "therapy_settings",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_end_mills",
                table: "temp_basals",
                column: "end_mills");

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_start_mills",
                table: "temp_basals",
                column: "start_mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_mills",
                table: "target_range_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_category_start",
                table: "state_spans",
                columns: new[] { "category", "start_mills" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_end_mills",
                table: "state_spans",
                column: "end_mills",
                filter: "end_mills IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_start_mills",
                table: "state_spans",
                column: "start_mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_mills",
                table: "sensor_glucose",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_mills",
                table: "sensitivity_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_pump_snapshots_mills",
                table: "pump_snapshots",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_notes_mills",
                table: "notes",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_meter_glucose_mills",
                table: "meter_glucose",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_device_events_mills",
                table: "device_events",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_mills",
                table: "carb_ratio_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_mills",
                table: "carb_intakes",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_calibrations_mills",
                table: "calibrations",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_boluses_mills",
                table: "boluses",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_bolus_calculations_mills",
                table: "bolus_calculations",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_bg_checks_mills",
                table: "bg_checks",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_mills",
                table: "basal_schedules",
                column: "mills",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_aps_snapshots_mills",
                table: "aps_snapshots",
                column: "mills",
                descending: new bool[0]);
        }
    }
}
