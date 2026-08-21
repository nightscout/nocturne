using System.Text.RegularExpressions;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Every tenant-scoped table is under FORCE ROW LEVEL SECURITY and the migrator role is
/// NOBYPASSRLS, so a statement that runs before <c>app.current_tenant_id</c> is set matches
/// nothing. A data migration that loops over tenants to set the GUC therefore cannot read its
/// own loop bounds from a tenant-scoped table: the driving SELECT runs outside any tenant
/// context, returns no rows, and the whole migration is a silent no-op that still records as
/// applied. Drive the loop off <c>tenants</c>, which carries no RLS.
/// </summary>
[Trait("Category", "Unit")]
public class DataMigrationTenantContextGuardTests
{
    /// <summary>
    /// Shipped migrations whose driving SELECT reads a tenant-scoped table. Each was a silent
    /// no-op on every deployment. They stay as-is because an applied migration is history and
    /// rewriting it would not re-run; the fix is a new migration that redoes the work. Nothing
    /// may be added here.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownNoOpMigrations = new HashSet<string>(StringComparer.Ordinal)
    {
        "20260428074655_BackfillSensorGlucosePatientDeviceId",
        "20260430071311_AlertsRedesign",
        "20260515091302_DropTenantAlertSettingsTimezone",
    };

    /// <summary>
    /// Matches a PL/pgSQL <c>FOR &lt;var&gt; IN SELECT ... FROM &lt;source&gt;</c> header, binding the
    /// source of the FIRST FROM after the SELECT. Anchoring to the first FROM keeps a derived
    /// table (<c>FROM (VALUES ...)</c>) or a set-returning function (<c>FROM jsonb_array_elements(...)</c>)
    /// from being skipped over in favour of a later, unrelated table name.
    /// </summary>
    private static readonly Regex LoopHeader = new(
        @"FOR\s+\w+\s+IN\s+SELECT\s+(?:(?!\bFROM\b).)*?\s+FROM\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    [Fact]
    public void NoDataMigrationDrivesItsTenantLoopOffATenantScopedTable()
    {
        var scoped = MigrationSourceFiles.TenantScopedTableNames();
        var offenders = new List<string>();

        foreach (var file in MigrationSourceFiles.All())
        {
            var migration = Path.GetFileNameWithoutExtension(file);

            foreach (System.Text.RegularExpressions.Match match in LoopHeader.Matches(File.ReadAllText(file)))
            {
                var source = LoopSourceTable(match.Groups[1].Value);

                if (scoped.Contains(source) && !KnownNoOpMigrations.Contains(migration))
                    offenders.Add($"{migration} -> FROM {source}");
            }
        }

        offenders.Should().BeEmpty(
            "a tenant loop must be driven off the tenants table; reading its bounds from a "
            + "tenant-scoped table under FORCE RLS returns no rows and makes the migration a no-op");
    }

    [Fact]
    public void TheGuardCanSeeMigrationsAndTenantScopedTables()
    {
        // A path or reflection regression would empty both sets and make the guard above pass
        // vacuously.
        MigrationSourceFiles.All().Should().NotBeEmpty();
        MigrationSourceFiles.TenantScopedTableNames().Should().NotBeEmpty();
    }

    [Fact]
    public void EveryAllowlistedMigrationStillExistsAndStillOffends()
    {
        var scoped = MigrationSourceFiles.TenantScopedTableNames();

        var stillOffending = MigrationSourceFiles.All()
            .Where(f => KnownNoOpMigrations.Contains(Path.GetFileNameWithoutExtension(f)))
            .Where(f => LoopHeader.Matches(File.ReadAllText(f))
                .Any(m => scoped.Contains(LoopSourceTable(m.Groups[1].Value))))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        stillOffending.Should().BeEquivalentTo(KnownNoOpMigrations,
            "an allowlist entry that no longer matches is stale and hides a regression");
    }

    /// <summary>
    /// Trims the captured FROM source to its bare table name, dropping an argument list,
    /// alias, or trailing punctuation. A derived table yields an empty string, which matches
    /// no table.
    /// </summary>
    private static string LoopSourceTable(string captured) =>
        Regex.Split(captured, @"[(;,\s]")[0].Trim('"').ToLowerInvariant();
}
