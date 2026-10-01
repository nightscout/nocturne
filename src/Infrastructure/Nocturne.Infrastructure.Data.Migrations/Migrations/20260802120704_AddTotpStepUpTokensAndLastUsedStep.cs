using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTotpStepUpTokensAndLastUsedStep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "last_used_step",
                table: "totp_credentials",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "totp_step_up_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_totp_step_up_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_totp_step_up_tokens_subjects_subject_id",
                        column: x => x.subject_id,
                        principalTable: "subjects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_totp_step_up_tokens_expires_at",
                table: "totp_step_up_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_totp_step_up_tokens_subject_id",
                table: "totp_step_up_tokens",
                column: "subject_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "totp_step_up_tokens");

            migrationBuilder.DropColumn(
                name: "last_used_step",
                table: "totp_credentials");
        }
    }
}
