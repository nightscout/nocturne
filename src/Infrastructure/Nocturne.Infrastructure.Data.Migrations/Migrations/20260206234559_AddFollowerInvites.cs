using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFollowerInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "follower_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValue: new List<string>()),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: true),
                    use_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
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

            migrationBuilder.CreateTable(
                name: "oauth_clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    is_known = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    redirect_uris = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "oauth_authorization_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValue: new List<string>()),
                    redirect_uri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    code_challenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_authorization_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_authorization_codes_oauth_clients_client_entity_id",
                        column: x => x.client_entity_id,
                        principalTable: "oauth_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_authorization_codes_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "app"),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValue: new List<string>()),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_used_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    last_used_user_agent = table.Column<string>(type: "text", nullable: true),
                    follower_subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_grants_oauth_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "oauth_clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_grants_subjects_follower_subject_id",
                        column: x => x.follower_subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_oauth_grants_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "oauth_device_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    device_code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scopes = table.Column<List<string>>(type: "text[]", nullable: false, defaultValue: new List<string>()),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    denied_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    interval = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    last_polled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_device_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_device_codes_oauth_grants_grant_id",
                        column: x => x.grant_id,
                        principalTable: "oauth_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "oauth_refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    grant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_oauth_refresh_tokens_oauth_grants_grant_id",
                        column: x => x.grant_id,
                        principalTable: "oauth_grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_oauth_refresh_tokens_oauth_refresh_tokens_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalTable: "oauth_refresh_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_follower_invites_owner_subject_id",
                table: "follower_invites",
                column: "owner_subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_follower_invites_token_hash",
                table: "follower_invites",
                column: "token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_authorization_codes_client_entity_id",
                table: "oauth_authorization_codes",
                column: "client_entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_codes_code_hash",
                table: "oauth_authorization_codes",
                column: "code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_codes_expires_at",
                table: "oauth_authorization_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_authorization_codes_subject_id",
                table: "oauth_authorization_codes",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_client_id",
                table: "oauth_clients",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_device_codes_device_code_hash",
                table: "oauth_device_codes",
                column: "device_code_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_device_codes_expires_at",
                table: "oauth_device_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_device_codes_grant_id",
                table: "oauth_device_codes",
                column: "grant_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_device_codes_user_code",
                table: "oauth_device_codes",
                column: "user_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_client_id",
                table: "oauth_grants",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_client_subject",
                table: "oauth_grants",
                columns: new[] { "client_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_follower_subject_id",
                table: "oauth_grants",
                column: "follower_subject_id",
                filter: "follower_subject_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_revoked_at",
                table: "oauth_grants",
                column: "revoked_at",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_subject_follower",
                table: "oauth_grants",
                columns: new[] { "subject_id", "follower_subject_id" },
                unique: true,
                filter: "follower_subject_id IS NOT NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_subject_id",
                table: "oauth_grants",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_expires_at",
                table: "oauth_refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_grant_id",
                table: "oauth_refresh_tokens",
                column: "grant_id");

            migrationBuilder.CreateIndex(
                name: "IX_oauth_refresh_tokens_replaced_by_id",
                table: "oauth_refresh_tokens",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_revoked_at",
                table: "oauth_refresh_tokens",
                column: "revoked_at",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_refresh_tokens_token_hash",
                table: "oauth_refresh_tokens",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "follower_invites");

            migrationBuilder.DropTable(
                name: "oauth_authorization_codes");

            migrationBuilder.DropTable(
                name: "oauth_device_codes");

            migrationBuilder.DropTable(
                name: "oauth_refresh_tokens");

            migrationBuilder.DropTable(
                name: "oauth_grants");

            migrationBuilder.DropTable(
                name: "oauth_clients");
        }
    }
}
