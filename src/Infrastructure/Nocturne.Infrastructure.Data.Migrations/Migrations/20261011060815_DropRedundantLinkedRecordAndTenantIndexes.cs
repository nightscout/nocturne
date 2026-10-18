using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Drops indexes another index already serves. On linked_records, every read pins tenant_id, so
    /// the tenant-led partial index covers the anti-join, and the canonical id alone narrows a
    /// lookup to its group. The plain tenant indexes go from history-paged tables, whose
    /// ix_&lt;table&gt;_tenant_history leads with tenant_id unfiltered. Every statement goes through
    /// <see cref="ConcurrentIndexBuilder"/>, so linked_records stays writable and an interrupted run
    /// can be repeated.
    /// </summary>
    public partial class DropRedundantLinkedRecordAndTenantIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_linked_records_non_primary_record");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_linked_records_type_canonical_primary");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "\"IX_aps_snapshots_tenant_id\"");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "\"IX_meter_glucose_tenant_id\"");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "\"IX_calibrations_tenant_id\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_non_primary_record",
                "ON linked_records (record_type, record_id) WHERE NOT is_primary");
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_linked_records_type_canonical_primary",
                "ON linked_records (record_type, canonical_id, is_primary)");
            ConcurrentIndexBuilder.Build(migrationBuilder, "\"IX_aps_snapshots_tenant_id\"", "ON aps_snapshots (tenant_id)");
            ConcurrentIndexBuilder.Build(migrationBuilder, "\"IX_meter_glucose_tenant_id\"", "ON meter_glucose (tenant_id)");
            ConcurrentIndexBuilder.Build(migrationBuilder, "\"IX_calibrations_tenant_id\"", "ON calibrations (tenant_id)");
        }
    }
}
