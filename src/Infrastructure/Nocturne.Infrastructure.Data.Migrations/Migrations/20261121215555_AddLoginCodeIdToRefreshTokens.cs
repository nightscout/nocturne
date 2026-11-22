using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginCodeIdToRefreshTokens : Migration
    {
        internal const string RevokedReason = "Handed-off session ended";

        /// <summary>
        /// Revokes every live session redeemed from a login code before <c>login_code_id</c> recorded
        /// it. It touches only unrevoked rows, so a second run changes nothing.
        /// </summary>
        internal const string RevokeUnmarkedHandoffsSql = $"""
            UPDATE refresh_tokens
            SET revoked_at = now(), revoked_reason = '{RevokedReason}', updated_at = now()
            WHERE device_description = 'Login handoff'
              AND login_code_id IS NULL
              AND revoked_at IS NULL
              AND expires_at >= now();
            """;

        /// <summary>
        /// Adds the column, then runs <see cref="RevokeUnmarkedHandoffsSql"/>: a session handed off
        /// before the column has no mark, and rotation would keep it unmarked for as long as it is
        /// used.
        /// </summary>
        /// <remarks>
        /// The revoke writes what <c>EfFirstPartyTokenRepository</c>'s revocations write, with no
        /// successor, so the application rejects the token without treating it as reused.
        /// <c>refresh_tokens</c> carries only the <c>subject_pin</c> policy, which admits every row
        /// on the migrator's connection, where <c>app.current_subject_id</c> is unset.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "login_code_id",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(RevokeUnmarkedHandoffsSql);
        }

        /// <inheritdoc />
        /// <remarks>Sessions <see cref="Up"/> revoked stay revoked.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "login_code_id",
                table: "refresh_tokens");
        }
    }
}
