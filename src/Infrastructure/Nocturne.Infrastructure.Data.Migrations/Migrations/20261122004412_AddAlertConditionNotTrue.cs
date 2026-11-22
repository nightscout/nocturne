using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertConditionNotTrue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_condition_not_true",
                columns: table => new
                {
                    alert_rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condition_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    not_true_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_condition_not_true", x => new { x.alert_rule_id, x.condition_path });
                    table.ForeignKey(
                        name: "FK_alert_condition_not_true_alert_rules_alert_rule_id",
                        column: x => x.alert_rule_id,
                        principalTable: "alert_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_condition_not_true_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_condition_not_true_tenant_id",
                table: "alert_condition_not_true",
                column: "tenant_id");

            migrationBuilder.Sql("ALTER TABLE alert_condition_not_true ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_condition_not_true FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON alert_condition_not_true;
                CREATE POLICY tenant_isolation ON alert_condition_not_true
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_condition_not_true");
        }
    }
}
