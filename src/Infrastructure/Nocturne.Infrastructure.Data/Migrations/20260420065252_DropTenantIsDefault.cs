using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropTenantIsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE tenants DROP COLUMN IF EXISTS is_default;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
