using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotCorrelationId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Promote correlation_id from EF shadow property (varchar) to explicit
            // entity property (Guid/uuid). ALTER COLUMN is idempotent if already uuid.
            foreach (var table in new[] { "aps_snapshots", "pump_snapshots", "uploader_snapshots" })
            {
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table}
                         ALTER COLUMN correlation_id DROP DEFAULT,
                         ALTER COLUMN correlation_id TYPE uuid USING correlation_id::uuid;
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert uuid back to varchar(50).
            foreach (var table in new[] { "aps_snapshots", "pump_snapshots", "uploader_snapshots" })
            {
                migrationBuilder.Sql(
                    $"""
                     ALTER TABLE {table}
                         ALTER COLUMN correlation_id TYPE character varying(50) USING correlation_id::text;
                     """);
            }
        }
    }
}
