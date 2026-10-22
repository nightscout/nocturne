using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginCodePurpose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every code minted before this column existed was a login code.
            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "login_codes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Login");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "purpose",
                table: "login_codes");
        }
    }
}
