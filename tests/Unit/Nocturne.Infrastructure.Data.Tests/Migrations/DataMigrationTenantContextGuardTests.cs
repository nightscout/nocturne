using System.Text.RegularExpressions;
using Match = System.Text.RegularExpressions.Match;

namespace Nocturne.Infrastructure.Data.Tests.Migrations;

/// <summary>
/// Every tenant-scoped table is under FORCE ROW LEVEL SECURITY and the migrator role is
/// NOBYPASSRLS, so a statement that runs before <c>app.current_tenant_id</c> is set matches
/// nothing. A data migration that loops over tenants to set the GUC therefore cannot read its
/// own loop bounds from a tenant-scoped table: the driving SELECT runs outside any tenant
/// context, returns no rows, and the whole migration is a silent no-op that still records as
/// applied. Drive the loop off <c>tenants</c>, which carries no RLS.
/// <para>
/// The same silent no-op needs no loop at all — a bare <c>UPDATE</c> on a tenant-scoped table
/// matches zero rows — so loop sources and loop-free DML are checked separately. Every
/// tenant-scoped table the baseline creates is already under RLS, so every finding counts.
/// </para>
/// <para>
/// Shapes this cannot see, which review has to catch: a DML target it cannot read off the
/// statement, i.e. an interpolated <c>{table}</c> hole or a PL/pgSQL variable; DML issued
/// through the <c>migrationBuilder.UpdateData</c>/<c>DeleteData</c>/<c>InsertData</c> builder
/// calls rather than raw SQL, of which nothing shipped has an example; <c>MERGE INTO</c>, which
/// no shipped migration uses; a <c>LATERAL</c> loop source that is a bare function rather than
/// a subquery, since <c>CROSS JOIN LATERAL (SELECT … FROM x)</c> binds <c>x</c> off the inner
/// <c>FROM</c> but <c>CROSS JOIN LATERAL unnest(…)</c> has no inner <c>FROM</c> to bind; a loop
/// source whose leading token is not an identifier once C# escaping is undone, an interpolated
/// <c>{table}</c> hole being the shape that reaches here; and a tenant context established
/// earlier in <c>Up</c> for an unrelated statement — including a <c>set_config(…, '', false)</c>
/// that resets it — since ordering is checked once per <c>Up</c> rather than per statement.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class DataMigrationTenantContextGuardTests
{
    /// <summary>
    /// Migrations whose tenant loop is driven by a query assembled at runtime, which this guard
    /// cannot read. An entry here is a hand-review claim that the query touches no tenant-scoped
    /// table before the GUC is set; it is not a shipped-history amnesty, because nothing shipped
    /// uses the shape.
    /// </summary>
    private static readonly IReadOnlySet<string> KnownDynamicLoopMigrations =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly Regex LoopHeaderStart = new(
        @"FOR\s+\w+\s+IN\s+",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LoopTerminator = new(
        @"\bLOOP\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The source list after each <c>FROM</c> or <c>JOIN</c>, ending at the clause keyword that
    /// closes it. Taking every list, not just the first table of the first one, is what makes a
    /// CTE header visible: its outer <c>FROM</c> names the CTE, and the tenant-scoped table
    /// actually read is the <c>FROM</c> inside — and a scoped table reached by <c>JOIN</c> or by
    /// a comma is as blind as one reached by <c>FROM</c>.
    /// </summary>
    private static readonly Regex QuerySource = new(
        @"\b(FROM|JOIN)\s+(.*?)(?=\b(?:WHERE|GROUP|HAVING|WINDOW|ORDER|LIMIT|OFFSET|FETCH"
        + @"|UNION|INTERSECT|EXCEPT|RETURNING|LOOP|ON|USING|JOIN|LEFT|RIGHT|FULL|INNER|OUTER"
        + @"|CROSS|NATURAL|SELECT)\b|$)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// The table a comma-separated source-list item names, matched against the item with its
    /// C#-escaped quotes already undone so a quoted identifier is one. An item that does not
    /// start with an identifier — a row constructor in <c>FROM (VALUES …)</c>, a literal inside
    /// one — names no table and is skipped rather than resolved to a stray token.
    /// </summary>
    private static readonly Regex SourceItem = new(
        @"^\s*(?:ONLY\s+)?(""?[A-Za-z_][\w""$.]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DynamicLoopQuery = new(
        @"^\s*EXECUTE\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DmlTarget = new(
        @"\b(UPDATE|DELETE\s+FROM|INSERT\s+INTO)\s+(?:ONLY\s+)?(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TenantContext = new(
        @"set_config\s*\(\s*'app\.current_tenant_id'",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly IReadOnlySet<string> ScopedTables = MigrationSourceFiles.TenantScopedTableNames();

    [Fact]
    public void NoDataMigrationDrivesItsTenantLoopOffATenantScopedTable()
    {
        Offenders(ScopedLoopSources).Should().BeEmpty(
            "a tenant loop must be driven off the tenants table; reading its bounds from a "
            + "tenant-scoped table under FORCE RLS returns no rows and makes the migration a no-op");
    }

    [Fact]
    public void NoDataMigrationDrivesItsTenantLoopOffARuntimeBuiltQuery()
    {
        Offenders(DynamicLoopSources, KnownDynamicLoopMigrations).Should().BeEmpty(
            "a loop source assembled at runtime cannot be read here, so it has to be reviewed by "
            + "hand and named in the allowlist rather than passing unexamined");
    }

    [Fact]
    public void NoDataMigrationRunsTenantScopedDmlWithoutATenantContext()
    {
        Offenders(ContextlessScopedDml).Should().BeEmpty(
            "DML on a tenant-scoped table under FORCE RLS matches no row until "
            + "app.current_tenant_id is set, so the migration records as applied having done nothing");
    }

    [Fact]
    public void TheGuardCanSeeMigrationsAndTenantScopedTables()
    {
        // Either going empty makes the guards above pass vacuously. A named table catches the
        // narrower regression too: a set that still holds most tables but has quietly dropped some.
        MigrationSourceFiles.All().Should().NotBeEmpty();
        MigrationSourceFiles.TenantScopedTableNames().Should().Contain("sensor_glucose");
    }

    [Fact]
    public void EveryAllowlistedMigrationStillExistsAndStillOffends()
    {
        foreach (var (allowlist, detect) in Allowlists)
        {
            var stillOffending = MigrationSourceFiles.All()
                .Where(f => allowlist.Contains(MigrationSourceFiles.Name(f)))
                .Where(f => Findings(f, detect).Count > 0)
                .Select(MigrationSourceFiles.Name)
                .ToHashSet(StringComparer.Ordinal);

            stillOffending.Should().BeEquivalentTo(allowlist,
                "an allowlist entry that no longer matches is stale and hides a regression");
        }
    }

    [Fact]
    public void ACteLoopHeaderBindsTheTableInsideTheCte()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN
                    WITH stale AS (SELECT tenant_id FROM sensor_glucose WHERE device IS NULL)
                    SELECT * FROM stale
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void ACteLoopHeaderEndingAtTheTableNameStillBindsIt()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN
                    WITH stale AS (SELECT DISTINCT tenant_id FROM sensor_glucose)
                    SELECT * FROM stale
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void EveryLoopSourceIsBoundNotJustTheFirst()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT t.id FROM tenants t JOIN sensor_glucose sg ON sg.tenant_id = t.id
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("JOIN sensor_glucose");
    }

    [Fact]
    public void ACommaJoinedLoopSourceIsBound()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT t.id FROM tenants t, sensor_glucose sg WHERE sg.tenant_id = t.id
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void AnOnlyQualifiedLoopSourceIsBound()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT tenant_id FROM ONLY sensor_glucose
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void AQuotedSchemaQualifiedLoopSourceIsBound()
    {
        const string up = """
            migrationBuilder.Sql("DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT tenant_id FROM \"public\".\"sensor_glucose\"
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;");
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void AnOnlyQualifiedQuotedLoopSourceBindsTheTableAndNotTheOnly()
    {
        const string up = """
            migrationBuilder.Sql("DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT tenant_id FROM ONLY \"public\".\"sensor_glucose\"
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;");
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void TheWordLoopInsideAStringLiteralInTheHeaderDoesNotTruncateIt()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT t.id, 'LOOP' AS tag FROM tenants t JOIN sensor_glucose sg ON sg.tenant_id = t.id
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("JOIN sensor_glucose");
    }

    [Fact]
    public void ACommentedWordLoopInsideTheHeaderDoesNotTruncateIt()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN
                    -- one row per tenant, then loop over them
                    SELECT DISTINCT tenant_id FROM sensor_glucose
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ScopedLoopSources(up)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void AnExecuteLoopHeaderIsReportedRatherThanPassing()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN EXECUTE format('SELECT tenant_id FROM %I', 'sensor_glucose')
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        DynamicLoopSources(up).Should().ContainSingle();
        ScopedLoopSources(up).Should().BeEmpty("an EXECUTE header binds no table to read");
    }

    [Fact]
    public void LoopFreeDmlOnATenantScopedTableIsDetected()
    {
        const string up = "migrationBuilder.Sql(\"UPDATE sensor_glucose SET device = 'unknown';\");";

        Details(ContextlessScopedDml(up)).Should().Equal("UPDATE sensor_glucose");
    }

    [Fact]
    public void DmlTargetIsReadThroughQuotesSchemaQualifiersAndAClosingParen()
    {
        const string up = """
            migrationBuilder.Sql("DELETE FROM alert_invites")
            migrationBuilder.Sql("UPDATE \"sensor_glucose\" SET device = 'unknown';");
            migrationBuilder.Sql("UPDATE public.heart_rates SET rate = 0;");
            """;

        Details(ContextlessScopedDml(up)).Should().Equal(
            "DELETE FROM alert_invites", "UPDATE sensor_glucose", "UPDATE heart_rates");
    }

    [Fact]
    public void ATableNameEndedByACSharpEscapeIsStillRead()
    {
        const string dml = """
            migrationBuilder.Sql("UPDATE sensor_glucose\n  SET device = 'unknown';");
            """;
        const string loop = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT tenant_id FROM sensor_glucose\n  WHERE device IS NULL
                LOOP
                    PERFORM set_config('app.current_tenant_id', r.tenant_id::text, true);
                END LOOP;
            END $$;
            """;

        Details(ContextlessScopedDml(dml)).Should().Equal("UPDATE sensor_glucose");
        Details(ScopedLoopSources(loop)).Should().Equal("FROM sensor_glucose");
    }

    [Fact]
    public void DmlPrecededByATenantContextIsNotDetected()
    {
        const string up = """
            DO $$ DECLARE r RECORD; BEGIN
                FOR r IN SELECT id FROM tenants LOOP
                    PERFORM set_config('app.current_tenant_id', r.id::text, true);
                    UPDATE sensor_glucose SET device = 'unknown';
                END LOOP;
            END $$;
            """;

        ContextlessScopedDml(up).Should().BeEmpty();
    }

    [Fact]
    public void DmlPrecededOnlyByACommentedOutTenantContextIsDetected()
    {
        const string up = """
            // PERFORM set_config('app.current_tenant_id', r.id::text, true);
            migrationBuilder.Sql("UPDATE sensor_glucose SET device = 'unknown';");
            """;

        Details(ContextlessScopedDml(up)).Should().Equal("UPDATE sensor_glucose");
    }

    [Fact]
    public void DmlOnATableThatIsNotTenantScopedIsNotDetected()
    {
        const string up = "migrationBuilder.Sql(\"UPDATE tenants SET display_name = trim(display_name);\");";

        ContextlessScopedDml(up).Should().BeEmpty();
    }

    private static readonly (IReadOnlySet<string> Allowlist, Detector Detect)[] Allowlists =
    [
        (KnownDynamicLoopMigrations, DynamicLoopSources),
    ];

    private delegate IReadOnlyList<(string Table, string Detail)> Detector(string up);

    private static IReadOnlyList<string> Offenders(Detector detect, IReadOnlySet<string>? allowlist = null) =>
        MigrationSourceFiles.All()
            .Where(f => allowlist?.Contains(MigrationSourceFiles.Name(f)) != true)
            .SelectMany(f => Findings(f, detect))
            .ToList();

    private static IReadOnlyList<string> Findings(string file, Detector detect)
    {
        var migration = MigrationSourceFiles.Name(file);

        return detect(MigrationSourceFiles.UpBody(file))
            .Select(x => $"{migration} -> {x.Detail}")
            .ToList();
    }

    private static IEnumerable<string> Details(IEnumerable<(string Table, string Detail)> findings) =>
        findings.Select(x => x.Detail);

    /// <summary>
    /// Tenant-scoped tables the <c>Up</c> drives a tenant loop off. Scanned as raw text: this
    /// detects offenders, and <see cref="MigrationSourceFiles.WithCommentsBlanked"/> withholds
    /// evidence, so a stray <c>/*</c> inside a SQL literal would blank a live loop out of view. A
    /// commented-out loop is therefore reported — the cheap direction to be wrong in.
    /// </summary>
    private static IReadOnlyList<(string Table, string Detail)> ScopedLoopSources(string up) =>
        LoopQueries(up)
            .Where(q => !DynamicLoopQuery.IsMatch(q))
            .SelectMany(q => QuerySource.Matches(q).SelectMany(m => m.Groups[2].Value
                .Split(',')
                .Select(item => SourceItem.Match(MigrationSourceFiles.Unescaped(item)))
                .Where(item => item.Success)
                .Select(item => (Keyword: m.Groups[1].Value.ToUpperInvariant(),
                                 Table: MigrationSourceFiles.BareTableName(item.Groups[1].Value)))))
            .Where(x => ScopedTables.Contains(x.Table))
            .Select(x => (x.Table, $"{x.Keyword} {x.Table}"))
            .ToList();

    private static IReadOnlyList<(string Table, string Detail)> DynamicLoopSources(string up) =>
        LoopQueries(up)
            .Where(q => DynamicLoopQuery.IsMatch(q))
            .Select(_ => (string.Empty, "loop source assembled at runtime (EXECUTE)"))
            .ToList();

    /// <summary>
    /// DML naming a tenant-scoped table with no <c>set_config('app.current_tenant_id', …)</c>
    /// ahead of it in the same <c>Up</c>. Targets are read off raw text so a comment cannot hide
    /// one; the establishing call is read off comment-blanked text — which preserves offsets — so
    /// a commented-out one cannot excuse one.
    /// </summary>
    private static IReadOnlyList<(string Table, string Detail)> ContextlessScopedDml(string up)
    {
        var context = TenantContext.Match(MigrationSourceFiles.WithCommentsBlanked(up));
        var establishedAt = context.Success ? context.Index : int.MaxValue;

        return DmlTarget.Matches(up)
            .Where(m => m.Index < establishedAt)
            .Select(m => (Verb: Regex.Replace(m.Groups[1].Value.ToUpperInvariant(), @"\s+", " "),
                          Table: MigrationSourceFiles.BareTableName(m.Groups[2].Value)))
            .Where(x => ScopedTables.Contains(x.Table))
            .Select(x => (x.Table, $"{x.Verb} {x.Table}"))
            .ToList();
    }

    /// <summary>
    /// The driving query of each PL/pgSQL <c>FOR &lt;var&gt; IN &lt;query&gt; LOOP</c> header,
    /// whole rather than one table off it. A plain <c>SELECT</c>, a <c>WITH</c> that binds its
    /// real sources inside a CTE, and an <c>EXECUTE</c> that binds nothing are all this shape;
    /// telling them apart is the callers' job.
    /// <para>
    /// The header is found in raw text, so a stray <c>/*</c> cannot blank a live loop out of
    /// view, but its terminator is found in text with comments and single-quoted SQL literals
    /// blanked — which preserves offsets — so the word <c>loop</c> in a comment or a literal
    /// inside the header does not truncate the query short of the tables it reads. Any other
    /// spelling of <c>LOOP</c> the blanking does not cover (a dollar-quoted literal, say) still
    /// would. An unterminated header runs to the end of <c>Up</c>, binding more sources than the
    /// loop really has rather than fewer.
    /// </para>
    /// </summary>
    private static IEnumerable<string> LoopQueries(string up)
    {
        var blanked = MigrationSourceFiles.WithCommentsAndSqlLiteralsBlanked(up);

        foreach (Match header in LoopHeaderStart.Matches(up))
        {
            var query = header.Index + header.Length;
            var terminator = LoopTerminator.Match(blanked, query);
            yield return up[query..(terminator.Success ? terminator.Index : up.Length)];
        }
    }
}
