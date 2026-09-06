using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadAccessLogCredentialFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // api_secret_hash_prefix held the first 8 characters of the credential's stored digest,
            // which is match material for the lookup that authenticates it. The fingerprint that
            // replaces it is computed from a different input, so no existing value can be carried
            // over; dropping the column discards the prefixes rather than leaving them readable
            // under a new name. read_access_log is under FORCE ROW LEVEL SECURITY, so an UPDATE
            // here would match nothing anyway (no tenant GUC is set during migration).
            migrationBuilder.DropColumn(
                name: "api_secret_hash_prefix",
                table: "read_access_log");

            migrationBuilder.AddColumn<string>(
                name: "credential_fingerprint",
                table: "read_access_log",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "credential_fingerprint",
                table: "read_access_log");

            migrationBuilder.AddColumn<string>(
                name: "api_secret_hash_prefix",
                table: "read_access_log",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }
    }
}
