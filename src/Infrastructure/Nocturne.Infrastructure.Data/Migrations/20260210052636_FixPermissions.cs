using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "permissions",
                table: "roles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'::jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "scopes",
                table: "oidc_providers",
                type: "jsonb",
                nullable: false,
                defaultValue: "[\"openid\",\"profile\",\"email\"]",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[\"openid\",\"profile\",\"email\"]'::jsonb");

            migrationBuilder.AlterColumn<string>(
                name: "default_roles",
                table: "oidc_providers",
                type: "jsonb",
                nullable: false,
                defaultValue: "[\"readable\"]",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[\"readable\"]'::jsonb");

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_grants",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>(),
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "oauth_grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_device_codes",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>(),
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValueSql: "'{}'::text[]");

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_authorization_codes",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>(),
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "oauth_authorization_codes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "follower_invites",
                type: "text[]",
                nullable: false,
                defaultValue: new List<string>(),
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "limit_to_24_hours",
                table: "follower_invites",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "oauth_authorization_codes");

            migrationBuilder.DropColumn(
                name: "limit_to_24_hours",
                table: "follower_invites");

            migrationBuilder.AlterColumn<string>(
                name: "permissions",
                table: "roles",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValue: "[]");

            migrationBuilder.AlterColumn<string>(
                name: "scopes",
                table: "oidc_providers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[\"openid\",\"profile\",\"email\"]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValue: "[\"openid\",\"profile\",\"email\"]");

            migrationBuilder.AlterColumn<string>(
                name: "default_roles",
                table: "oidc_providers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[\"readable\"]'::jsonb",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValue: "[\"readable\"]");

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_grants",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValue: new List<string>());

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_device_codes",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValue: new List<string>());

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "oauth_authorization_codes",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValue: new List<string>());

            migrationBuilder.AlterColumn<List<string>>(
                name: "scopes",
                table: "follower_invites",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldDefaultValue: new List<string>());
        }
    }
}
