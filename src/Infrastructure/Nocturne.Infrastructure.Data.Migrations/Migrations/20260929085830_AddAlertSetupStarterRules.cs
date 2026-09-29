using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertSetupStarterRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "starter_kind",
                table: "alert_rules",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "receipt_confirmed_at",
                table: "alert_instances",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "starter_kind",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "receipt_confirmed_at",
                table: "alert_instances");
        }
    }
}
