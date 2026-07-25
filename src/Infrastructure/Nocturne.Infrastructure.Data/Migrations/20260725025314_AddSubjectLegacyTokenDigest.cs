using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubjectLegacyTokenDigest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "legacy_token_digest",
                table: "subjects",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_subjects_legacy_token_digest",
                table: "subjects",
                column: "legacy_token_digest",
                filter: "legacy_token_digest IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_subjects_legacy_token_digest",
                table: "subjects");

            migrationBuilder.DropColumn(
                name: "legacy_token_digest",
                table: "subjects");
        }
    }
}
