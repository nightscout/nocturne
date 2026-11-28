using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Gives each demo tenant a kind, the existing one being the host, and the host the demo clinic's team. The index
    /// goes through <see cref="ConcurrentIndexBuilder"/>.
    /// </summary>
    public partial class AddDemoTenantKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "clinic_team_id",
                table: "tenant_demo_config",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "tenant_demo_config",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "host");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_demo_config_kind",
                table: "tenant_demo_config",
                sql: "kind IN ('host', 'person')");

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_demo_config_teams_clinic_team_id",
                table: "tenant_demo_config",
                column: "clinic_team_id",
                principalTable: "teams",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            ConcurrentIndexBuilder.Build(
                migrationBuilder, "ix_tenant_demo_config_clinic_team_id", "ON tenant_demo_config (clinic_team_id)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ConcurrentIndexBuilder.Drop(migrationBuilder, "ix_tenant_demo_config_clinic_team_id");

            migrationBuilder.DropForeignKey(
                name: "FK_tenant_demo_config_teams_clinic_team_id",
                table: "tenant_demo_config");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_demo_config_kind",
                table: "tenant_demo_config");

            migrationBuilder.DropColumn(
                name: "clinic_team_id",
                table: "tenant_demo_config");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "tenant_demo_config");
        }
    }
}
