using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthAuditActorAndTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_credential",
                table: "auth_audit_log",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "actor_subject_id",
                table: "auth_audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "auth_audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_actor_credential_created",
                table: "auth_audit_log",
                columns: new[] { "actor_credential", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_actor_subject_created",
                table: "auth_audit_log",
                columns: new[] { "actor_subject_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_auth_audit_log_tenant_created",
                table: "auth_audit_log",
                columns: new[] { "tenant_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.AddForeignKey(
                name: "FK_auth_audit_log_subjects_actor_subject_id",
                table: "auth_audit_log",
                column: "actor_subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_auth_audit_log_subjects_actor_subject_id",
                table: "auth_audit_log");

            migrationBuilder.DropIndex(
                name: "ix_auth_audit_log_actor_credential_created",
                table: "auth_audit_log");

            migrationBuilder.DropIndex(
                name: "ix_auth_audit_log_actor_subject_created",
                table: "auth_audit_log");

            migrationBuilder.DropIndex(
                name: "ix_auth_audit_log_tenant_created",
                table: "auth_audit_log");

            migrationBuilder.DropColumn(
                name: "actor_credential",
                table: "auth_audit_log");

            migrationBuilder.DropColumn(
                name: "actor_subject_id",
                table: "auth_audit_log");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "auth_audit_log");
        }
    }
}
