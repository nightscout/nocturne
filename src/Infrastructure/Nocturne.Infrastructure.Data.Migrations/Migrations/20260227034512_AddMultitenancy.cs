using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultitenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "user_food_favorites",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "uploader_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "treatments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "treatment_foods",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "tracker_presets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "tracker_notification_thresholds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "tracker_instances",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "tracker_definitions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "therapy_settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "temp_basals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "target_range_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "step_counts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "state_spans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "settings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "sensor_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "sensitivity_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "pump_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "pump_devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "notification_preferences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "notes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "meter_glucose",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "linked_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "in_app_notifications",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "heart_rates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "foods",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "emergency_contacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "discrepancy_details",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "discrepancy_analyses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "devicestatus",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "device_health",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "device_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "data_source_metadata",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "connector_food_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "connector_configurations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "compression_low_suggestions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "clock_faces",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "carb_ratio_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "carb_intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "calibrations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "boluses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "bolus_calculations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "bg_checks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "basal_schedules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "aps_snapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "alert_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "alert_history",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "activities",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    api_secret_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_members_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_members_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_subject_id",
                table: "tenant_members",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            // Composite indexes for high-query tables
            migrationBuilder.CreateIndex(
                name: "ix_entries_tenant_mills",
                table: "entries",
                columns: new[] { "tenant_id", "mills" });

            migrationBuilder.CreateIndex(
                name: "ix_sensor_glucose_tenant_timestamp",
                table: "sensor_glucose",
                columns: new[] { "tenant_id", "timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_treatments_tenant_mills",
                table: "treatments",
                columns: new[] { "tenant_id", "mills" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop composite indexes
            migrationBuilder.DropIndex(
                name: "ix_entries_tenant_mills",
                table: "entries");

            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_timestamp",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_treatments_tenant_mills",
                table: "treatments");

            migrationBuilder.DropTable(
                name: "tenant_members");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "user_food_favorites");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "treatment_foods");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "tracker_presets");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "tracker_notification_thresholds");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "tracker_instances");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "tracker_definitions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "therapy_settings");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "target_range_schedules");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "step_counts");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "sensor_glucose");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "sensitivity_schedules");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "pump_devices");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "notification_preferences");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "meter_glucose");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "linked_records");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "in_app_notifications");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "heart_rates");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "foods");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "entries");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "emergency_contacts");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "discrepancy_details");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "discrepancy_analyses");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "devicestatus");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "device_health");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "data_source_metadata");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "connector_food_entries");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "connector_configurations");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "compression_low_suggestions");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "clock_faces");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "carb_ratio_schedules");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "calibrations");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "bg_checks");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "basal_schedules");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "aps_snapshots");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "alert_history");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "activities");
        }
    }
}
