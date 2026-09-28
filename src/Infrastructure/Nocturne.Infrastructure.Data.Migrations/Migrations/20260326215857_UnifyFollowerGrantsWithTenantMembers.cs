using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnifyFollowerGrantsWithTenantMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_follower_invites_created_from_invite_id",
                table: "oauth_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_subjects_follower_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropTable(
                name: "follower_invites");

            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members");

            migrationBuilder.DropIndex(
                name: "IX_oauth_grants_created_from_invite_id",
                table: "oauth_grants");

            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_follower_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_subject_follower",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "created_from_invite_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "follower_subject_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "oauth_grants");

            migrationBuilder.AddColumn<Guid>(
                name: "created_from_invite_id",
                table: "tenant_members",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "tenant_members",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_used_at",
                table: "tenant_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_used_ip",
                table: "tenant_members",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_used_user_agent",
                table: "tenant_members",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "tenant_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "revoked_at",
                table: "tenant_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scopes",
                table: "tenant_members",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "member_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scopes = table.Column<string>(type: "jsonb", nullable: true),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_member_invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_member_invites_subjects_created_by_subject_id",
                        column: x => x.created_by_subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_member_invites_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_members_created_from_invite_id",
                table: "tenant_members",
                column: "created_from_invite_id");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_member_invites_created_by_subject_id",
                table: "member_invites",
                column: "created_by_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_invites_tenant_id",
                table: "member_invites",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_member_invites_token_hash",
                table: "member_invites",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_members_member_invites_created_from_invite_id",
                table: "tenant_members",
                column: "created_from_invite_id",
                principalTable: "member_invites",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenant_members_member_invites_created_from_invite_id",
                table: "tenant_members");

            migrationBuilder.DropTable(
                name: "member_invites");

            migrationBuilder.DropIndex(
                name: "IX_tenant_members_created_from_invite_id",
                table: "tenant_members");

            migrationBuilder.DropIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "created_from_invite_id",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "label",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "last_used_ip",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "last_used_user_agent",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "revoked_at",
                table: "tenant_members");

            migrationBuilder.DropColumn(
                name: "scopes",
                table: "tenant_members");

            migrationBuilder.AddColumn<Guid>(
                name: "created_from_invite_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "follower_subject_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "oauth_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "follower_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    limit_to_24_hours = table.Column<bool>(type: "boolean", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    use_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_follower_invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_follower_invites_subjects_owner_subject_id",
                        column: x => x.owner_subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_members_tenant_subject",
                table: "tenant_members",
                columns: new[] { "tenant_id", "subject_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_oauth_grants_created_from_invite_id",
                table: "oauth_grants",
                column: "created_from_invite_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_follower_subject_id",
                table: "oauth_grants",
                column: "follower_subject_id",
                filter: "follower_subject_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_subject_follower",
                table: "oauth_grants",
                columns: new[] { "subject_id", "follower_subject_id" },
                unique: true,
                filter: "follower_subject_id IS NOT NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_follower_invites_owner_subject_id",
                table: "follower_invites",
                column: "owner_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_follower_invites_token_hash",
                table: "follower_invites",
                column: "token_hash");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_follower_invites_created_from_invite_id",
                table: "oauth_grants",
                column: "created_from_invite_id",
                principalTable: "follower_invites",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_subjects_follower_subject_id",
                table: "oauth_grants",
                column: "follower_subject_id",
                principalTable: "subjects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
