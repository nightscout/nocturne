using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeBasalInjectionInsulinContextOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "insulin_context",
                table: "basal_injections",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows written without a PatientInsulin reference hold NULL; backfill them to an empty
            // JSON object before restoring NOT NULL, or the ALTER fails. basal_injections is
            // tenant-scoped under FORCE ROW LEVEL SECURITY, so the update runs per tenant with the
            // tenant GUC set. (The scaffolded defaultValue of "" is not valid jsonb, hence "{}".)
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t_id uuid;
                BEGIN
                    FOR t_id IN SELECT id FROM tenants
                    LOOP
                        PERFORM set_config('app.current_tenant_id', t_id::text, true);

                        UPDATE basal_injections
                        SET insulin_context = '{}'::jsonb
                        WHERE insulin_context IS NULL;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "insulin_context",
                table: "basal_injections",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
    }
}
