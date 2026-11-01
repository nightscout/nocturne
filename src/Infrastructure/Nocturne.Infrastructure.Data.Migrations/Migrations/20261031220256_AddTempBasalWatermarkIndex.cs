using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the watermark index <c>temp_basals</c> lacked, through <see cref="ConcurrentIndexBuilder"/>.
    /// </summary>
    public partial class AddTempBasalWatermarkIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_temp_basals_tenant_source_timestamp",
                "ON temp_basals (tenant_id, data_source, start_timestamp DESC) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_temp_basals_tenant_source_timestamp");
        }
    }
}
