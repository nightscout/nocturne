using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateChatIdentityIntoDirectory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_identity_links");

            migrationBuilder.CreateTable(
                name: "chat_identity_directory",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    platform_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    platform_channel_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nocturne_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    display_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_identity_directory", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "chat_identity_pending_links",
                columns: table => new
                {
                    token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    platform_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_identity_pending_links", x => x.token);
                });

            migrationBuilder.CreateIndex(
                name: "ix_directory_tenant_id",
                table: "chat_identity_directory",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ux_directory_user_label",
                table: "chat_identity_directory",
                columns: new[] { "platform", "platform_user_id", "label" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_directory_user_one_default",
                table: "chat_identity_directory",
                columns: new[] { "platform", "platform_user_id" },
                unique: true,
                filter: "is_default = true");

            migrationBuilder.CreateIndex(
                name: "ux_directory_user_tenant",
                table: "chat_identity_directory",
                columns: new[] { "platform", "platform_user_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pending_links_expires_at",
                table: "chat_identity_pending_links",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_identity_directory");

            migrationBuilder.DropTable(
                name: "chat_identity_pending_links");

            migrationBuilder.CreateTable(
                name: "chat_identity_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    display_unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    nocturne_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    platform_channel_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    platform_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_identity_links", x => x.Id);
                });
        }
    }
}
