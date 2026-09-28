using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStateSpanSupersession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "superseded_by_id",
                table: "state_spans",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_state_spans_superseded_by_id",
                table: "state_spans",
                column: "superseded_by_id");

            migrationBuilder.AddForeignKey(
                name: "FK_state_spans_state_spans_superseded_by_id",
                table: "state_spans",
                column: "superseded_by_id",
                principalTable: "state_spans",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_state_spans_state_spans_superseded_by_id",
                table: "state_spans");

            migrationBuilder.DropIndex(
                name: "ix_state_spans_superseded_by_id",
                table: "state_spans");

            migrationBuilder.DropColumn(
                name: "superseded_by_id",
                table: "state_spans");
        }
    }
}
