using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_id = table.Column<string>(
                        type: "character varying(24)",
                        maxLength: 24,
                        nullable: true
                    ),
                    mills = table.Column<long>(type: "bigint", nullable: false),
                    dateString = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    type = table.Column<string>(
                        type: "character varying(100)",
                        maxLength: 100,
                        nullable: true
                    ),
                    description = table.Column<string>(type: "text", nullable: true),
                    duration = table.Column<double>(type: "double precision", nullable: true),
                    intensity = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    notes = table.Column<string>(type: "text", nullable: true),
                    enteredBy = table.Column<string>(
                        type: "character varying(255)",
                        maxLength: 255,
                        nullable: true
                    ),
                    utcOffset = table.Column<int>(type: "integer", nullable: true),
                    timestamp = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<string>(
                        type: "character varying(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    additional_properties = table.Column<string>(type: "jsonb", nullable: true),
                    sys_created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    sys_updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false,
                        defaultValueSql: "CURRENT_TIMESTAMP"
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activities", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_activities_mills",
                table: "activities",
                column: "mills",
                descending: new bool[0]
            );

            migrationBuilder.CreateIndex(
                name: "ix_activities_sys_created_at",
                table: "activities",
                column: "sys_created_at"
            );

            migrationBuilder.CreateIndex(
                name: "ix_activities_type",
                table: "activities",
                column: "type"
            );

            migrationBuilder.CreateIndex(
                name: "ix_activities_type_mills",
                table: "activities",
                columns: new[] { "type", "mills" },
                descending: new[] { false, true }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "activities");
        }
    }
}
