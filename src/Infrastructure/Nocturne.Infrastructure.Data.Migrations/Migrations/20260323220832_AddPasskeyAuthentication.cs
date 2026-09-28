using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasskeyAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants");

            migrationBuilder.AddColumn<string>(
                name: "username",
                table: "subjects",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "client_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "token_hash",
                table: "oauth_grants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "passkey_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credential_id = table.Column<byte[]>(type: "bytea", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    sign_count = table.Column<long>(type: "bigint", nullable: false),
                    transports = table.Column<List<string>>(type: "text[]", nullable: false),
                    label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    aa_guid = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_passkey_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_passkey_credentials_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "recovery_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recovery_codes_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_passkey_credentials_subject_id",
                table: "passkey_credentials",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "IX_passkey_credentials_tenant_id_credential_id",
                table: "passkey_credentials",
                columns: new[] { "tenant_id", "credential_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recovery_codes_subject_id",
                table: "recovery_codes",
                column: "subject_id");

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants",
                column: "client_id",
                principalTable: "oauth_clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants");

            migrationBuilder.DropTable(
                name: "passkey_credentials");

            migrationBuilder.DropTable(
                name: "recovery_codes");

            migrationBuilder.DropColumn(
                name: "username",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "token_hash",
                table: "oauth_grants");

            migrationBuilder.AlterColumn<Guid>(
                name: "client_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    battery_low_threshold = table.Column<int>(type: "integer", nullable: true),
                    calibration_reminder_hours = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    data_gap_warning_minutes = table.Column<int>(type: "integer", nullable: true),
                    email_address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    email_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    emergency_override_quiet_hours = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    push_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pushover_devices = table.Column<string>(type: "jsonb", nullable: true),
                    pushover_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    pushover_user_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    quiet_hours_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    sensor_expiration_warning_hours = table.Column<int>(type: "integer", nullable: true),
                    sms_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sms_phone_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    user_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    webhook_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    webhook_urls = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_email_enabled",
                table: "notification_preferences",
                column: "email_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_pushover_enabled",
                table: "notification_preferences",
                column: "pushover_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_sms_enabled",
                table: "notification_preferences",
                column: "sms_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_notification_preferences_user_id",
                table: "notification_preferences",
                column: "user_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_oauth_grants_oauth_clients_client_id",
                table: "oauth_grants",
                column: "client_id",
                principalTable: "oauth_clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
