using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceStatusExtrasTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_status_extras",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    extras = table.Column<string>(type: "jsonb", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_status_extras", x => x.id);
                    table.ForeignKey(
                        name: "FK_device_status_extras_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_status_extras_tenant_id",
                table: "device_status_extras",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_status_extras_correlation_id",
                table: "device_status_extras",
                column: "correlation_id");

            migrationBuilder.Sql("ALTER TABLE device_status_extras ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE device_status_extras FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                CREATE POLICY tenant_isolation ON device_status_extras
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON device_status_extras;");
            migrationBuilder.Sql("ALTER TABLE device_status_extras NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE device_status_extras DISABLE ROW LEVEL SECURITY;");

            migrationBuilder.DropTable(
                name: "device_status_extras");
        }
    }
}
