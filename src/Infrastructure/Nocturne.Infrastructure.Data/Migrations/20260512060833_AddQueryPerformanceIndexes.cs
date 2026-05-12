using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS public.ix_linked_records_record;");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_sensor_glucose_tenant_timestamp " +
                "ON sensor_glucose (tenant_id, timestamp DESC);");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_linked_records_non_primary_record " +
                "ON linked_records (record_type, record_id) WHERE NOT is_primary;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sensor_glucose_tenant_timestamp",
                table: "sensor_glucose");

            migrationBuilder.DropIndex(
                name: "ix_linked_records_non_primary_record",
                table: "linked_records");

            migrationBuilder.CreateIndex(
                name: "ix_linked_records_record",
                table: "linked_records",
                columns: new[] { "record_type", "record_id" });
        }
    }
}
