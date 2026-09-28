using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceIsDemoWithDataSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Add new data_source columns
            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "treatments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "entries",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true
            );

            // Step 2: Migrate existing data - set data_source = 'demo-service' where is_demo = true
            migrationBuilder.Sql(
                "UPDATE treatments SET data_source = 'demo-service' WHERE is_demo = true"
            );
            migrationBuilder.Sql(
                "UPDATE entries SET data_source = 'demo-service' WHERE is_demo = true"
            );

            // Step 3: Drop the old is_demo columns
            migrationBuilder.DropColumn(name: "is_demo", table: "treatments");

            migrationBuilder.DropColumn(name: "is_demo", table: "entries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Step 1: Add back the is_demo columns
            migrationBuilder.AddColumn<bool>(
                name: "is_demo",
                table: "treatments",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "is_demo",
                table: "entries",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            // Step 2: Migrate data back - set is_demo = true where data_source = 'demo-service'
            migrationBuilder.Sql(
                "UPDATE treatments SET is_demo = true WHERE data_source = 'demo-service'"
            );
            migrationBuilder.Sql(
                "UPDATE entries SET is_demo = true WHERE data_source = 'demo-service'"
            );

            // Step 3: Drop the data_source columns
            migrationBuilder.DropColumn(name: "data_source", table: "treatments");

            migrationBuilder.DropColumn(name: "data_source", table: "entries");
        }
    }
}
