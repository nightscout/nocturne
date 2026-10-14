using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertSetupStarterRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "starter_kind",
                table: "alert_rules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "receipt_confirmed_at",
                table: "alert_instances",
                type: "timestamp with time zone",
                nullable: true);

            // starter_kind is added above, so no row holds one yet and this removes nothing. It is
            // here because the unique index goes over a table this migration did not create
            // (CLAUDE.md, "Unique indexes over existing data"). The oldest rule of a kind wins.
            // FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);

                        DELETE FROM alert_rules
                        WHERE id IN (
                            SELECT id
                            FROM (
                                SELECT id,
                                       row_number() OVER (
                                           PARTITION BY tenant_id, starter_kind
                                           ORDER BY created_at, id) AS rn
                                FROM alert_rules
                                WHERE starter_kind IS NOT NULL
                            ) ranked
                            WHERE ranked.rn > 1);
                    END LOOP;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_alert_rules_starter_kind_tenant",
                table: "alert_rules",
                columns: new[] { "starter_kind", "tenant_id" },
                unique: true,
                filter: "starter_kind IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_alert_rules_starter_kind_tenant",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "starter_kind",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "receipt_confirmed_at",
                table: "alert_instances");
        }
    }
}
