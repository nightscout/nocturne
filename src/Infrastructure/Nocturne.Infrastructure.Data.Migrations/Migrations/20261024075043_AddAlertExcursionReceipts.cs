using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertExcursionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_excursion_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_excursion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_excursion_receipts", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_excursion_receipts_alert_excursions_alert_excursion_id",
                        column: x => x.alert_excursion_id,
                        principalTable: "alert_excursions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_excursion_receipts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_excursion_receipts_alert_excursion_id",
                table: "alert_excursion_receipts",
                column: "alert_excursion_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_excursion_receipts_tenant_excursion_actor",
                table: "alert_excursion_receipts",
                columns: new[] { "tenant_id", "alert_excursion_id", "actor_kind", "actor_id" },
                unique: true);

            // NULLIF + missing_ok keeps the policy expression safe to evaluate when the GUC is
            // unset, matching every other tenant-scoped table.
            migrationBuilder.Sql("ALTER TABLE alert_excursion_receipts ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE alert_excursion_receipts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON alert_excursion_receipts;
                CREATE POLICY tenant_isolation ON alert_excursion_receipts
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_excursion_receipts");
        }
    }
}
