using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdditionalPropertiesColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "treatments",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "settings",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "profiles",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "foods",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "entries",
                type: "jsonb",
                nullable: true
            );

            migrationBuilder.AlterColumn<int>(
                name: "contact_type",
                table: "emergency_contacts",
                type: "integer",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50
            );

            migrationBuilder.AddColumn<string>(
                name: "additional_properties",
                table: "devicestatus",
                type: "jsonb",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "additional_properties", table: "treatments");

            migrationBuilder.DropColumn(name: "additional_properties", table: "settings");

            migrationBuilder.DropColumn(name: "additional_properties", table: "profiles");

            migrationBuilder.DropColumn(name: "additional_properties", table: "foods");

            migrationBuilder.DropColumn(name: "additional_properties", table: "entries");

            migrationBuilder.DropColumn(name: "additional_properties", table: "devicestatus");

            migrationBuilder.AlterColumn<string>(
                name: "contact_type",
                table: "emergency_contacts",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer"
            );
        }
    }
}
