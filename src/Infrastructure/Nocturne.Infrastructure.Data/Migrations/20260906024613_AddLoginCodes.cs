using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "login_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_login_codes", x => x.id);
                    table.ForeignKey(
                        name: "FK_login_codes_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_login_codes_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_login_codes_code_hash",
                table: "login_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_login_codes_expires_at",
                table: "login_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_login_codes_subject_id",
                table: "login_codes",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_login_codes_tenant_id",
                table: "login_codes",
                column: "tenant_id");

            // NULLIF + missing_ok keeps the policy expression safe to evaluate when the GUC is
            // unset, matching every other tenant-scoped table.
            migrationBuilder.Sql("ALTER TABLE login_codes ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE login_codes FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON login_codes;
                CREATE POLICY tenant_isolation ON login_codes
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_codes");
        }
    }
}
