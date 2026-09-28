using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantMemberUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "tenant_members",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members",
                columns: new[] { "tenant_id", "username" },
                unique: true,
                filter: "username IS NOT NULL AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_username",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "username",
                table: "tenant_members");
        }
    }
}
