using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeDevicesUniqueIndexTenantScoped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_category_type_serial",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_devices_tenant_id",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "ix_devices_category_type_serial",
                table: "devices",
                columns: new[] { "tenant_id", "category", "type", "serial" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_devices_category_type_serial",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "ix_devices_category_type_serial",
                table: "devices",
                columns: new[] { "category", "type", "serial" },
                unique: true,
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_tenant_id",
                table: "devices",
                column: "tenant_id");
        }
    }
}
