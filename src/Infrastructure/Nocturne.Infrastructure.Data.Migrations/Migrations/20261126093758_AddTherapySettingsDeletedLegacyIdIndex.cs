using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds the deleted-row legacy-id index on <c>therapy_settings</c> through
    /// <see cref="ConcurrentIndexBuilder"/>. See there for why CONCURRENTLY, and for what an interrupted
    /// build leaves behind. Why it exists is at its declaration in
    /// <c>ModelConventions.ConfigureSharedRecordIndexes</c>.
    /// </summary>
    public partial class AddTherapySettingsDeletedLegacyIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Build(
                migrationBuilder,
                "ix_therapy_settings_tenant_legacy_id_deleted",
                "ON therapy_settings (tenant_id, legacy_id) WHERE legacy_id IS NOT NULL AND deleted_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_therapy_settings_tenant_legacy_id_deleted");
        }
    }
}
