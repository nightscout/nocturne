using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Every statement here is re-runnable, because the concurrent build at the end cannot be
    /// transactional: a failure in it leaves everything before it committed. A plain
    /// <c>CREATE INDEX</c> would then fail the retry on its own first statement, and
    /// <see cref="ConcurrentIndexBuilder"/>'s repair of the interrupted build would never be
    /// reached.
    /// </summary>
    public partial class AddCompositePerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_therapy_settings_tenant_timestamp " +
                "ON therapy_settings (tenant_id, timestamp DESC);");

            // AddTenantTimestampIndexes (20260511000001) creates this name without the descending
            // ordering, so on an instance that ran it the ordering here never takes effect.
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_temp_basals_tenant_start_timestamp " +
                "ON temp_basals (tenant_id, start_timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_target_range_schedules_tenant_profile_timestamp " +
                "ON target_range_schedules (tenant_id, profile_name, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_sensitivity_schedules_tenant_profile_timestamp " +
                "ON sensitivity_schedules (tenant_id, profile_name, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_carb_ratio_schedules_tenant_profile_timestamp " +
                "ON carb_ratio_schedules (tenant_id, profile_name, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_carb_intakes_tenant_timestamp " +
                "ON carb_intakes (tenant_id, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_boluses_tenant_timestamp " +
                "ON boluses (tenant_id, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_basal_schedules_tenant_profile_timestamp " +
                "ON basal_schedules (tenant_id, profile_name, timestamp DESC);");

            // Partial index for deduplication subqueries: WHERE tenant_id=? AND record_type=? AND NOT is_primary.
            // Raw SQL rather than CreateIndex so EF Core does not conflate it with the existing
            // full unique index on the same columns.
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_tenant_type_not_primary",
                "ON linked_records (tenant_id, record_type, record_id) WHERE NOT is_primary");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_therapy_settings_tenant_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_temp_basals_tenant_start_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_target_range_schedules_tenant_profile_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_sensitivity_schedules_tenant_profile_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_carb_ratio_schedules_tenant_profile_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_carb_intakes_tenant_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_boluses_tenant_timestamp;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_basal_schedules_tenant_profile_timestamp;");

            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_linked_records_tenant_type_not_primary");
        }
    }
}
