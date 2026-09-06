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
    public void BuildPolicySql_RecencyColumn_AddsThe24HourClampBehindFullHistory()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("boluses", Scope.TreatmentsRead, "timestamp");

        sql.Should().Contain("current_setting('app.share_full_history', true) = 'true'");
        sql.Should().Contain("\"timestamp\" >= now() - interval '24 hours'");
        // The clamp narrows the category unlock; it must never widen the is_share gate.
        sql.Should().Contain("current_setting('app.is_share', true) IS DISTINCT FROM 'true'");
    }

    [Fact]
    public void BuildPolicySql_NoRecencyColumn_HasNoClamp()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("foods", Scope.FoodRead);

        sql.Should().NotContain("share_full_history");
        sql.Should().NotContain("interval");
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
