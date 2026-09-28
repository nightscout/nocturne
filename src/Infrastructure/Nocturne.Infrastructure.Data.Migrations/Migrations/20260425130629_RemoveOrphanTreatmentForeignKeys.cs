using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOrphanTreatmentForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "source_treatment_id",
                table: "decomposition_batches");

            migrationBuilder.DropColumn(
                name: "matched_treatment_id",
                table: "connector_food_entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_treatment_id",
                table: "decomposition_batches",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "matched_treatment_id",
                table: "connector_food_entries",
                type: "uuid",
                nullable: true);
        }
    }
}
