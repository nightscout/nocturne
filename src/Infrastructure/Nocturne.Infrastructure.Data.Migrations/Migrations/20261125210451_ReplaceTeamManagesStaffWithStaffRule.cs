using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceTeamManagesStaffWithStaffRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "staff_rule",
                table: "team_tenant_access",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "team_decides");

            migrationBuilder.Sql("""
                UPDATE team_tenant_access
                   SET staff_rule = CASE WHEN team_manages_staff THEN 'team_decides' ELSE 'owner_approves' END;
                """);

            migrationBuilder.DropColumn(
                name: "team_manages_staff",
                table: "team_tenant_access");

            migrationBuilder.AddCheckConstraint(
                name: "ck_team_tenant_access_staff_rule",
                table: "team_tenant_access",
                sql: "staff_rule IN ('owner_approves', 'team_decides', 'team_trusted')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_team_tenant_access_staff_rule",
                table: "team_tenant_access");

            migrationBuilder.AddColumn<bool>(
                name: "team_manages_staff",
                table: "team_tenant_access",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("""
                UPDATE team_tenant_access SET team_manages_staff = staff_rule <> 'owner_approves';
                """);

            migrationBuilder.DropColumn(
                name: "staff_rule",
                table: "team_tenant_access");
        }
    }
}
