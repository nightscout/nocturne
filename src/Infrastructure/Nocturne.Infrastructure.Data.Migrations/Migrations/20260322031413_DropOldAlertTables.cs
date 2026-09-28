using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropOldAlertTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_history");

            migrationBuilder.DropTable(
                name: "emergency_contacts");

            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropTable(
                name: "alert_rules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    active_hours = table.Column<string>(type: "jsonb", nullable: true),
                    client_configuration = table.Column<string>(type: "jsonb", nullable: true),
                    cooldown_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    days_of_week = table.Column<string>(type: "jsonb", nullable: true),
                    default_snooze_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    escalation_delay_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 15),
                    forecast_lead_time_minutes = table.Column<int>(type: "integer", nullable: true),
                    high_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    low_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    max_escalations = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    max_snooze_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 120),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    notification_channels = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    urgent_high_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    urgent_low_threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "emergency_contacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_types = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    contact_type = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    email_address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_emergency_contacts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    battery_low_threshold = table.Column<int>(type: "integer", nullable: true),
                    calibration_reminder_hours = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    data_gap_warning_minutes = table.Column<int>(type: "integer", nullable: true),
                    email_address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    emergency_override_quiet_hours = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pushover_devices = table.Column<string>(type: "jsonb", nullable: true),
                    pushover_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    pushover_user_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    quiet_hours_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    sensor_expiration_warning_hours = table.Column<int>(type: "integer", nullable: true),
                    sms_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sms_phone_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    webhook_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    webhook_urls = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "alert_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledgment_notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    alert_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    escalation_attempts = table.Column<string>(type: "jsonb", nullable: false),
                    escalation_level = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    escalation_paused = table.Column<bool>(type: "boolean", nullable: false),
                    escalation_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    glucose_value = table.Column<decimal>(type: "numeric", nullable: true),
                    next_escalation_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    notifications_sent = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    snooze_count = table.Column<int>(type: "integer", nullable: false),
                    snooze_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    snooze_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    threshold = table.Column<decimal>(type: "numeric", nullable: true),
                    trigger_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_history_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_alert_rule_id",
                table: "alert_history",
                column: "alert_rule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_alert_type",
                table: "alert_history",
                column: "alert_type");

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_status",
                table: "alert_history",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_trigger_time",
                table: "alert_history",
                column: "trigger_time",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_user_id",
                table: "alert_history",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_user_status",
                table: "alert_history",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_history_user_trigger_time",
                table: "alert_history",
                columns: new[] { "user_id", "trigger_time" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_created_at",
                table: "alert_rules",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_is_enabled",
                table: "alert_rules",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_user_enabled",
                table: "alert_rules",
                columns: new[] { "user_id", "is_enabled" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_user_id",
                table: "alert_rules",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_contacts_contact_type",
                table: "emergency_contacts",
                column: "contact_type");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_contacts_is_active",
                table: "emergency_contacts",
                column: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_contacts_priority",
                table: "emergency_contacts",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_emergency_contacts_user_id",
                table: "emergency_contacts",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_email_enabled",
                table: "notification_preferences",
                column: "email_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_pushover_enabled",
                table: "notification_preferences",
                column: "pushover_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_sms_enabled",
                table: "notification_preferences",
                column: "sms_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_tenant_user",
                table: "notification_preferences",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);
        }
    }
}
