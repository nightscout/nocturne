using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertEngineTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    condition_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    condition_params = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    hysteresis_minutes = table.Column<int>(type: "integer", nullable: false),
                    confirmation_readings = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "alert_excursions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acknowledged_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    hysteresis_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_excursions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_excursions_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    days_of_week = table.Column<string>(type: "jsonb", nullable: true),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_schedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_schedules_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_tracker_state",
                columns: table => new
                {
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "idle"),
                    confirmation_count = table.Column<int>(type: "integer", nullable: false),
                    active_excursion_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_tracker_state", x => x.alert_rule_id);
                    table.ForeignKey(
                        name: "FK_alert_tracker_state_alert_excursions_active_excursion_id",
                        column: x => x.active_excursion_id,
                        principalTable: "alert_excursions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_alert_tracker_state_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_escalation_steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_escalation_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_escalation_steps_alert_schedules_alert_schedule_id",
                        column: x => x.alert_schedule_id,
                        principalTable: "alert_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_excursion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_step_order = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "triggered"),
                    triggered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_escalation_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_instances_alert_excursions_alert_excursion_id",
                        column: x => x.alert_excursion_id,
                        principalTable: "alert_excursions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_instances_alert_schedules_alert_schedule_id",
                        column: x => x.alert_schedule_id,
                        principalTable: "alert_schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    escalation_step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "view_acknowledge"),
                    is_used = table.Column<bool>(type: "boolean", nullable: false),
                    used_by = table.Column<Guid>(type: "uuid", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_invites_alert_escalation_steps_escalation_step_id",
                        column: x => x.escalation_step_id,
                        principalTable: "alert_escalation_steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_step_channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    destination = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    destination_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_step_channels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_step_channels_alert_escalation_steps_escalation_step_~",
                        column: x => x.escalation_step_id,
                        principalTable: "alert_escalation_steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    destination = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "pending"),
                    platform_message_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    platform_thread_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    retry_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_deliveries_alert_escalation_steps_escalation_step_id",
                        column: x => x.escalation_step_id,
                        principalTable: "alert_escalation_steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_deliveries_alert_instances_alert_instance_id",
                        column: x => x.alert_instance_id,
                        principalTable: "alert_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_last_reading_at",
                table: "tenants",
                column: "last_reading_at");

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_alert_instance_id",
                table: "alert_deliveries",
                column: "alert_instance_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_escalation_step_id",
                table: "alert_deliveries",
                column: "escalation_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_deliveries_status_created_at",
                table: "alert_deliveries",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_escalation_steps_alert_schedule_id",
                table: "alert_escalation_steps",
                column: "alert_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_excursions_rule_ended_at",
                table: "alert_excursions",
                columns: new[] { "alert_rule_id", "ended_at" });

            migrationBuilder.CreateIndex(
                name: "ix_alert_excursions_tenant_ended_at",
                table: "alert_excursions",
                columns: new[] { "tenant_id", "ended_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_instances_alert_excursion_id",
                table: "alert_instances",
                column: "alert_excursion_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_instances_alert_schedule_id",
                table: "alert_instances",
                column: "alert_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_instances_status_next_escalation",
                table: "alert_instances",
                columns: new[] { "status", "next_escalation_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_invites_escalation_step_id",
                table: "alert_invites",
                column: "escalation_step_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_invites_token",
                table: "alert_invites",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_schedules_alert_rule_id",
                table: "alert_schedules",
                column: "alert_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_step_channels_escalation_step_id",
                table: "alert_step_channels",
                column: "escalation_step_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_tracker_state_active_excursion_id",
                table: "alert_tracker_state",
                column: "active_excursion_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_deliveries");

            migrationBuilder.DropTable(
                name: "alert_invites");

            migrationBuilder.DropTable(
                name: "alert_step_channels");

            migrationBuilder.DropTable(
                name: "alert_tracker_state");

            migrationBuilder.DropTable(
                name: "alert_instances");

            migrationBuilder.DropTable(
                name: "alert_escalation_steps");

            migrationBuilder.DropTable(
                name: "alert_excursions");

            migrationBuilder.DropTable(
                name: "alert_schedules");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.DropIndex(
                name: "ix_tenants_last_reading_at",
                table: "tenants");
        }
    }
}
