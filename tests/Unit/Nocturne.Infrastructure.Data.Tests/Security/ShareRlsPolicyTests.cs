using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Nocturne.Core.Models.Authorization;
using Nocturne.Infrastructure.Data.Security;

namespace Nocturne.Infrastructure.Data.Tests.Security;

[Trait("Category", "Unit")]
public class ShareRlsPolicyTests
{
    private static IModel Model()
    {
        using var ctx = OfflineDbContext.Create();
        return ctx.Model;
    }

    [Fact]
    public void BuildPolicySql_GovernedTable_GatesOnIsShareAndCategory()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("boluses", Scope.TreatmentsRead);

        sql.Should().Contain("CREATE POLICY share_category_read ON boluses AS RESTRICTIVE FOR SELECT");
        sql.Should().Contain("current_setting('app.is_share', true) IS DISTINCT FROM 'true'");
        sql.Should().Contain(
            "'treatments.read' = ANY(string_to_array(current_setting('app.visible_categories', true), ','))");
        sql.Should().Contain("DROP POLICY IF EXISTS share_category_read ON boluses");
        sql.Should().Contain("ENABLE ROW LEVEL SECURITY");
        sql.Should().Contain("FORCE ROW LEVEL SECURITY");
    }

    [Fact]
    public void BuildPolicySql_HiddenTable_GatesOnIsShareOnly()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("therapy_settings", null);

        sql.Should().Contain("current_setting('app.is_share', true) IS DISTINCT FROM 'true'");
        sql.Should().NotContain("visible_categories");
        sql.Should().NotContain("ANY(");
        sql.Should().NotContain("history_clamped", "a member sees a table no share can reach in full");
    }

    [Theory]
    [InlineData("boluses; DROP TABLE x")]
    [InlineData("bad-name")]
    [InlineData("Boluses")]
    [InlineData("")]
    public void BuildPolicySql_UnsafeTable_Throws(string table)
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(table, null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_UnsafeScope_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql("boluses", "treatments.read'; DROP POLICY x");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_RecencyColumn_ClampsSharesAndHistoryClampedConnectionsOutsideTheCategoryGate()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("boluses", Scope.TreatmentsRead, "timestamp");

        // The recency conjunct sits beside the category gate rather than inside its share branch,
        // so it can narrow a non-share connection too. A share is clamped unless it has full
        // history (fail-closed); anything else only when history_clamped says so (fail-open).
        // IS [NOT] DISTINCT FROM keeps each test non-null when its GUC is unset, so an unset
        // history_clamped cannot hide a row from an unclamped member.
        const string expected =
            "USING ((current_setting('app.is_share', true) IS DISTINCT FROM 'true'" +
            " OR 'treatments.read' = ANY(string_to_array(current_setting('app.visible_categories', true), ',')))" +
            " AND (\"timestamp\" >= now() - interval '24 hours'" +
            " OR NOT ((current_setting('app.is_share', true) IS NOT DISTINCT FROM 'true'" +
            " AND current_setting('app.share_full_history', true) IS DISTINCT FROM 'true')" +
            " OR current_setting('app.history_clamped', true) IS NOT DISTINCT FROM 'true')));";

        sql.Should().Contain(expected);
    }

    [Fact]
    public void BuildPolicySql_NoRecencyColumn_HasNoClamp()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("foods", Scope.FoodRead);

        sql.Should().NotContain("share_full_history");
        sql.Should().NotContain("history_clamped");
        sql.Should().NotContain("interval");
    }

    [Fact]
    public void BuildPolicySql_SpanEndColumn_ClampsByOverlapSoARunningSpanStaysVisible()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("state_spans", null, spanEndColumn: "end_timestamp");

        sql.Should().Contain(
            "USING (current_setting('app.is_share', true) IS DISTINCT FROM 'true'" +
            " AND ((\"end_timestamp\" IS NULL OR \"end_timestamp\" >= now() - interval '24 hours')" +
            " OR NOT ((current_setting('app.is_share', true) IS NOT DISTINCT FROM 'true'" +
            " AND current_setting('app.share_full_history', true) IS DISTINCT FROM 'true')" +
            " OR current_setting('app.history_clamped', true) IS NOT DISTINCT FROM 'true')));");
        sql.Should().NotContain("visible_categories");
    }

    [Fact]
    public void BuildPolicySql_UnixMillisRecencyColumn_ComparesWithTheWindowStartInMilliseconds()
    {
        var sql = ShareRlsPolicy.BuildPolicySql(
            "compression_low_suggestions", null, "end_mills", recencyIsUnixMillis: true);

        sql.Should().Contain(
            "USING (current_setting('app.is_share', true) IS DISTINCT FROM 'true'" +
            " AND (\"end_mills\" >= (extract(epoch from now() - interval '24 hours') * 1000)::bigint" +
            " OR NOT ((current_setting('app.is_share', true) IS NOT DISTINCT FROM 'true'" +
            " AND current_setting('app.share_full_history', true) IS DISTINCT FROM 'true')" +
            " OR current_setting('app.history_clamped', true) IS NOT DISTINCT FROM 'true')));");
    }

    [Fact]
    public void BuildPolicySql_UnixMillisWithoutARecencyColumn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "state_spans", null, spanEndColumn: "end_timestamp", recencyIsUnixMillis: true);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_Parent_ClampsByTheParentRowsTime()
    {
        var sql = ShareRlsPolicy.BuildPolicySql(
            "treatment_foods", null, parent: new ParentRecency("carb_intakes", "carb_intake_id"),
            parentRecencyColumn: "timestamp");

        sql.Should().Contain(
            "USING (current_setting('app.is_share', true) IS DISTINCT FROM 'true'" +
            " AND (NOT ((current_setting('app.is_share', true) IS NOT DISTINCT FROM 'true'" +
            " AND current_setting('app.share_full_history', true) IS DISTINCT FROM 'true')" +
            " OR current_setting('app.history_clamped', true) IS NOT DISTINCT FROM 'true')" +
            " OR EXISTS (SELECT 1 FROM carb_intakes p WHERE p.tenant_id = treatment_foods.tenant_id AND p.id = treatment_foods.\"carb_intake_id\"" +
            " AND p.\"timestamp\" >= now() - interval '24 hours')));");
        sql.Should().NotContain("visible_categories");
    }

    [Fact]
    public void BuildPolicySql_ParentSpan_ShowsThePartsOfASpanTheClampShows()
    {
        var sql = ShareRlsPolicy.BuildPolicySql(
            "sleep_stages", null, parent: new ParentRecency("sleep_sessions", "sleep_session_id"),
            parentSpanEndColumn: "end_time");

        sql.Should().Contain(
            " OR EXISTS (SELECT 1 FROM sleep_sessions p WHERE p.tenant_id = sleep_stages.tenant_id AND p.id = sleep_stages.\"sleep_session_id\"" +
            " AND (p.\"end_time\" IS NULL OR p.\"end_time\" >= now() - interval '24 hours'))));");
    }

    [Fact]
    public void BuildPolicySql_OpenSpanBound_ShowsAnOpenSpanOnlyWithinTheBound()
    {
        var sql = ShareRlsPolicy.BuildPolicySql(
            "in_app_notifications", null, spanEndColumn: "archived_at",
            openSpanBound: new OpenSpanBound("created_at", 7));

        sql.Should().Contain(
            " AND (((\"archived_at\" IS NULL AND \"created_at\" >= now() - interval '7 days')" +
            " OR \"archived_at\" >= now() - interval '24 hours') OR NOT (");
    }

    [Fact]
    public void BuildPolicySql_OpenSpanBoundWithoutASpan_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "notes", null, "timestamp", openSpanBound: new OpenSpanBound("created_at", 7));
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("created_at\"; DROP TABLE x", 7)]
    [InlineData("created_at", 0)]
    public void BuildPolicySql_InvalidOpenSpanBound_Throws(string startColumn, int days)
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "in_app_notifications", null, spanEndColumn: "archived_at", openSpanBound: new OpenSpanBound(startColumn, days));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_ParentWithBothClampColumns_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "sleep_stages", null, parent: new ParentRecency("sleep_sessions", "sleep_session_id"),
            parentRecencyColumn: "start_time", parentSpanEndColumn: "end_time");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_ParentWithAClampColumnOfItsOwn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "treatment_foods", null, "sys_created_at", parent: new ParentRecency("carb_intakes", "carb_intake_id"),
            parentRecencyColumn: "timestamp");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("carb_intakes; DROP TABLE x", "carb_intake_id", "timestamp")]
    [InlineData("carb_intakes", "carb_intake_id\"; DROP TABLE x", "timestamp")]
    [InlineData("carb_intakes", "carb_intake_id", "timestamp\"; DROP TABLE x")]
    public void BuildPolicySql_UnsafeParentIdentifier_Throws(string parentTable, string foreignKey, string parentColumn)
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "treatment_foods", null, parent: new ParentRecency(parentTable, foreignKey), parentRecencyColumn: parentColumn);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_ParentWithoutItsRecencyColumn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql(
            "treatment_foods", null, parent: new ParentRecency("carb_intakes", "carb_intake_id"));
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// The clamp classification names columns as strings, and a policy over a column that does not
    /// exist fails the reconciler at startup; this catches the typo before that, and checks a column
    /// marked as Unix milliseconds holds a number and every other clamp column a timestamp.
    /// </summary>
    [Fact]
    public void EveryClampColumn_ExistsOnItsTableWithTheMatchingType()
    {
        var model = Model();
        var tables = ShareRlsPolicy.TenantScopedTableNames(model);
        var problems = new List<string>();

        foreach (var table in tables)
        {
            var columns = model.GetEntityTypes()
                .Where(t => t.GetTableName() == table)
                .SelectMany(t => t.GetProperties())
                .GroupBy(p => p.GetColumnName(StoreObjectIdentifier.Table(table, null)) ?? p.GetColumnName())
                .ToDictionary(g => g.Key, g => Nullable.GetUnderlyingType(g.First().ClrType) ?? g.First().ClrType);

            void Expect(string? column, string role, params Type[] types)
            {
                if (column is null) return;
                if (!columns.TryGetValue(column, out var actual))
                    problems.Add($"{table}.{column} ({role}) is not a column");
                else if (!types.Contains(actual))
                    problems.Add($"{table}.{column} ({role}) is {actual.Name}");
            }

            Type[] timestamp = [typeof(DateTime), typeof(DateTimeOffset)];
            Expect(ShareDataCategories.RecencyColumnFor(table), "recency",
                ShareDataCategories.RecencyIsUnixMillis(table) ? [typeof(long)] : timestamp);
            Expect(ShareDataCategories.SpanEndColumnFor(table), "span end", timestamp);
            Expect(ShareDataCategories.OpenSpanBoundFor(table)?.StartColumn, "open-span start", timestamp);
            if (ShareDataCategories.ParentRecencyFor(table) is { } parent)
            {
                Expect(parent.ForeignKeyColumn, "foreign key", typeof(Guid));
                var references = model.GetEntityTypes()
                    .Where(t => t.GetTableName() == table)
                    .SelectMany(t => t.GetForeignKeys())
                    .Where(fk => fk.Properties
                        .Select(p => p.GetColumnName(StoreObjectIdentifier.Table(table, null)))
                        .Where(c => c != "tenant_id")
                        .SequenceEqual([parent.ForeignKeyColumn]))
                    .Select(fk => fk.PrincipalEntityType.GetTableName());
                if (!references.Contains(parent.Table))
                    problems.Add($"{table}.{parent.ForeignKeyColumn} is not a foreign key to {parent.Table}");
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void BuildPolicySql_RecencyAndSpanEndColumn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql("state_spans", null, "start_timestamp", "end_timestamp");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_UnsafeSpanEndColumn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql("state_spans", null, spanEndColumn: "end\"; DROP TABLE x");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildPolicySql_UnsafeRecencyColumn_Throws()
    {
        var act = () => ShareRlsPolicy.BuildPolicySql("boluses", Scope.TreatmentsRead, "timestamp\"; DROP TABLE x");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantScopedTableNames_IncludesTenantScoped_ExcludesGlobal()
    {
        var tables = ShareRlsPolicy.TenantScopedTableNames(Model());

        tables.Should().Contain("boluses");
        tables.Should().Contain("sensor_glucose");
        tables.Should().Contain("therapy_settings");
        // Global (non-ITenantScoped) tables must not get a per-tenant share policy.
        tables.Should().NotContain("tenants");
        tables.Should().NotContain("subjects");
    }

    [Fact]
    public void TenantScopedTableNames_CoversEveryTenantScopedEntity()
    {
        // Every caller takes its table set from here, so an entity this misses is unpoliced and
        // unguarded everywhere at once.
        var model = Model();
        var tables = ShareRlsPolicy.TenantScopedTableNames(model).ToHashSet(StringComparer.Ordinal);

        var uncovered = typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .Where(t => model.FindEntityType(t)?.GetTableName() is not { } table || !tables.Contains(table))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        uncovered.Should().BeEmpty();
        tables.Should().NotBeEmpty();
    }
}
