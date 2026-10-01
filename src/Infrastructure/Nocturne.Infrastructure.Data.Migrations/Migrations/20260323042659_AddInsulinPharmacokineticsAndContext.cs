using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInsulinPharmacokineticsAndContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "insulin_context",
                table: "treatments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "concentration",
                table: "patient_insulins",
                type: "integer",
                nullable: false,
                defaultValue: 100);

            migrationBuilder.AddColumn<string>(
                name: "curve",
                table: "patient_insulins",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "rapid-acting");

            migrationBuilder.AddColumn<double>(
                name: "dia",
                table: "patient_insulins",
                type: "double precision",
                nullable: false,
                defaultValue: 4.0);

            migrationBuilder.AddColumn<string>(
                name: "formulation_id",
                table: "patient_insulins",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "patient_insulins",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "peak",
                table: "patient_insulins",
                type: "integer",
                nullable: false,
                defaultValue: 75);

            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "patient_insulins",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Both");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "insulin_context",
                table: "treatments");

            migrationBuilder.DropColumn(
                name: "concentration",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "curve",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "dia",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "formulation_id",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "peak",
                table: "patient_insulins");

            migrationBuilder.DropColumn(
                name: "role",
                table: "patient_insulins");
        }
    }
}
