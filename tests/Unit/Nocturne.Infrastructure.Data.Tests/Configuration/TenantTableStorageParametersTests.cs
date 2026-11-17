using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

[Trait("Category", "Unit")]
public class TenantTableStorageParametersTests
{
    [Fact]
    public void BuildSetSql_NamesEveryDriftedParameterInOneStatementUnderALockTimeout()
    {
        var sql = TenantTableStorageParameters.BuildSetSql("linked_records", TenantTableStorageParameters.All);

        sql.Should().Contain("SET LOCAL lock_timeout = '3s';");
        sql.Should().Contain(
            "ALTER TABLE linked_records SET (autovacuum_analyze_scale_factor = 0.01, autovacuum_vacuum_scale_factor = 0.02);");
    }

    [Fact]
    public void BuildSetSql_SetsOnlyTheDriftedParameter()
    {
        var sql = TenantTableStorageParameters.BuildSetSql(
            "linked_records", [TenantTableStorageParameters.VacuumScaleFactor]);

        sql.Should().Contain("ALTER TABLE linked_records SET (autovacuum_vacuum_scale_factor = 0.02);");
        sql.Should().NotContain("autovacuum_analyze_scale_factor");
    }

    [Fact]
    public void BuildSetSql_NoParametersOrAnUnpinnedOne_Throws()
    {
        var none = () => TenantTableStorageParameters.BuildSetSql("boluses", []);
        var unpinned = () => TenantTableStorageParameters.BuildSetSql(
            "boluses", [new TenantTableStorageParameters.Parameter("fillfactor", "50")]);

        none.Should().Throw<ArgumentException>();
        unpinned.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("boluses; DROP TABLE x")]
    [InlineData("bad-name")]
    [InlineData("Boluses")]
    [InlineData("")]
    public void BuildSetSql_UnsafeTable_Throws(string table)
    {
        var act = () => TenantTableStorageParameters.BuildSetSql(table, TenantTableStorageParameters.All);
        act.Should().Throw<ArgumentException>();
    }
}
