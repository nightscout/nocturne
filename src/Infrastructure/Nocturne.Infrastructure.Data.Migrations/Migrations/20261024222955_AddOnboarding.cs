using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_glucose_units",
                table: "tenants",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "patient_relationship",
                table: "tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "setup_hub_enrolled_at",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "setup_strip_dismissed_revision",
                table: "tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "takes_no_insulin",
                table: "patient_records",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.CreateTable(
                name: "setup_hub_additions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    record_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_hub_additions", x => x.id);
                    table.ForeignKey(
                        name: "FK_setup_hub_additions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "setup_hub_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    confirmed_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setup_hub_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_setup_hub_items_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateIndex(
                name: "ix_setup_hub_additions_tenant_item_kind",
                table: "setup_hub_additions",
                columns: new[] { "tenant_id", "item_key", "record_kind" });

            migrationBuilder.CreateIndex(
                name: "ix_setup_hub_items_tenant_item_key",
                table: "setup_hub_items",
                columns: new[] { "tenant_id", "item_key" },
                unique: true);

            migrationBuilder.Sql("ALTER TABLE setup_hub_items ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE setup_hub_items FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON setup_hub_items;
                CREATE POLICY tenant_isolation ON setup_hub_items
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE setup_hub_additions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE setup_hub_additions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS tenant_isolation ON setup_hub_additions;
                CREATE POLICY tenant_isolation ON setup_hub_additions
                    USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "setup_hub_additions");

            migrationBuilder.DropTable(
                name: "setup_hub_items");

            migrationBuilder.DropIndex(
                name: "ix_alert_rules_starter_kind_tenant",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "default_glucose_units",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "patient_relationship",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "setup_hub_enrolled_at",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "setup_strip_dismissed_revision",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "takes_no_insulin",
                table: "patient_records");

            migrationBuilder.DropColumn(
                name: "starter_kind",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "receipt_confirmed_at",
                table: "alert_instances");
        }
    }
}
