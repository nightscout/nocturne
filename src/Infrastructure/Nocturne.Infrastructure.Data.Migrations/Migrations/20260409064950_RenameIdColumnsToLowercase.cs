using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameIdColumnsToLowercase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Id",
                table: "user_food_favorites",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "uploader_snapshots",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "treatments",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "treatment_foods",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tracker_presets",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tracker_notification_thresholds",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tracker_instances",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tracker_definitions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "totp_credentials",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "therapy_settings",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tenants",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "tenant_members",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "temp_basals",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "target_range_schedules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "system_events",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "subjects",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "step_counts",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "state_spans",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "settings",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "sensor_glucose",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "sensitivity_schedules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "roles",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "refresh_tokens",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "recovery_codes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "pump_snapshots",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "profiles",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "patient_records",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "patient_insulins",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "patient_devices",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "passkey_credentials",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oidc_providers",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oauth_refresh_tokens",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oauth_grants",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oauth_device_codes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oauth_clients",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "oauth_authorization_codes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "notes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "migration_sources",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "migration_runs",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "meter_glucose",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "member_invites",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "linked_records",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "in_app_notifications",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "heart_rates",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "foods",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "entries",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "devicestatus",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "devices",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "device_events",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "connector_food_entries",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "connector_configurations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "compression_low_suggestions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "clock_faces",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "carb_ratio_schedules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "carb_intakes",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "calibrations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "boluses",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "bolus_calculations",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "body_weights",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "bg_checks",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "basal_schedules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "auth_audit_log",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "aps_snapshots",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_step_channels",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_schedules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_rules",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_invites",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_instances",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_excursions",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_escalation_steps",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_deliveries",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "alert_custom_sounds",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "activities",
                newName: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "id",
                table: "user_food_favorites",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "uploader_snapshots",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "treatments",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "treatment_foods",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tracker_presets",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tracker_notification_thresholds",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tracker_instances",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tracker_definitions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "totp_credentials",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "therapy_settings",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tenants",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "tenant_members",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "temp_basals",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "target_range_schedules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "system_events",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "subjects",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "step_counts",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "state_spans",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "settings",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "sensor_glucose",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "sensitivity_schedules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "roles",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "refresh_tokens",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "recovery_codes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "pump_snapshots",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "profiles",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "patient_records",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "patient_insulins",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "patient_devices",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "passkey_credentials",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oidc_providers",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oauth_refresh_tokens",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oauth_grants",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oauth_device_codes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oauth_clients",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "oauth_authorization_codes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "notes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "migration_sources",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "migration_runs",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "meter_glucose",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "member_invites",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "linked_records",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "in_app_notifications",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "heart_rates",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "foods",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "entries",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "devicestatus",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "devices",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "device_events",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "connector_food_entries",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "connector_configurations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "compression_low_suggestions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "clock_faces",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "carb_ratio_schedules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "carb_intakes",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "calibrations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "boluses",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "bolus_calculations",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "body_weights",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "bg_checks",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "basal_schedules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "auth_audit_log",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "aps_snapshots",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_step_channels",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_schedules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_rules",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_invites",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_instances",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_excursions",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_escalation_steps",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_deliveries",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "alert_custom_sounds",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "activities",
                newName: "Id");
        }
    }
}
