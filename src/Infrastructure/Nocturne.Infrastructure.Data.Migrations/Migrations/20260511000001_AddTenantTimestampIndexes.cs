using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTimestampIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These index names overlap with AddCompositePerformanceIndexes (20260511122202),
            // which creates them with descending timestamp ordering. On databases that already
            // ran the later migration, recreating them here would fail; on fresh databases
            // the later migration would fail when it ran second. IF NOT EXISTS makes both
            // paths a no-op when the index is already present.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_boluses_tenant_timestamp " +
                "ON boluses (tenant_id, timestamp);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_carb_intakes_tenant_timestamp " +
                "ON carb_intakes (tenant_id, timestamp);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_temp_basals_tenant_start_timestamp " +
                "ON temp_basals (tenant_id, start_timestamp);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_boluses_tenant_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_carb_intakes_tenant_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_temp_basals_tenant_start_timestamp;");
        }
    }
}
