using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DurableJobRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_migration_sources_identifier",
                table: "migration_sources");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "migration_sources",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at",
                table: "migration_runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "migration_runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "source_description",
                table: "migration_runs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "migration_runs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "connector_reset_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    total_connectors = table.Column<int>(type: "integer", nullable: false),
                    completed_connectors = table.Column<int>(type: "integer", nullable: false),
                    connectors_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connector_reset_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_tenant_identifier",
                table: "migration_sources",
                columns: new[] { "tenant_id", "source_identifier" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_migration_runs_tenant",
                table: "migration_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_connector_reset_jobs_state",
                table: "connector_reset_jobs",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_connector_reset_jobs_tenant",
                table: "connector_reset_jobs",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connector_reset_jobs");

            migrationBuilder.DropIndex(
                name: "ix_migration_sources_tenant_identifier",
                table: "migration_sources");

            migrationBuilder.DropIndex(
                name: "ix_migration_runs_tenant",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "migration_sources");

            migrationBuilder.DropColumn(
                name: "created_at",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "source_description",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "migration_runs");

            migrationBuilder.CreateIndex(
                name: "ix_migration_sources_identifier",
                table: "migration_sources",
                column: "source_identifier",
                unique: true);
        }
    }
}
