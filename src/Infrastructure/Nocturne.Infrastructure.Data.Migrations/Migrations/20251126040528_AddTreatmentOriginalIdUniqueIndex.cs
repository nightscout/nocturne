using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTreatmentOriginalIdUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create unique index on original_id (partial index - only where original_id IS NOT NULL)
            // This prevents duplicate MongoDB ObjectIds when migrating from MongoDB
            migrationBuilder.CreateIndex(
                name: "ix_treatments_original_id_unique",
                table: "treatments",
                column: "original_id",
                unique: true,
                filter: "original_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_treatments_original_id_unique",
                table: "treatments");
        }
    }
}
