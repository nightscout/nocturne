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

    private static readonly IReadOnlySet<string> ScopedTables = MigrationSourceFiles.TenantScopedTableNames();

    [Fact]
    public void NoDataMigrationDrivesItsTenantLoopOffATenantScopedTable()
    {
        var offenders = MigrationSourceFiles.All()
            .Where(f => !KnownNoOpMigrations.Contains(MigrationSourceFiles.Name(f)))
            .SelectMany(f => ScopedLoopSources(f).Select(t => $"{MigrationSourceFiles.Name(f)} -> FROM {t}"))
            .ToList();

        offenders.Should().BeEmpty(
            "a tenant loop must be driven off the tenants table; reading its bounds from a "
            + "tenant-scoped table under FORCE RLS returns no rows and makes the migration a no-op");
    }

    [Fact]
    public void TheGuardCanSeeMigrationsAndTenantScopedTables()
    {
        // Either set going empty makes the guard above pass vacuously. A named table catches the
        // narrower regression too: a set that still holds most tables but has quietly dropped some.
        MigrationSourceFiles.All().Should().NotBeEmpty();
        MigrationSourceFiles.TenantScopedTableNames().Should().Contain("sensor_glucose");
    }

    [Fact]
    public void EveryAllowlistedMigrationStillExistsAndStillOffends()
    {
        var stillOffending = MigrationSourceFiles.All()
            .Where(f => KnownNoOpMigrations.Contains(MigrationSourceFiles.Name(f)))
            .Where(f => ScopedLoopSources(f).Count > 0)
            .Select(MigrationSourceFiles.Name)
            .ToHashSet(StringComparer.Ordinal);

        stillOffending.Should().BeEquivalentTo(KnownNoOpMigrations,
            "an allowlist entry that no longer matches is stale and hides a regression");
    }

    /// <summary>
    /// Tenant-scoped tables the migration's <c>Up</c> drives a tenant loop off. Scanned as raw
    /// text: this detects offenders, and
    /// <see cref="MigrationSourceFiles.WithCommentsBlanked"/> withholds evidence, so a stray
    /// <c>/*</c> inside a SQL literal would blank a live loop out of view. A commented-out loop
    /// is therefore reported — the cheap direction to be wrong in.
    /// </summary>
    private static IReadOnlyList<string> ScopedLoopSources(string file)
    {
        var up = MigrationSourceFiles.UpBody(file);

        return LoopHeader.Matches(up)
            .Select(m => MigrationSourceFiles.BareTableName(m.Groups[1].Value))
            .Where(ScopedTables.Contains)
            .ToList();
    }
}
