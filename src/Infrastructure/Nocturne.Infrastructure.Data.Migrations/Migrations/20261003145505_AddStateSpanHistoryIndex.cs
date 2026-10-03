using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the history-page index the v3 treatments history read pages the state spans of the legacy
    /// treatment projection through, via <see cref="ConcurrentIndexBuilder"/>. Why it exists is at its
    /// declaration in <c>NocturneDbContext</c>.
    /// </summary>
    public partial class AddStateSpanHistoryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_state_spans_tenant_category_updated_at",
                "ON state_spans (tenant_id, category, updated_at, id) WHERE deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_state_spans_tenant_category_updated_at");
        }
    }
}
