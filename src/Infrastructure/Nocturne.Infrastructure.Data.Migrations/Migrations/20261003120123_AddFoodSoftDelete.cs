using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Makes foods soft-deletable, so a deleted food reaches the v3 history read with
    /// <c>isValid: false</c>. Kept apart from <see cref="ReportDeletionsInV3History"/>: that one builds
    /// its indexes outside a transaction, and a column added ahead of those builds would commit before
    /// the history row, so an interrupted build would leave every re-run failing on the column.
    /// </summary>
    public partial class AddFoodSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at",
                table: "foods",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "deleted_by_user",
                table: "foods",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Dropping <c>deleted_at</c> alone would bring every soft-deleted food back as live, so the
        /// deleted ones are hard-deleted first, which is what a food delete did before this migration.
        /// The trade-off: their v3 history tombstones are lost, as they are once the soft-delete
        /// retention purges them. The foreign-key side effects already ran when each food was deleted.
        /// FORCE ROW LEVEL SECURITY binds the migrator too, hence the per-tenant GUC.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    t RECORD;
                BEGIN
                    FOR t IN SELECT id FROM tenants LOOP
                        PERFORM set_config('app.current_tenant_id', t.id::text, true);
                        DELETE FROM foods WHERE deleted_at IS NOT NULL;
                    END LOOP;
                END $$;
                """);

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "foods");

            migrationBuilder.DropColumn(
                name: "deleted_by_user",
                table: "foods");
        }
    }
}
