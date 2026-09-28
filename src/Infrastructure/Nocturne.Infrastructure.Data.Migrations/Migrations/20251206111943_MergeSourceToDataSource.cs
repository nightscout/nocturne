using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeSourceToDataSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Merge any values from 'source' to 'data_source' where data_source is null
            // This preserves data from connectors that were using the redundant Source field
            migrationBuilder.Sql(
                "UPDATE treatments SET data_source = source WHERE data_source IS NULL AND source IS NOT NULL"
            );

            // Step 2: Drop the old source column since data is now in data_source
            migrationBuilder.DropColumn(
                name: "source",
                table: "treatments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "treatments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);
        }
    }
}
