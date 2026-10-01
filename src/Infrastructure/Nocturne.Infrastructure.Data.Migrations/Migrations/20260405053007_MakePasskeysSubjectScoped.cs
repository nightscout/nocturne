using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakePasskeysSubjectScoped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passkey_credentials_tenant_id_credential_id",
                table: "passkey_credentials");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "passkey_credentials");

            migrationBuilder.CreateIndex(
                name: "IX_passkey_credentials_credential_id",
                table: "passkey_credentials",
                column: "credential_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passkey_credentials_credential_id",
                table: "passkey_credentials");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "passkey_credentials",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_passkey_credentials_tenant_id_credential_id",
                table: "passkey_credentials",
                columns: new[] { "tenant_id", "credential_id" },
                unique: true);
        }
    }
}
