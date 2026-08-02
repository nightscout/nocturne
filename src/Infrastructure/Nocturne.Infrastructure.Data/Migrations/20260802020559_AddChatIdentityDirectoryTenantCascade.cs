using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatIdentityDirectoryTenantCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The absence of this FK is exactly what let rows outlive their tenant, so any
            // instance that has ever deleted a tenant may hold orphans — and AddForeignKey would
            // fail on them. Drop them first: a directory row whose tenant is gone can route
            // nowhere, and keeping it would preserve the chat-platform id and the old tenant's
            // slug/display name that this cascade exists to remove.
            migrationBuilder.Sql(
                """
                DELETE FROM chat_identity_directory d
                WHERE NOT EXISTS (SELECT 1 FROM tenants t WHERE t.id = d.tenant_id);
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_chat_identity_directory_tenants_tenant_id",
                table: "chat_identity_directory",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_identity_directory_tenants_tenant_id",
                table: "chat_identity_directory");
        }
    }
}
