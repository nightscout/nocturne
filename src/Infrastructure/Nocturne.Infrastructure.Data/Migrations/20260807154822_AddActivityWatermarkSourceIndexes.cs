using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityWatermarkSourceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_state_spans_tenant_id",
                table: "state_spans");

            migrationBuilder.CreateIndex(
                name: "ix_step_counts_tenant_source_timestamp",
                table: "step_counts",
                columns: new[] { "tenant_id", "data_source", "timestamp" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_tenant_source_category_start",
                table: "state_spans",
                columns: new[] { "tenant_id", "source", "category", "start_timestamp" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_heart_rates_tenant_source_timestamp",
                table: "heart_rates",
                columns: new[] { "tenant_id", "data_source", "timestamp" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_step_counts_tenant_source_timestamp",
                table: "step_counts");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_tenant_source_category_start",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_heart_rates_tenant_source_timestamp",
                table: "heart_rates");

            migrationBuilder.CreateIndex(
                name: "IX_state_spans_tenant_id",
                table: "state_spans",
                column: "tenant_id");
        }
    }
}
