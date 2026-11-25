using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamMembershipReasonSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_reasons_source_id",
                table: "tenant_member_reasons",
                column: "source_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_member_reasons_team_source",
                table: "tenant_member_reasons",
                sql: "(kind = 'team') = (source_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_member_reasons_team_tenant_access_source_id",
                table: "tenant_member_reasons",
                column: "source_id",
                principalTable: "team_tenant_access",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenant_member_reasons_team_tenant_access_source_id",
                table: "tenant_member_reasons");

            migrationBuilder.DropIndex(
                name: "IX_tenant_member_reasons_source_id",
                table: "tenant_member_reasons");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_member_reasons_team_source",
                table: "tenant_member_reasons");
        }
    }
}
