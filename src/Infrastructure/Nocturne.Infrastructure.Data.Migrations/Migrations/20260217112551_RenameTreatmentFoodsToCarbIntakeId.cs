using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTreatmentFoodsToCarbIntakeId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_treatment_foods_treatments_treatment_id",
                table: "treatment_foods");

            migrationBuilder.RenameColumn(
                name: "treatment_id",
                table: "treatment_foods",
                newName: "carb_intake_id");

            migrationBuilder.RenameIndex(
                name: "ix_treatment_foods_treatment_id",
                table: "treatment_foods",
                newName: "ix_treatment_foods_carb_intake_id");

            migrationBuilder.AddForeignKey(
                name: "FK_treatment_foods_carb_intakes_carb_intake_id",
                table: "treatment_foods",
                column: "carb_intake_id",
                principalTable: "carb_intakes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_treatment_foods_carb_intakes_carb_intake_id",
                table: "treatment_foods");

            migrationBuilder.RenameColumn(
                name: "carb_intake_id",
                table: "treatment_foods",
                newName: "treatment_id");

            migrationBuilder.RenameIndex(
                name: "ix_treatment_foods_carb_intake_id",
                table: "treatment_foods",
                newName: "ix_treatment_foods_treatment_id");

            migrationBuilder.AddForeignKey(
                name: "FK_treatment_foods_treatments_treatment_id",
                table: "treatment_foods",
                column: "treatment_id",
                principalTable: "treatments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
