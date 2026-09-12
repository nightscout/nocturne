using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "personal_google_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_settings = table.Column<string>(type: "text", nullable: false),
                    protected_token = table.Column<string>(type: "text", nullable: true),
                    account_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_sync = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_google_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_personal_google_connections_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "personal_health_readings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    end_mills = table.Column<long>(type: "bigint", nullable: true),
                    utc_offset_minutes = table.Column<int>(type: "integer", nullable: true),
                    value = table.Column<decimal>(type: "numeric", nullable: false),
                    unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_health_readings", x => x.id);
                    table.ForeignKey(
                        name: "FK_personal_health_readings_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_personal_google_connections_tenant_id",
                table: "personal_google_connections",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_personal_health_readings_tenant_id_data_type_mills",
                table: "personal_health_readings",
                columns: new[] { "tenant_id", "data_type", "mills" });

            migrationBuilder.CreateIndex(
                name: "IX_personal_health_readings_tenant_id_data_type_source_key",
                table: "personal_health_readings",
                columns: new[] { "tenant_id", "data_type", "source_key" },
                unique: true);

            foreach (var table in new[] { "personal_google_connections", "personal_health_readings" })
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON {table}
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                        AND COALESCE(current_setting('app.is_share', true), '') <> 'true')
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                        AND COALESCE(current_setting('app.is_share', true), '') <> 'true');
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "personal_google_connections");

            migrationBuilder.DropTable(
                name: "personal_health_readings");

        }
    }
}
