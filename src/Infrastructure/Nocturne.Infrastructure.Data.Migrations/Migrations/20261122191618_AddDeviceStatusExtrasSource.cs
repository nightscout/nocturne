using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceStatusExtrasSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "data_source",
                table: "device_status_extras",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "device",
                table: "device_status_extras",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "data_source",
                table: "device_status_extras");

            migrationBuilder.DropColumn(
                name: "device",
                table: "device_status_extras");
        }
    }
}
