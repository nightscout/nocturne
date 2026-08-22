using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Migrations run on API startup, so a unique index that trips over rows an upgrading instance
/// already holds fails the chain and crash-loops the API with no self-service recovery. A
/// migration adding one to a table it did not create in the same migration must therefore remove
/// the duplicate losers first.
/// <para>
/// The check is presence and ordering within <c>Up</c>: at least one live duplicate-ranking
/// statement ahead of the first unique index. It does not prove the SQL is correct, and does not
/// pair each index with its own cleanup — reviewers do that. It matches only the house idiom
/// (<c>row_number() OVER (PARTITION BY …)</c> feeding a soft delete or a delete), so a cleanup
/// written some other way reports as an offender; widen the migration to the idiom rather than
/// the guard to the migration. The shapes it cannot see are listed under "Unique indexes over
/// existing data" in CLAUDE.md, where the authors it is a backstop for will read them.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class UniqueIndexDeduplicationGuardTests
{
    /// <summary>
    /// Migrations that shipped before the rule. Their <c>Up</c> is history — an instance whose
    /// chain fails here never reaches a later migration, so no new migration can repair them and
    /// editing them would not re-run. Membership in this list is the only thing separating a
    /// grandfathered migration from a new one; nothing may be added.
    /// </summary>
    private static readonly IReadOnlySet<string> GrandfatheredMigrations = new HashSet<string>(StringComparer.Ordinal)
    {
        "20251126040528_AddTreatmentOriginalIdUniqueIndex",
        "20251229020632_AddConnectorFoodEntries",
        "20260217005635_MakeV4LegacyIdUnique",
        "20260227230204_FixConnectorConfigUniqueIndex",
        "20260228071347_AddRlsWithCheckAndCompositeIndexes",
        "20260320105641_DeviceIdentityUnification",
        "20260326215857_UnifyFollowerGrantsWithTenantMembers",
        "20260405053007_MakePasskeysSubjectScoped",
        "20260410002642_OAuthTenantScope",
        "20260416103826_DropCarbIntakeBolusIdAndAddSyncIdentifierIndexes",
        "20260424052011_AddTenantMemberUsername",
        "20260514052027_AddSoftDeleteToV4Entities",
        "20260603134703_MakeDevicesUniqueIndexTenantScoped",
        "20260607084109_AddTenantShareToken",
        "20260609093227_AddTimezoneTimelineAndSensorGlucoseSyncId",
        "20260616124951_AddHealthMetricsSyncDedup",
        "20260717133302_AddDeviceStatusSnapshotSyncIdentifiers",
        "20260717141728_AddTempBasalSyncIdentifierAndCarbMacros",
        "20260802073145_DurableJobRecords",
        "20260817042048_RestoreUpdateTimestampWritesAndBasalInjectionSoftDeleteIndex",
        "20260817064038_AddTenantScopedLegacyIdIndexesToMeterGlucoseAndCalibrations",
    };

    [Fact]
    public void NoNewMigrationCreatesAUniqueIndexOverPreExistingDataWithoutDeduplicating()
    {
        var offenders = MigrationSourceFiles.All()
            .Where(f => !GrandfatheredMigrations.Contains(MigrationSourceFiles.Name(f)))
            .Select(f => (Migration: MigrationSourceFiles.Name(f), Tables: UndeduplicatedTables(f)))
            .Where(x => x.Tables.Count > 0)
            .Select(x => $"{x.Migration} -> {string.Join(", ", x.Tables)}")
            .ToList();

        offenders.Should().BeEmpty(
            "migrations run on API startup, so a unique index that fails on data an upgrading "
            + "instance already holds crash-loops the API with no self-service recovery; soft-delete "
            + "the duplicate losers earlier in the same Up, as "
            + "20260818102940_AddTenantScopedLegacyIdIndexesToSnapshots does. A table reported as "
            + $"'{UnresolvedTable}' is one the guard could not read off the call; unroll the loop "
            + "so both the CREATE TABLE and the index name it literally — naming only the index "
            + "still reports, because the exemption cannot match a create it also cannot read");
    }

    [Fact]
    public void EveryGrandfatheredMigrationStillExistsAndStillLacksDeduplication()
    {
        // Also the discovery check: a moved directory, a changed glob or a detection regex that
        // stopped matching empties this set, and an empty set is not the allowlist.
        var stillOffending = MigrationSourceFiles.All()
            .Where(f => UndeduplicatedTables(f).Count > 0)
            .Select(MigrationSourceFiles.Name)
            .ToHashSet(StringComparer.Ordinal);

        stillOffending.Should().BeEquivalentTo(GrandfatheredMigrations,
            "an allowlist entry that no longer matches is stale and hides a regression");
    }

    /// <summary>
    /// Tables the migration makes unique without an earlier cleanup, ignoring tables it creates
    /// itself — those hold no rows yet, so a cleanup there would be dead code.
    /// </summary>
    private static IReadOnlyList<string> UndeduplicatedTables(string file)
    {
        var up = MigrationSourceFiles.UpBody(file);
        var live = MigrationSourceFiles.WithCommentsBlanked(up);
        var created = TablesCreatedIn(live);

        var indexed = UniqueIndexCreations(up)
            .Where(x => !created.Contains(x.Table))
            .ToList();

        if (indexed.Count == 0)
            return [];

        var firstIndex = indexed.Min(x => x.Position);

        var deduplicates =
            RankedDuplicates.Matches(live).Any(m => m.Index < firstIndex)
            && RemovesRows.Matches(live).Any(m => m.Index < firstIndex);

        return deduplicates
            ? []
            : indexed.Select(x => x.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every unique index the migration's <c>Up</c> creates, paired with its target table. A
    /// target that is not a bare literal — a <c>{table}</c> hole in interpolated SQL, a loop
    /// variable, a schema qualifier — resolves to <see cref="UnresolvedTable"/>, which no
    /// <c>CreateTable</c> can match, so it can only ever be exempted by an actual cleanup.
    /// Dropping it instead would blind the guard to the multi-table loop the rule's own worked
    /// example is written in.
    /// </summary>
    private static IEnumerable<(int Position, string Table)> UniqueIndexCreations(string up)
    {
        foreach (var regex in new[] { FluentUniqueIndex, FluentUniqueConstraint })
            foreach (Match match in regex.Matches(up))
                yield return (match.Index, FluentTable(match.Groups[1].Value));

        foreach (var regex in new[] { SqlUniqueIndex, SqlUniqueConstraint })
            foreach (Match match in regex.Matches(up))
                yield return (match.Index, ResolveTable(match.Groups[1].Value));
    }

    /// <summary>
    /// The table a builder call targets: the named <c>table:</c> argument, or for a positional
    /// call the second string literal — <c>CreateIndex</c>, <c>AddUniqueConstraint</c> and
    /// <c>AddPrimaryKey</c> all take (name, table, …).
    /// </summary>
    private static string FluentTable(string arguments) =>
        TableArgument.Match(arguments) is { Success: true } named
            ? ResolveTable(named.Groups[1].Value)
            : PositionalTableArgument.Match(arguments) is { Success: true } positional
                ? ResolveTable(positional.Groups[1].Value)
                : UnresolvedTable;

    /// <summary>
    /// The table a captured reference names, or <see cref="UnresolvedTable"/> when it is not a
    /// plain name — an interpolated <c>{table}</c> hole, or a loop variable. A schema qualifier is
    /// dropped so <c>public.x</c> and <c>x</c> are one table on both the index and the
    /// <c>CREATE TABLE</c> side; splitting them would red-build a schema-qualified new table.
    /// </summary>
    private static string ResolveTable(string captured)
    {
        var bare = MigrationSourceFiles.BareTableName(captured);
        var unqualified = bare[(bare.LastIndexOf('.') + 1)..];

        return PlainTableName.IsMatch(unqualified) ? unqualified : UnresolvedTable;
    }

    private static IReadOnlySet<string> TablesCreatedIn(string up) =>
        new[] { FluentCreateTable, SqlCreateTable }
            .SelectMany(r => r.Matches(up).Select(m => ResolveTable(m.Groups[1].Value)))
            .Where(t => t != UnresolvedTable)
            .ToHashSet(StringComparer.Ordinal);

    private const string UnresolvedTable = "<table not resolvable to a literal name>";

    private const RegexOptions Sql = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex PlainTableName = new(@"^\w+$", RegexOptions.Compiled);

    private static readonly Regex FluentUniqueIndex = new(
        @"\.CreateIndex\s*\(([^;]*?\bunique\s*:\s*true[^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex FluentUniqueConstraint = new(
        @"\.(?:AddUniqueConstraint|AddPrimaryKey)\s*\(([^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex FluentCreateTable = new(
        @"\.CreateTable\s*\(\s*name\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex TableArgument = new(@"table\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex PositionalTableArgument = new(
        @"^\s*""[^""]*""\s*,\s*""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex SqlUniqueIndex = new(
        @"CREATE\s+UNIQUE\s+INDEX(?:\s+CONCURRENTLY)?(?:\s+IF\s+NOT\s+EXISTS)?\s+\S+\s+ON\s+(?:ONLY\s+)?(\S+)",
        Sql);

    private static readonly Regex SqlUniqueConstraint = new(
        @"ALTER\s+TABLE\s+(?:ONLY\s+)?(\S+)[^;]*?\bADD\s+CONSTRAINT\b[^;]*?\bUNIQUE\b", Sql);

    private static readonly Regex SqlCreateTable = new(
        @"CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(\S+)", Sql);

    private static readonly Regex RankedDuplicates = new(
        @"row_number\s*\(\s*\)\s*over\s*\(\s*partition\s+by\b", Sql);

    private static readonly Regex RemovesRows = new(
        @"\bupdate\s+\S+\s+set\s+deleted_at\b|\bdelete\s+from\s+\S+", Sql);
}
