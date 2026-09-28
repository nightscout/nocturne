using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkOidcIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subjects_oidc_identity",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "oidc_issuer",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "oidc_subject_id",
                table: "subjects");

            migrationBuilder.CreateTable(
                name: "subject_oidc_identities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    oidc_subject_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    linked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subject_oidc_identities", x => x.id);
                    table.ForeignKey(
                        name: "FK_subject_oidc_identities_oidc_providers_provider_id",
                        column: x => x.provider_id,
                        principalTable: "oidc_providers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subject_oidc_identities_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_subject_oidc_identities_external",
                table: "subject_oidc_identities",
                columns: new[] { "oidc_subject_id", "issuer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subject_oidc_identities_provider_id",
                table: "subject_oidc_identities",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_subject_oidc_identities_subject_id",
                table: "subject_oidc_identities",
                column: "subject_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subject_oidc_identities");

            migrationBuilder.AddColumn<string>(
                name: "oidc_issuer",
                table: "subjects",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "oidc_subject_id",
                table: "subjects",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_oidc_identity",
                table: "subjects",
                columns: new[] { "oidc_subject_id", "oidc_issuer" },
                unique: true);
        }
    }
}
