using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
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
        // The EF model builds offline (no connection); UseNpgsql gives the relational mapping
        // so GetTableName() resolves real table names, mirroring the reconciler at startup.
        var options = new DbContextOptionsBuilder<NocturneDbContext>()
            .UseNpgsql("Host=localhost;Database=nocturne;Username=test;Password=test")
            .Options;
        using var ctx = new NocturneDbContext(options);
        return ctx.Model;
    }

    [Fact]
    public void BuildPolicySql_GovernedTable_GatesOnIsShareAndCategory()
    {
        var sql = ShareRlsPolicy.BuildPolicySql("boluses", OAuthScopes.TreatmentsRead);

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
    public void TenantScopedTableNames_MatchesTableAttributeReflection()
    {
        // The reconciler resolves names from the EF model; the coverage guard resolves them from
        // [Table] attributes. They must agree, or a table could be policied under one source and
        // classified under the other. This fails the build if an entity ever maps its table via
        // ToTable() without a matching [Table] attribute.
        var fromModel = ShareRlsPolicy.TenantScopedTableNames(Model());

        var fromAttributes = typeof(ITenantScoped).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantScoped).IsAssignableFrom(t))
            .Select(t => t.GetCustomAttribute<TableAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal);

        fromModel.Should().BeEquivalentTo(fromAttributes);
    }
}
