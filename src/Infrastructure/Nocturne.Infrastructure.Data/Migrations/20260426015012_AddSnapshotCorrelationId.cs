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
            // correlation_id already exists as varchar(50) from a shadow property.
            // Convert to uuid using a CAST so existing data is preserved.
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
