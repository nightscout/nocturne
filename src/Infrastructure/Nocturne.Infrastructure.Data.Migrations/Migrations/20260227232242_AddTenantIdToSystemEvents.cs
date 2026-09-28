using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToSystemEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "system_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Backfill existing rows with the default tenant
            migrationBuilder.Sql(
                "UPDATE system_events SET tenant_id = (SELECT \"Id\" FROM tenants WHERE is_default = true) WHERE tenant_id = '00000000-0000-0000-0000-000000000000'");

            migrationBuilder.CreateIndex(
                name: "IX_system_events_tenant_id",
                table: "system_events",
                column: "tenant_id");

            migrationBuilder.AddForeignKey(
                name: "FK_system_events_tenants_tenant_id",
                table: "system_events",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_system_events_tenants_tenant_id",
                table: "system_events");

            migrationBuilder.DropIndex(
                name: "IX_system_events_tenant_id",
                table: "system_events");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "system_events");
        }
    }
}
