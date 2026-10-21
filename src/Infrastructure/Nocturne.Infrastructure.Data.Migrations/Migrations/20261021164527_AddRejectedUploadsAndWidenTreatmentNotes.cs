using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRejectedUploadsAndWidenTreatmentNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "text",
                table: "notes",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096);

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                table: "device_events",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4096)",
                oldMaxLength: 4096,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "rejected_uploads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    identity = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    payload_bytes = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    origin = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    app = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    device = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rejected_uploads", x => x.id);
                    table.ForeignKey(
                        name: "FK_rejected_uploads_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rejected_uploads_tenant_last_seen_at",
                table: "rejected_uploads",
                columns: new[] { "tenant_id", "last_seen_at" });

            migrationBuilder.CreateIndex(
                name: "ux_rejected_uploads_tenant_record_identity",
                table: "rejected_uploads",
                columns: new[] { "tenant_id", "record_type", "identity" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE rejected_uploads ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE rejected_uploads FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON rejected_uploads;
                CREATE POLICY tenant_isolation ON rejected_uploads
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP POLICY IF EXISTS tenant_isolation ON rejected_uploads;");

            migrationBuilder.DropTable(
                name: "rejected_uploads");

            migrationBuilder.AlterColumn<string>(
                name: "text",
                table: "notes",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "notes",
                table: "device_events",
                type: "character varying(4096)",
                maxLength: 4096,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
