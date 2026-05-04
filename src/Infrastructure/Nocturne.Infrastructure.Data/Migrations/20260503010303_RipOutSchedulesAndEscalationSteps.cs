using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RipOutSchedulesAndEscalationSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-flight data fix: every alert_invites row's escalation_step_id points at a
            // row in alert_escalation_steps, which is about to be dropped. Without this
            // delete, the AddForeignKey at the end of Up() (re-pointing the column at
            // alert_rule_channels) would fail on any non-empty database. Invites are
            // short-lived tokens; clearing them is safe collateral for the schedule rip-out
            // and re-issuing is one click.
            migrationBuilder.Sql("DELETE FROM alert_invites;");

            // Resolve any latent rows whose status was set under the old escalation model.
            // Without this, the orchestrator (which now only knows triggered/acknowledged/
            // resolved) would silently leave them dangling forever.
            migrationBuilder.Sql(
                "UPDATE alert_instances SET status = 'resolved', resolved_at = NOW() " +
                "WHERE status = 'escalating';");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_deliveries_alert_escalation_steps_escalation_step_id",
                table: "alert_deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_instances_alert_schedules_alert_schedule_id",
                table: "alert_instances");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_invites_alert_escalation_steps_escalation_step_id",
                table: "alert_invites");

            migrationBuilder.DropTable(
                name: "alert_step_channels");

            migrationBuilder.DropTable(
                name: "alert_escalation_steps");

            migrationBuilder.DropTable(
                name: "alert_schedules");

            migrationBuilder.DropIndex(
                name: "IX_alert_instances_alert_schedule_id",
                table: "alert_instances");

            migrationBuilder.DropIndex(
                name: "ix_alert_instances_status_next_escalation",
                table: "alert_instances");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_escalation_step_id",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "alert_schedule_id",
                table: "alert_instances");

            migrationBuilder.DropColumn(
                name: "current_step_order",
                table: "alert_instances");

            migrationBuilder.DropColumn(
                name: "next_escalation_at",
                table: "alert_instances");

            migrationBuilder.DropColumn(
                name: "escalation_step_id",
                table: "alert_deliveries");

            migrationBuilder.RenameColumn(
                name: "escalation_step_id",
                table: "alert_invites",
                newName: "alert_rule_channel_id");

            migrationBuilder.RenameIndex(
                name: "IX_alert_invites_escalation_step_id",
                table: "alert_invites",
                newName: "IX_alert_invites_alert_rule_channel_id");

            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                table: "alert_instances",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "alert_rule_channel_id",
                table: "alert_deliveries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                table: "alert_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_alert_rule_channel_id",
                table: "alert_deliveries",
                column: "alert_rule_channel_id");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_deliveries_alert_rule_channels_alert_rule_channel_id",
                table: "alert_deliveries",
                column: "alert_rule_channel_id",
                principalTable: "alert_rule_channels",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_alert_invites_alert_rule_channels_alert_rule_channel_id",
                table: "alert_invites",
                column: "alert_rule_channel_id",
                principalTable: "alert_rule_channels",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_alert_deliveries_alert_rule_channels_alert_rule_channel_id",
                table: "alert_deliveries");

            migrationBuilder.DropForeignKey(
                name: "FK_alert_invites_alert_rule_channels_alert_rule_channel_id",
                table: "alert_invites");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_alert_rule_channel_id",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "is_test",
                table: "alert_instances");

            migrationBuilder.DropColumn(
                name: "alert_rule_channel_id",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "is_test",
                table: "alert_deliveries");

            migrationBuilder.RenameColumn(
                name: "alert_rule_channel_id",
                table: "alert_invites",
                newName: "escalation_step_id");

            migrationBuilder.RenameIndex(
                name: "IX_alert_invites_alert_rule_channel_id",
                table: "alert_invites",
                newName: "IX_alert_invites_escalation_step_id");

            migrationBuilder.AddColumn<Guid>(
                name: "alert_schedule_id",
                table: "alert_instances",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "current_step_order",
                table: "alert_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_escalation_at",
                table: "alert_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "escalation_step_id",
                table: "alert_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "alert_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    days_of_week = table.Column<string>(type: "jsonb", nullable: true),
                    end_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_override_critical = table.Column<bool>(type: "boolean", nullable: false),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    start_time = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_schedules", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_schedules_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_schedules_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_escalation_steps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    delay_seconds = table.Column<int>(type: "integer", nullable: false),
                    step_order = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_escalation_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_escalation_steps_alert_schedules_alert_schedule_id",
                        column: x => x.alert_schedule_id,
                        principalTable: "alert_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_escalation_steps_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_step_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    escalation_step_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    destination = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    destination_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_step_channels", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_step_channels_alert_escalation_steps_escalation_step_~",
                        column: x => x.escalation_step_id,
                        principalTable: "alert_escalation_steps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_step_channels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_instances_alert_schedule_id",
                table: "alert_instances",
                column: "alert_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_instances_status_next_escalation",
                table: "alert_instances",
                columns: new[] { "status", "next_escalation_at" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_escalation_step_id",
                table: "alert_deliveries",
                column: "escalation_step_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_escalation_steps_alert_schedule_id",
                table: "alert_escalation_steps",
                column: "alert_schedule_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_escalation_steps_tenant_id",
                table: "alert_escalation_steps",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_schedules_alert_rule_id",
                table: "alert_schedules",
                column: "alert_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_schedules_tenant_id",
                table: "alert_schedules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_step_channels_escalation_step_id",
                table: "alert_step_channels",
                column: "escalation_step_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_step_channels_tenant_id",
                table: "alert_step_channels",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_deliveries_alert_escalation_steps_escalation_step_id",
                table: "alert_deliveries",
                column: "escalation_step_id",
                principalTable: "alert_escalation_steps",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_alert_instances_alert_schedules_alert_schedule_id",
                table: "alert_instances",
                column: "alert_schedule_id",
                principalTable: "alert_schedules",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_alert_invites_alert_escalation_steps_escalation_step_id",
                table: "alert_invites",
                column: "escalation_step_id",
                principalTable: "alert_escalation_steps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
