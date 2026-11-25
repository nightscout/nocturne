using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nocturne.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectorSetupToCaretaker : Migration
    {
        /// <summary>
        /// Every set of permissions a built-in Caretaker can hold without an owner having edited it:
        /// one for each seed it was ever given, as later data migrations left it.
        /// </summary>
        private const string UneditedCaretakers = """
            (VALUES
                ('["glucose.read", "treatments.readwrite", "devices.read", "therapy.read", "alerts.read", "reports.read",
                   "heartrate.read", "stepcount.read", "food.read", "sleep.read"]'::jsonb),
                ('["glucose.read", "treatments.readwrite", "devices.read", "food.read", "heartrate.read", "stepcount.read",
                   "reports.read", "therapy.read", "alerts.readwrite", "sleep.read"]'::jsonb),
                ('["glucose.read", "treatments.readwrite", "devices.read", "food.read", "heartrate.read", "stepcount.read",
                   "reports.read", "therapy.read", "alerts.readwrite", "sleep.read", "device.notify", "device.actuate"]'::jsonb)
            ) AS unedited (permissions)
            """;

        /// <summary>
        /// Gives <c>connectors.readwrite</c> to every built-in Caretaker whose permissions are, as a
        /// set, one of <see cref="UneditedCaretakers"/>. A row that already holds it matches none of
        /// them, so a second run changes nothing.
        /// </summary>
        internal const string UpSql = $"""
            UPDATE tenant_roles r
            SET permissions = r.permissions || '["connectors.readwrite"]'::jsonb
            WHERE r.slug = 'caretaker' AND r.is_system
              AND EXISTS (
                  SELECT 1 FROM {UneditedCaretakers}
                  WHERE r.permissions @> unedited.permissions AND r.permissions <@ unedited.permissions);
            """;

        internal const string DownSql = $"""
            UPDATE tenant_roles r
            SET permissions = r.permissions - 'connectors.readwrite'
            WHERE r.slug = 'caretaker' AND r.is_system
              AND r.permissions @> '["connectors.readwrite"]'::jsonb
              AND EXISTS (
                  SELECT 1 FROM {UneditedCaretakers}
                  WHERE (r.permissions - 'connectors.readwrite') @> unedited.permissions
                    AND (r.permissions - 'connectors.readwrite') <@ unedited.permissions);
            """;

        /// <inheritdoc />
        /// <remarks>No per-tenant loop: <c>tenant_roles</c> carries no row level security policy.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
