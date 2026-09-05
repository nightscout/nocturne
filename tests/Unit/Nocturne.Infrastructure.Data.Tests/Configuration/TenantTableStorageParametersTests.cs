using System.Globalization;
using Nocturne.Infrastructure.Data.Configuration;

namespace Nocturne.Infrastructure.Data.Tests.Configuration;

[Trait("Category", "Unit")]
public class TenantTableStorageParametersTests
{
    [Fact]
    public void BuildSetSql_PinsTheParameterUnderALockTimeout()
    {
        var sql = TenantTableStorageParameters.BuildSetSql("linked_records");

        sql.Should().Contain("SET LOCAL lock_timeout = '3s';");
        sql.Should().Contain("ALTER TABLE linked_records SET (autovacuum_analyze_scale_factor = 0.01);");
    }

    [Fact]
    public void AnalyzeScaleFactor_IsCanonicalText()
    {
        // The drift query compares the stored option text verbatim; a non-canonical constant
        // (trailing zero, leading plus) would make every startup re-apply it.
        decimal.Parse(TenantTableStorageParameters.AnalyzeScaleFactor, CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture)
            .Should().Be(TenantTableStorageParameters.AnalyzeScaleFactor);
    }

    [Theory]
    [InlineData("boluses; DROP TABLE x")]
    [InlineData("bad-name")]
    [InlineData("Boluses")]
    [InlineData("")]
    public void BuildSetSql_UnsafeTable_Throws(string table)
    {
        var act = () => TenantTableStorageParameters.BuildSetSql(table);
        act.Should().Throw<ArgumentException>();
    }
}
