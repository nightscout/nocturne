using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDoNotDisturbAndFlatChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_through_dnd",
                table: "alert_rules",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "suppression_reason",
                table: "alert_instances",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "alert_rule_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    destination = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    destination_label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rule_channels", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_rule_channels_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_rule_channels_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_alert_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dnd_manual_active = table.Column<bool>(type: "boolean", nullable: false),
                    dnd_manual_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dnd_manual_started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    dnd_schedule_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    dnd_schedule_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    dnd_schedule_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_alert_settings", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_alert_settings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_rule_channels_alert_rule_id",
                table: "alert_rule_channels",
                column: "alert_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rule_channels_tenant_id",
                table: "alert_rule_channels",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_alert_settings_tenant_id_unique",
                table: "tenant_alert_settings",
                column: "tenant_id",
                unique: true);

            // Enable RLS on both new tenant-scoped tables. Same pattern as every other
            // tenant-scoped table in the codebase (USING + WITH CHECK on
            // app.current_tenant_id, plus FORCE so the migrator role obeys policies too).
            migrationBuilder.Sql("ALTER TABLE alert_rule_channels ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_rule_channels FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON alert_rule_channels;
                CREATE POLICY tenant_isolation ON alert_rule_channels
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE tenant_alert_settings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenant_alert_settings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS tenant_isolation ON tenant_alert_settings;
                CREATE POLICY tenant_isolation ON tenant_alert_settings
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON alert_rule_channels;");
            migrationBuilder.Sql("ALTER TABLE alert_rule_channels NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_rule_channels DISABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON tenant_alert_settings;");
            migrationBuilder.Sql("ALTER TABLE tenant_alert_settings NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE tenant_alert_settings DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "alert_rule_channels");

            migrationBuilder.DropTable(
                name: "tenant_alert_settings");

            migrationBuilder.DropColumn(
                name: "allow_through_dnd",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "suppression_reason",
                table: "alert_instances");
        }
    }
}
