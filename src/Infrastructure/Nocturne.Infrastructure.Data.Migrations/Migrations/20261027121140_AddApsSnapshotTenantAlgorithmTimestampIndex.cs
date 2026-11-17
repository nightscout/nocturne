using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes a tenant's snapshots by algorithm and time, for the end bound of
    /// <c>ApsSnapshotRepository.StampLoopClosedLoopAsync</c>. See <c>NocturneDbContext</c> for why.
    /// </summary>
    public partial class AddApsSnapshotTenantAlgorithmTimestampIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_aps_snapshots_tenant_algorithm_timestamp",
                "ON aps_snapshots (tenant_id, aps_system, \"timestamp\" DESC) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_aps_snapshots_tenant_algorithm_timestamp");
        }
    }
}
