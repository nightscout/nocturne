using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMigrationRunCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "checkpoint",
                table: "migration_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_encrypted",
                table: "migration_runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "migration_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // A run an earlier version left live has no saved request to resume from, and the
            // startup sweep that settled such runs no longer covers this table.
            migrationBuilder.Sql("""
                UPDATE migration_runs
                SET state = 'Interrupted',
                    completed_at = now(),
                    error_message = 'The API restarted while this job was running; re-run it to finish the work.'
                WHERE state IN ('Pending', 'Running');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "checkpoint",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "request_encrypted",
                table: "migration_runs");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "migration_runs");
        }
    }
}
