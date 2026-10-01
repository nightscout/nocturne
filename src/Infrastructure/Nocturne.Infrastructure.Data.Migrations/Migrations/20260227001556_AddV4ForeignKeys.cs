using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddV4ForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "aps_snapshot_id",
                table: "temp_basals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "bolus_id",
                table: "carb_intakes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "aps_snapshot_id",
                table: "boluses",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "bolus_calculation_id",
                table: "boluses",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "aps_snapshot_id",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "bolus_id",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "aps_snapshot_id",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "bolus_calculation_id",
                table: "boluses");
        }
    }
}
