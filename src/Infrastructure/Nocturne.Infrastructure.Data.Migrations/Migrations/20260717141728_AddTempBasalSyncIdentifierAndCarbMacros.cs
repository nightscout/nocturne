using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTempBasalSyncIdentifierAndCarbMacros : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "sync_identifier",
                table: "temp_basals",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "fat_grams",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "protein_grams",
                table: "carb_intakes",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_temp_basals_tenant_source_sync_id",
                table: "temp_basals",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL AND deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_temp_basals_tenant_source_sync_id",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "sync_identifier",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "fat_grams",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "protein_grams",
                table: "carb_intakes");
        }
    }
}
