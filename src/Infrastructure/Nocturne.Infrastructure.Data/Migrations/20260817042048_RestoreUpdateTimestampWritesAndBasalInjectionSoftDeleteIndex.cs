using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestoreUpdateTimestampWritesAndBasalInjectionSoftDeleteIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_basal_injections_tenant_legacy_id",
                table: "basal_injections");

            migrationBuilder.CreateIndex(
                name: "ix_basal_injections_tenant_legacy_id",
                table: "basal_injections",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_basal_injections_tenant_legacy_id",
                table: "basal_injections");

            migrationBuilder.CreateIndex(
                name: "ix_basal_injections_tenant_legacy_id",
                table: "basal_injections",
                columns: new[] { "tenant_id", "legacy_id" },
                unique: true,
                filter: "legacy_id IS NOT NULL");
        }
    }
}
