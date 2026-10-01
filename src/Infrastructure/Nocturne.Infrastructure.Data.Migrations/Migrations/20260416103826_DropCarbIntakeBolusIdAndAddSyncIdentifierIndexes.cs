using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropCarbIntakeBolusIdAndAddSyncIdentifierIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bolus_id",
                table: "carb_intakes");

            migrationBuilder.CreateIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses",
                columns: new[] { "tenant_id", "data_source", "sync_identifier" },
                unique: true,
                filter: "sync_identifier IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_carb_intakes_tenant_source_sync_id",
                table: "carb_intakes");

            migrationBuilder.DropIndex(
                name: "ix_boluses_tenant_source_sync_id",
                table: "boluses");

            migrationBuilder.AddColumn<Guid>(
                name: "bolus_id",
                table: "carb_intakes",
                type: "uuid",
                nullable: true);
        }
    }
}
