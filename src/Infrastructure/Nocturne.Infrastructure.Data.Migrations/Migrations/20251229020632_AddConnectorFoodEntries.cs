using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorFoodEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "foods",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_source",
                table: "foods",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "connector_food_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    connector_source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    external_entry_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    external_food_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    food_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    logged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    meal_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    carbs = table.Column<decimal>(type: "numeric", nullable: false),
                    protein = table.Column<decimal>(type: "numeric", nullable: false),
                    fat = table.Column<decimal>(type: "numeric", nullable: false),
                    energy = table.Column<decimal>(type: "numeric", nullable: false),
                    servings = table.Column<decimal>(type: "numeric", nullable: false),
                    serving_description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    matched_treatment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_food_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_connector_food_entries_foods_food_id",
                        column: x => x.food_id,
                        principalTable: "foods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_connector_food_entries_treatments_matched_treatment_id",
                        column: x => x.matched_treatment_id,
                        principalTable: "treatments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_foods_external_source_id",
                table: "foods",
                columns: new[] { "external_source", "external_id" },
                unique: true,
                filter: "external_source IS NOT NULL AND external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_consumed_at",
                table: "connector_food_entries",
                column: "consumed_at");

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_external_entry_id",
                table: "connector_food_entries",
                column: "external_entry_id");

            migrationBuilder.CreateIndex(
                name: "IX_connector_food_entries_food_id",
                table: "connector_food_entries",
                column: "food_id");

            migrationBuilder.CreateIndex(
                name: "IX_connector_food_entries_matched_treatment_id",
                table: "connector_food_entries",
                column: "matched_treatment_id");

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_source",
                table: "connector_food_entries",
                column: "connector_source");

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_source_entry",
                table: "connector_food_entries",
                columns: new[] { "connector_source", "external_entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_status",
                table: "connector_food_entries",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_connector_food_entries_sys_created_at",
                table: "connector_food_entries",
                column: "sys_created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connector_food_entries");

            migrationBuilder.DropIndex(
                name: "ix_foods_external_source_id",
                table: "foods");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "foods");

            migrationBuilder.DropColumn(
                name: "external_source",
                table: "foods");
        }
    }
}
