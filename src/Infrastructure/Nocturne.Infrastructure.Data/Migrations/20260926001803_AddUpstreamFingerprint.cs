using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUpstreamFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "temp_basals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "device_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "carb_intakes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "boluses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "bolus_calculations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream_fingerprint",
                table: "bg_checks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "upstream_fingerprint",
                table: "bg_checks");
        }
    }
}
