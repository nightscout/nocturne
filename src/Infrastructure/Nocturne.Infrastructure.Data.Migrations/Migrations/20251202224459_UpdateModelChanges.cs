using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oidc_providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    issuer_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    client_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    client_secret_encrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    discovery_document = table.Column<string>(type: "jsonb", nullable: true),
                    discovery_cached_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[\"openid\",\"profile\",\"email\"]"),
                    claim_mappings = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    default_roles = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[\"readable\"]"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    display_order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    icon = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    button_color = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_providers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    permissions = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    notes = table.Column<string>(type: "text", nullable: true),
                    is_system_role = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "subjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    access_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    access_token_prefix = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    oidc_subject_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    oidc_issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    original_id = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subjects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    oidc_session_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subject_roles",
                columns: table => new
                {
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    assigned_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subject_roles", x => new { x.subject_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_subject_roles_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_subject_roles_subjects_assigned_by_id",
                        column: x => x.assigned_by_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_subject_roles_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "auth_audit_log",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refresh_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true),
                    success = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auth_audit_log", x => x.Id);
                    table.ForeignKey(
                        name: "FK_auth_audit_log_refresh_tokens_refresh_token_id",
                        column: x => x.refresh_token_id,
                        principalTable: "refresh_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_auth_audit_log_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_created_at",
                table: "auth_audit_log",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_event_type",
                table: "auth_audit_log",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_ip_address",
                table: "auth_audit_log",
                column: "ip_address");

            migrationBuilder.CreateIndex(
                name: "IX_auth_audit_log_refresh_token_id",
                table: "auth_audit_log",
                column: "refresh_token_id");

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_subject_created",
                table: "auth_audit_log",
                columns: new[] { "subject_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_subject_id",
                table: "auth_audit_log",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_oidc_providers_is_enabled",
                table: "oidc_providers",
                column: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_oidc_providers_issuer_url",
                table: "oidc_providers",
                column: "issuer_url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_oidc_session_id",
                table: "refresh_tokens",
                column: "oidc_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_revoked_at",
                table: "refresh_tokens",
                column: "revoked_at",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_subject_id",
                table: "refresh_tokens",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subject_roles_assigned_by_id",
                table: "subject_roles",
                column: "assigned_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_subject_roles_role_id",
                table: "subject_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_subjects_access_token_hash",
                table: "subjects",
                column: "access_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_email",
                table: "subjects",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "ix_subjects_name",
                table: "subjects",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "ix_subjects_oidc_identity",
                table: "subjects",
                columns: new[] { "oidc_subject_id", "oidc_issuer" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_audit_log");

            migrationBuilder.DropTable(
                name: "oidc_providers");

            migrationBuilder.DropTable(
                name: "subject_roles");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "subjects");
        }
    }
}
