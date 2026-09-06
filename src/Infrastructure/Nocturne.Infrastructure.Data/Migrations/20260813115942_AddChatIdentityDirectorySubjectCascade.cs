using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatIdentityDirectorySubjectCascade : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows whose subject is already gone would make AddForeignKey fail. subjects is a
            // global table, so this needs no tenant context.
            migrationBuilder.Sql(
                """
                DELETE FROM chat_identity_directory d
                WHERE NOT EXISTS (SELECT 1 FROM subjects s WHERE s.id = d.nocturne_user_id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_chat_identity_directory_nocturne_user_id",
                table: "chat_identity_directory",
                column: "nocturne_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_identity_directory_subjects_nocturne_user_id",
                table: "chat_identity_directory",
                column: "nocturne_user_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_identity_directory_subjects_nocturne_user_id",
                table: "chat_identity_directory");

            migrationBuilder.DropIndex(
                name: "IX_chat_identity_directory_nocturne_user_id",
                table: "chat_identity_directory");
        }
    }
}
