using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoTenantSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_demo",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "tenant_demo_config",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    next_reset_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_reset_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    access_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    backfill_days = table.Column<int>(type: "integer", nullable: false),
                    interval_minutes = table.Column<int>(type: "integer", nullable: false),
                    reset_interval_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_demo_config", x => x.tenant_id);
                    table.ForeignKey(
                        name: "FK_tenant_demo_config_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_demo_config");

            migrationBuilder.DropColumn(
                name: "is_demo",
                table: "tenants");
        }
    }
}
