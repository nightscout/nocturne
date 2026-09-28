using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTreatmentFoodBreakdown : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "treatment_foods",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    treatment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    food_id = table.Column<Guid>(type: "uuid", nullable: true),
                    portions = table.Column<decimal>(type: "numeric", nullable: false),
                    carbs = table.Column<decimal>(type: "numeric", nullable: false),
                    time_offset_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_treatment_foods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_treatment_foods_foods_food_id",
                        column: x => x.food_id,
                        principalTable: "foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_treatment_foods_treatments_treatment_id",
                        column: x => x.treatment_id,
                        principalTable: "treatments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_food_favorites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    food_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_food_favorites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_food_favorites_foods_food_id",
                        column: x => x.food_id,
                        principalTable: "foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_treatment_foods_food_id",
                table: "treatment_foods",
                column: "food_id");

            migrationBuilder.CreateIndex(
                name: "ix_treatment_foods_sys_created_at",
                table: "treatment_foods",
                column: "sys_created_at");

            migrationBuilder.CreateIndex(
                name: "ix_treatment_foods_treatment_id",
                table: "treatment_foods",
                column: "treatment_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_food_favorites_food_id",
                table: "user_food_favorites",
                column: "food_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_food_favorites_user_food",
                table: "user_food_favorites",
                columns: new[] { "user_id", "food_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_food_favorites_user_id",
                table: "user_food_favorites",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "treatment_foods");

            migrationBuilder.DropTable(
                name: "user_food_favorites");
        }
    }
}
