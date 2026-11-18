using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes a tenant's uploader snapshots by type and time, for
    /// <c>UploaderSnapshotRepository.GetLatestOfTypeAsync</c>. See
    /// <c>UploaderSnapshotEntityConfiguration</c> for why.
    /// </summary>
    public partial class AddUploaderSnapshotTenantTypeTimestampIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_uploader_snapshots_tenant_type_timestamp",
                "ON uploader_snapshots (tenant_id, type, \"timestamp\" DESC) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_uploader_snapshots_tenant_type_timestamp");
        }
    }
}
