using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceToDeviceSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "uploader_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "pump_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "aps_snapshots",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "data_source",
                table: "uploader_snapshots");

            migrationBuilder.DropColumn(
                name: "data_source",
                table: "pump_snapshots");

            migrationBuilder.DropColumn(
                name: "data_source",
                table: "aps_snapshots");
        }
    }
}
