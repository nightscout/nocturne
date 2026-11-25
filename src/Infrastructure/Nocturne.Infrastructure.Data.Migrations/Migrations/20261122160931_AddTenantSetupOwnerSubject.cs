using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantSetupOwnerSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "setup_owner_subject_id",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tenants_setup_owner_subject_id",
                table: "tenants",
                column: "setup_owner_subject_id");

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_subjects_setup_owner_subject_id",
                table: "tenants",
                column: "setup_owner_subject_id",
                principalTable: "subjects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenants_subjects_setup_owner_subject_id",
                table: "tenants");

            migrationBuilder.DropIndex(
                name: "ix_tenants_setup_owner_subject_id",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "setup_owner_subject_id",
                table: "tenants");
        }
    }
}
