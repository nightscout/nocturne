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
            // Add correlation_id as uuid if it doesn't exist, or promote from varchar.
            foreach (var table in new[] { "aps_snapshots", "pump_snapshots", "uploader_snapshots" })
            {
                migrationBuilder.Sql(
                    $"""
                     DO $$
                     BEGIN
                         IF NOT EXISTS (
                             SELECT 1 FROM information_schema.columns
                             WHERE table_name = '{table}' AND column_name = 'correlation_id'
                         ) THEN
                             ALTER TABLE {table} ADD COLUMN correlation_id uuid;
                         ELSE
                             ALTER TABLE {table}
                                 ALTER COLUMN correlation_id DROP DEFAULT,
                                 ALTER COLUMN correlation_id TYPE uuid USING correlation_id::uuid;
                         END IF;
                     END $$;
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
