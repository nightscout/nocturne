using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Rebuilds the heart rate and step count watermark indexes with the <c>deleted_at IS NULL</c> filter the other
    /// record tables carry, through <see cref="ConcurrentIndexBuilder"/>. The filter keeps the watermark read
    /// index-only; the reason is at the loop in <c>ModelConventions.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class FilterHealthWatermarkIndexes : Migration
    {
        private static readonly string[] Tables = ["heart_rates", "step_counts"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_source_timestamp");
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_source_timestamp",
                    $"ON {table} (tenant_id, data_source, timestamp DESC) WHERE deleted_at IS NULL");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                ConcurrentIndexBuilder.Drop(migrationBuilder, $"ix_{table}_tenant_source_timestamp");
                ConcurrentIndexBuilder.Build(
                    migrationBuilder,
                    $"ix_{table}_tenant_source_timestamp",
                    $"ON {table} (tenant_id, data_source, timestamp DESC)");
            }
        }
    }
}
