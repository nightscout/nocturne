using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDndWindowsActivePartialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dnd_windows_tenant_id_scope",
                table: "dnd_windows");

            migrationBuilder.CreateIndex(
                name: "IX_dnd_windows_tenant_id_scope",
                table: "dnd_windows",
                columns: new[] { "tenant_id", "scope" },
                filter: "cleared_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dnd_windows_tenant_id_scope",
                table: "dnd_windows");

            migrationBuilder.CreateIndex(
                name: "IX_dnd_windows_tenant_id_scope",
                table: "dnd_windows",
                columns: new[] { "tenant_id", "scope" });
        }
    }
}
