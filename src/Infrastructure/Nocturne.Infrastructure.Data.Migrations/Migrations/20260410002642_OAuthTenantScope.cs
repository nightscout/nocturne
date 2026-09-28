using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class OAuthTenantScope : Migration
    {
        /// <inheritdoc />
        /// <summary>
        /// Tables to clean before adding NOT NULL tenant_id columns.
        /// Order: child tables first to avoid FK violations.
        /// </summary>
        private static readonly string[] OAuthTablesToClean =
        [
            "oauth_refresh_tokens",
            "oauth_authorization_codes",
            "oauth_device_codes",
            "oauth_grants",
            "oauth_clients",
        ];

        /// <summary>
        /// Tables that receive a new tenant_id column and need RLS policies.
        /// oauth_refresh_tokens is excluded — it has no tenant_id; isolation
        /// is enforced transitively via its FK to oauth_grants.
        /// </summary>
        private static readonly string[] TenantScopedOAuthTables =
        [
            "oauth_authorization_codes",
            "oauth_device_codes",
            "oauth_grants",
            "oauth_clients",
        ];

        /// <summary>
        /// Adds tenant_id columns and RLS policies to OAuth tables, making them tenant-scoped.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Clean-slate: delete all existing OAuth rows before adding NOT NULL tenant_id.
            // Order matters: child tables first to avoid FK violations.
            foreach (var table in OAuthTablesToClean)
            {
                migrationBuilder.Sql($"DELETE FROM {table};");
            }

            migrationBuilder.DropIndex(
                name: "ix_oauth_clients_client_id",
                table: "oauth_clients");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oauth_grants",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oauth_device_codes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "client_name",
                table: "oauth_clients",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "client_uri",
                table: "oauth_clients",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "created_from_ip",
                table: "oauth_clients",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logo_uri",
                table: "oauth_clients",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "software_id",
                table: "oauth_clients",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oauth_clients",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "oauth_authorization_codes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_oauth_grants_tenant_subject",
                table: "oauth_grants",
                columns: new[] { "tenant_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_tenant_client_id",
                table: "oauth_clients",
                columns: new[] { "tenant_id", "client_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients",
                columns: new[] { "tenant_id", "software_id" },
                unique: true,
                filter: "\"software_id\" IS NOT NULL");

            // Enable RLS on all OAuth tables that now carry tenant_id.
            // Matches the pattern from AddRlsToNewTenantTables migration.
            foreach (var table in TenantScopedOAuthTables)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"""
                    DROP POLICY IF EXISTS tenant_isolation ON {table};
                    CREATE POLICY tenant_isolation ON {table}
                        USING (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Tear down RLS policies
            foreach (var table in TenantScopedOAuthTables)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }

            migrationBuilder.DropIndex(
                name: "ix_oauth_grants_tenant_subject",
                table: "oauth_grants");

            migrationBuilder.DropIndex(
                name: "ix_oauth_clients_tenant_client_id",
                table: "oauth_clients");

            migrationBuilder.DropIndex(
                name: "ix_oauth_clients_tenant_software_id",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oauth_grants");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oauth_device_codes");

            migrationBuilder.DropColumn(
                name: "client_name",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "client_uri",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "created_from_ip",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "logo_uri",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "software_id",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oauth_clients");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "oauth_authorization_codes");

            migrationBuilder.CreateIndex(
                name: "ix_oauth_clients_client_id",
                table: "oauth_clients",
                column: "client_id",
                unique: true);
        }
    }
}
