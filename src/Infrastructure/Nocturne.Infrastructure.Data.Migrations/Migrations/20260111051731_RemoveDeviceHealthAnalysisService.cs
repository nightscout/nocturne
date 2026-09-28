using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeviceHealthAnalysisService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "migration_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_identifier = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    nightscout_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    nightscout_api_secret_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    mongo_connection_string_encrypted = table.Column<string>(type: "text", nullable: true),
                    mongo_database_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    last_migration_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_migrated_data_timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_sources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "migration_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    date_range_start = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    date_range_end = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    entries_migrated = table.Column<int>(type: "integer", nullable: false),
                    treatments_migrated = table.Column<int>(type: "integer", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_migration_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_migration_runs_migration_sources_source_id",
                        column: x => x.source_id,
                        principalTable: "migration_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_runs_source_id",
                table: "migration_runs",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_migration_runs_source_state",
                table: "migration_runs",
                columns: new[] { "source_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_migration_runs_started_at",
                table: "migration_runs",
                column: "started_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_migration_runs_state",
                table: "migration_runs",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_created_at",
                table: "migration_sources",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_identifier",
                table: "migration_sources",
                column: "source_identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_last_migration",
                table: "migration_sources",
                column: "last_migration_at");

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_mode",
                table: "migration_sources",
                column: "mode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "migration_runs");

            migrationBuilder.DropTable(
                name: "migration_sources");
        }
    }
}
