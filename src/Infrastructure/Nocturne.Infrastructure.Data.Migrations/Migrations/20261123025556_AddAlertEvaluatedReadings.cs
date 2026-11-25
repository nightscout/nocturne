using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertEvaluatedReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_evaluated_readings",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reading_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reading_mgdl = table.Column<double>(type: "double precision", nullable: false),
                    reading_trend_rate = table.Column<double>(type: "double precision", nullable: true),
                    evaluated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_evaluated_readings", x => x.tenant_id);
                    table.ForeignKey(
                        name: "FK_alert_evaluated_readings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("ALTER TABLE alert_evaluated_readings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_evaluated_readings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON alert_evaluated_readings;
                CREATE POLICY tenant_isolation ON alert_evaluated_readings
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_evaluated_readings");
        }
    }
}
