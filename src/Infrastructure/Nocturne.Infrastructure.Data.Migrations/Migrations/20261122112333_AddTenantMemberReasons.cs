using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantMemberReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant_member_reasons",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_member_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    direct_permissions = table.Column<string>(type: "jsonb", nullable: true),
                    limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    access_ends_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_from_invite_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    sys_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_member_reasons", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_member_reasons_member_invites_created_from_invite_id",
                        column: x => x.created_from_invite_id,
                        principalTable: "member_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tenant_member_reasons_tenant_members_tenant_member_id",
                        column: x => x.tenant_member_id,
                        principalTable: "tenant_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_member_reason_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_member_reason_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_member_reason_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_tenant_member_reason_roles_tenant_member_reasons_tenant_mem~",
                        column: x => x.tenant_member_reason_id,
                        principalTable: "tenant_member_reasons",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_member_reason_roles_tenant_roles_tenant_role_id",
                        column: x => x.tenant_role_id,
                        principalTable: "tenant_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_reason_roles_tenant_member_reason_id_tenant_r~",
                table: "tenant_member_reason_roles",
                columns: new[] { "tenant_member_reason_id", "tenant_role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_reason_roles_tenant_role_id",
                table: "tenant_member_reason_roles",
                column: "tenant_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_member_reasons_created_from_invite_id",
                table: "tenant_member_reasons",
                column: "created_from_invite_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_member_reasons_one_direct",
                table: "tenant_member_reasons",
                column: "tenant_member_id",
                unique: true,
                filter: "kind = 'direct'");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_member_reasons_one_per_source",
                table: "tenant_member_reasons",
                columns: new[] { "tenant_member_id", "kind", "source_id" },
                unique: true,
                filter: "source_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_member_reasons_tenant_member_id",
                table: "tenant_member_reasons",
                column: "tenant_member_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_member_reason_roles");

            migrationBuilder.DropTable(
                name: "tenant_member_reasons");
        }
    }
}
