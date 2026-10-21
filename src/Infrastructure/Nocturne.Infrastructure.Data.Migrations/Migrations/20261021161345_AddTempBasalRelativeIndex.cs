using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes the percent temp basals by tenant and start, the window a schedule or activation write
    /// re-resolves (TempBasalRateReresolver). Few rows carry a percent, so the partial index is small.
    /// </summary>
    public partial class AddTempBasalRelativeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_temp_basals_relative",
                "ON temp_basals (tenant_id, start_timestamp) WHERE percent IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_temp_basals_relative");
        }
    }
}
