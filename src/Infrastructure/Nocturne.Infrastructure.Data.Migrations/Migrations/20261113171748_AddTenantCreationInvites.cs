using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCreationInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "platform_policy",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    allow_owner_slug_change = table.Column<bool>(type: "boolean", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_policy", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_creation_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_creation_invites", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_creation_invites_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_creation_invite_uses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invite_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    redeemed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_creation_invite_uses", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_creation_invite_uses_tenant_creation_invites_invite_~",
                        column: x => x.invite_id,
                        principalTable: "tenant_creation_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_creation_invite_uses_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_creation_invite_uses_invite_id",
                table: "tenant_creation_invite_uses",
                column: "invite_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_creation_invite_uses_tenant_id",
                table: "tenant_creation_invite_uses",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_creation_invites_created_by_subject_id",
                table: "tenant_creation_invites",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_creation_invites_token_hash",
                table: "tenant_creation_invites",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_policy");

            migrationBuilder.DropTable(
                name: "tenant_creation_invite_uses");

            migrationBuilder.DropTable(
                name: "tenant_creation_invites");
        }
    }
}
