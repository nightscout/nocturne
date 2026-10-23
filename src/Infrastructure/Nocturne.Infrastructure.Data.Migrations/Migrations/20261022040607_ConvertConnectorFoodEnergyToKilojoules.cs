using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Connector imports stored kilocalories. Only rows a connector wrote are certain to be kcal: a food
    /// with neither marker may have come from Nightscout or a form, already in kilojoules. The tenant is
    /// named in each statement as well as the GUC, so a migrator that bypasses RLS still converts each
    /// row once rather than once per tenant.
    /// </remarks>
    public partial class ConvertConnectorFoodEnergyToKilojoules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Scale("* 4.184"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Scale("/ 4.184"));
        }

        private static string Scale(string operation) => $$"""
            DO $$
            DECLARE
                t RECORD;
            BEGIN
                FOR t IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', t.id::text, true);

                    UPDATE foods
                    SET energy = energy {{operation}}
                    WHERE tenant_id = t.id
                      AND external_source IS NOT NULL
                      AND external_source <> '';

                    UPDATE connector_food_entries
                    SET energy = energy {{operation}}
                    WHERE tenant_id = t.id;
                END LOOP;
            END $$;
            """;
    }
}
