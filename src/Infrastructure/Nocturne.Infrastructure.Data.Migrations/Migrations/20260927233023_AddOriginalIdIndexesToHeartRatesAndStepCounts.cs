using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Indexes the activity id heart rates and step counts were decomposed from, which every
    /// activity write and connector reconcile looks up, soft-deleted rows included. Built through
    /// <see cref="ConcurrentIndexBuilder"/>, whose remarks cover why CONCURRENTLY and what an
    /// interrupted build leaves behind. Non-unique, so no loser cleanup is needed.
    /// </summary>
    public partial class AddOriginalIdIndexesToHeartRatesAndStepCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_step_counts_tenant_original_id",
                "ON step_counts (tenant_id, original_id) WHERE original_id IS NOT NULL");

            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_heart_rates_tenant_original_id",
                "ON heart_rates (tenant_id, original_id) WHERE original_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_step_counts_tenant_original_id");
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_heart_rates_tenant_original_id");
        }
    }
}
