using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_source_metadata",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_data_source_metadata", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_data_source_metadata_created_at",
                table: "data_source_metadata",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_data_source_metadata_device_id",
                table: "data_source_metadata",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_data_source_metadata_is_archived",
                table: "data_source_metadata",
                column: "is_archived");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_source_metadata");
        }
    }
}
