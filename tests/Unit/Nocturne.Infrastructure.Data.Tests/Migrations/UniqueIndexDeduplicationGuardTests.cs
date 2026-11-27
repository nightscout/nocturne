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
    /// Migrations whose only unique builds replace a table's primary key with a wider one that
    /// contains it, so there is no loser to clean up. The guard cannot read the old key off the
    /// migration, so <see cref="AWidenedPrimaryKeyMigrationBuildsNoUniqueIndexItDoesNotPromote"/>
    /// holds each entry to building no other unique index. The rationale is on
    /// <c>NocturneDbContext.ScopeKeyToTenant</c>.
    /// </summary>
    private static readonly IReadOnlySet<string> WidenedPrimaryKeyMigrations = new HashSet<string>(StringComparer.Ordinal)
    {
        "20261027134107_ScopeClientIdKeysToTenant",
        "20261027160711_ScopeReferencedClientIdKeysToTenant",
    };

    [Fact]
    public void NoNewMigrationCreatesAUniqueIndexOverPreExistingDataWithoutDeduplicating()
    {
        var offenders = MigrationSourceFiles.All()
            .Where(f => !WidenedPrimaryKeyMigrations.Contains(MigrationSourceFiles.Name(f)))
            .Select(f => (Migration: MigrationSourceFiles.Name(f), Tables: UndeduplicatedTables(f)))
            .Where(x => x.Tables.Count > 0)
            .Select(x => $"{x.Migration} -> {string.Join(", ", x.Tables)}")
            .ToList();

        offenders.Should().BeEmpty(
            "migrations run on API startup, so a unique index that fails on data an upgrading "
            + "instance already holds crash-loops the API with no self-service recovery; soft-delete "
            + "the duplicate losers earlier in the same Up, as "
            + "20260818102940_AddTenantScopedLegacyIdIndexesToSnapshots does (squashed into the "
            + "baseline; read it at the v0.3.0 tag). A table reported as "
            + $"'{MigrationSourceFiles.UnresolvedTable}' is one the guard could not read off the call; unroll the loop "
            + "so both the CREATE TABLE and the index name it literally — naming only the index "
            + "still reports, because the exemption cannot match a create it also cannot read");
    }

    /// <summary>
    /// The concurrent unique build has no grandfathered offender to keep its pattern honest, and a
    /// pattern that stopped matching would clear every migration using it.
    /// </summary>
    [Fact]
    public void TheGuardSeesAConcurrentUniqueBuild()
    {
        const string up = """
            ConcurrentIndexBuilder.BuildUnique(
                migrationBuilder,
                "ix_notes_tenant_source_sync_id",
                "ON notes (tenant_id, data_source, sync_identifier) "
                + "WHERE sync_identifier IS NOT NULL AND deleted_at IS NULL");
            """;

        UniqueIndexCreations(up).Select(x => x.Table).Should().Equal("notes");
    }

    /// <summary>
    /// <c>revoked_at</c> is how <c>oauth_grants</c> soft-deletes, so revoking the losers is a cleanup
    /// there and nowhere else.
    /// </summary>
    [Fact]
    public void TheGuardSeesARevokedAtCleanupOnOauthGrantsAndOnNoOtherTable()
    {
        const string rank = """
            WITH ranked AS (SELECT id, row_number() OVER (PARTITION BY tenant_id, client_id, subject_id ORDER BY created_at DESC) AS rn FROM TABLE)
            UPDATE TABLE g SET revoked_at = now() FROM ranked WHERE g.id = ranked.id AND ranked.rn > 1;
            ConcurrentIndexBuilder.BuildUnique(migrationBuilder, "ix_x", "ON TABLE (tenant_id, client_id, subject_id)");
            """;

        UndeduplicatedTablesIn(rank.Replace("TABLE", "oauth_grants")).Should().BeEmpty();
        UndeduplicatedTablesIn(rank.Replace("TABLE", "oauth_refresh_tokens")).Should().Equal("oauth_refresh_tokens");
    }

    [Fact]
    public void AWidenedPrimaryKeyMigrationBuildsNoUniqueIndexItDoesNotPromote()
    {
        foreach (var migration in WidenedPrimaryKeyMigrations)
        {
            var file = MigrationSourceFiles.All().Single(f => MigrationSourceFiles.Name(f) == migration);

            UnpromotedUniqueBuilds(MigrationSourceFiles.UpBody(file)).Should().BeEmpty(
                $"{migration} is exempt from the cleanup rule only while every unique index it builds "
                + "becomes a primary key, so a stray unique index cannot hide behind the allowlist");
        }
    }

    [Fact]
    public void TheGuardSeesAUniqueBuildThatIsNotPromoted()
    {
        const string up = """
            ConcurrentIndexBuilder.BuildUnique(migrationBuilder, $"pk_{table}_tenant_id_id", $"ON {table} (tenant_id, id)");
            migrationBuilder.Sql($"ALTER TABLE {table} ADD CONSTRAINT PK_{table} PRIMARY KEY USING INDEX pk_{table}_tenant_id_id;");
            ConcurrentIndexBuilder.BuildUnique(migrationBuilder, "ix_stray", "ON notes (sync_identifier)");
            """;

        UnpromotedUniqueBuilds(up).Should().Equal("ix_stray");
    }

    private static IReadOnlyList<string> UnpromotedUniqueBuilds(string up) =>
        BuiltUniqueIndexName.Matches(up)
            .Select(m => m.Groups[1].Value)
            .Where(name => !up.Contains($"PRIMARY KEY USING INDEX {name}", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void EveryWidenedPrimaryKeyMigrationStillExistsAndStillLacksDeduplication()
    {
        // Also the discovery check: a moved directory, a changed glob or a detection regex that
        // stopped matching empties this set, and an empty set is not the allowlist.
        var stillOffending = MigrationSourceFiles.All()
            .Where(f => UndeduplicatedTables(f).Count > 0)
            .Select(MigrationSourceFiles.Name)
            .ToHashSet(StringComparer.Ordinal);

        stillOffending.Should().BeEquivalentTo(WidenedPrimaryKeyMigrations,
            "an allowlist entry that no longer matches is stale and hides a regression");
    }

    /// <summary>
    /// Tables the migration makes unique without an earlier cleanup, ignoring tables it creates
    /// itself — those hold no rows yet, so a cleanup there would be dead code.
    /// </summary>
    private static IReadOnlyList<string> UndeduplicatedTables(string file) =>
        UndeduplicatedTablesIn(MigrationSourceFiles.UpBody(file));

    private static IReadOnlyList<string> UndeduplicatedTablesIn(string up)
    {
        var live = MigrationSourceFiles.WithCommentsBlanked(up);
        var created = MigrationSourceFiles.TablesCreatedIn(live);

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
    /// target that is not a bare literal, such as a <c>{table}</c> hole in interpolated SQL or a
    /// loop variable, resolves to <see cref="MigrationSourceFiles.UnresolvedTable"/>, which no
    /// <c>CreateTable</c> can match, so it can only ever be exempted by an actual cleanup. A schema
    /// qualifier is stripped, so <c>public.x</c> resolves to <c>x</c>.
    /// Dropping it instead would blind the guard to the multi-table loop the rule's own worked
    /// example is written in.
    /// </summary>
    private static IEnumerable<(int Position, string Table)> UniqueIndexCreations(string up)
    {
        foreach (var regex in new[] { FluentUniqueIndex, FluentUniqueConstraint })
            foreach (Match match in regex.Matches(up))
                yield return (match.Index, MigrationSourceFiles.FluentTable(match.Groups[1].Value));

        foreach (var regex in new[] { SqlUniqueIndex, SqlUniqueConstraint, BuilderUniqueIndex })
            foreach (Match match in regex.Matches(up))
                yield return (match.Index, MigrationSourceFiles.ResolveTable(match.Groups[1].Value));
    }

    private const RegexOptions Sql = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled;

    private static readonly Regex FluentUniqueIndex = new(
        @"\.CreateIndex\s*\(([^;]*?\bunique\s*:\s*true[^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex FluentUniqueConstraint = new(
        @"\.(?:AddUniqueConstraint|AddPrimaryKey)\s*\(([^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex SqlUniqueIndex = new(
        @"CREATE\s+UNIQUE\s+INDEX(?:\s+CONCURRENTLY)?(?:\s+IF\s+NOT\s+EXISTS)?\s+\S+\s+ON\s+(?:ONLY\s+)?(\S+)",
        Sql);

    /// <summary>
    /// <c>ConcurrentIndexBuilder.BuildUnique</c>, whose table is the one its definition names after
    /// <c>ON</c>; a <c>{table}</c> hole there resolves to <see cref="MigrationSourceFiles.UnresolvedTable"/>.
    /// </summary>
    private static readonly Regex BuilderUniqueIndex = new(
        @"ConcurrentIndexBuilder\.BuildUnique\s*\([^;]*?""ON\s+(?:ONLY\s+)?([^\s""]+)", Sql);

    private static readonly Regex BuiltUniqueIndexName = new(
        @"ConcurrentIndexBuilder\.BuildUnique\s*\(\s*migrationBuilder\s*,\s*\$?""([^""]+)""", Sql);

    private static readonly Regex SqlUniqueConstraint = new(
        @"ALTER\s+TABLE\s+(?:ONLY\s+)?(\S+)[^;]*?\bADD\s+CONSTRAINT\b[^;]*?\bUNIQUE\b", Sql);

    private static readonly Regex RankedDuplicates = new(
        @"row_number\s*\(\s*\)\s*over\s*\(\s*partition\s+by\b", Sql);

    /// <summary>
    /// A soft delete or a delete. <c>oauth_grants</c> has no <c>deleted_at</c>: a revoked grant is
    /// its soft-deleted one, so <c>revoked_at</c> counts there and for no other table.
    /// </summary>
    private static readonly Regex RemovesRows = new(
        @"\bupdate\s+\S+\s+set\s+deleted_at\b|\bdelete\s+from\s+\S+"
        + @"|\bupdate\s+oauth_grants(?:\s+(?:as\s+)?\w+)?\s+set\s+revoked_at\b", Sql);
}
