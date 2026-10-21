using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes the percent temp basals whose rate follows the schedule, by tenant and start: the window
    /// a schedule or activation change re-resolves (TempBasalRateReresolver), and its existence probe.
    /// Few rows qualify, so the partial index is small.
    /// </summary>
    public partial class AddTempBasalRelativeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_temp_basals_relative",
                "ON temp_basals (tenant_id, start_timestamp) WHERE percent IS NOT NULL AND deleted_at IS NULL AND (rate IS NULL OR rate_from_schedule)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_temp_basals_relative");
        }
    }
}
