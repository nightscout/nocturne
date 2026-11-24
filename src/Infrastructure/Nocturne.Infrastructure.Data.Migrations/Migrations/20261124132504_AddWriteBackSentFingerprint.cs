using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWriteBackSentFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "temp_basals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "profile_activations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "notes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "device_events",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "carb_intakes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "boluses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "bolus_calculations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "write_back_sent_fingerprint",
                table: "bg_checks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "temp_basals");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "profile_activations");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "device_events");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "carb_intakes");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "boluses");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "bolus_calculations");

            migrationBuilder.DropColumn(
                name: "write_back_sent_fingerprint",
                table: "bg_checks");
        }
    }
}
