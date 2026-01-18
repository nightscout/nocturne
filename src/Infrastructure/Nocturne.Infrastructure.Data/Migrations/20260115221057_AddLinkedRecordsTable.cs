using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkedRecordsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "linked_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    canonical_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_timestamp = table.Column<long>(type: "bigint", nullable: false),
                    data_source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    sys_created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_linked_records", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_canonical",
                table: "linked_records",
                column: "canonical_id");

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_type_canonical_primary",
                table: "linked_records",
                columns: new[] { "record_type", "canonical_id", "is_primary" });

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_type_timestamp",
                table: "linked_records",
                columns: new[] { "record_type", "source_timestamp" });

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_unique",
                table: "linked_records",
                columns: new[] { "record_type", "record_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "linked_records");
        }
    }
}
