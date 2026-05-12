using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositePerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_therapy_settings_tenant_timestamp",
                table: "therapy_settings",
                columns: new[] { "tenant_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_tenant_start_timestamp",
                table: "temp_basals",
                columns: new[] { "tenant_id", "start_timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_target_range_schedules_tenant_profile_timestamp",
                table: "target_range_schedules",
                columns: new[] { "tenant_id", "profile_name", "timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_sensitivity_schedules_tenant_profile_timestamp",
                table: "sensitivity_schedules",
                columns: new[] { "tenant_id", "profile_name", "timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_carb_ratio_schedules_tenant_profile_timestamp",
                table: "carb_ratio_schedules",
                columns: new[] { "tenant_id", "profile_name", "timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_timestamp",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_timestamp",
                table: "boluses",
                columns: new[] { "tenant_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_basal_schedules_tenant_profile_timestamp",
                table: "basal_schedules",
                columns: new[] { "tenant_id", "profile_name", "timestamp" },
                descending: new[] { false, false, true });

            // Partial index for deduplication subqueries: WHERE tenant_id=? AND record_type=? AND NOT is_primary.
            // Added via raw SQL to avoid EF Core conflating it with the existing full unique index on the same columns.
            // CONCURRENTLY avoids an AccessExclusiveLock on linked_records (6M rows) during migration;
            // suppressTransaction is required because CONCURRENTLY cannot run inside a transaction block.
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_linked_records_tenant_type_not_primary " +
                "ON linked_records (tenant_id, record_type, record_id) WHERE NOT is_primary;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_therapy_settings_tenant_timestamp",
                table: "therapy_settings");

            migrationBuilder.DropIndex(
                name: "ix_temp_basals_tenant_start_timestamp",
                table: "temp_basals");

            migrationBuilder.DropIndex(
                name: "ix_target_range_schedules_tenant_profile_timestamp",
                table: "target_range_schedules");

            migrationBuilder.DropIndex(
                name: "ix_sensitivity_schedules_tenant_profile_timestamp",
                table: "sensitivity_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_ratio_schedules_tenant_profile_timestamp",
                table: "carb_ratio_schedules");

            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_timestamp",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_timestamp",
                table: "boluses");

            migrationBuilder.DropIndex(
                name: "ix_basal_schedules_tenant_profile_timestamp",
                table: "basal_schedules");

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS ix_linked_records_tenant_type_not_primary;",
                suppressTransaction: true);
        }
    }
}
