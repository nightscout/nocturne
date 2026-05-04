using FluentAssertions;
using Nocturne.Core.Models.Entries;
using Xunit;

namespace Nocturne.Core.Models.Tests.Entries;

public class EntryDomainLogicTests
{
    // ========================================================================
    // BuildDemoModeFilterQuery
    // ========================================================================

    [Fact]
    public void BuildDemoModeFilterQuery_DemoEnabled_NullQuery_ReturnsDemoFilter()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: true, existingQuery: null);

        result.Should().Contain("\"data_source\":\"demo-service\"");
        result.Should().StartWith("{").And.EndWith("}");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_DemoDisabled_NullQuery_ReturnsExcludeFilter()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: false, existingQuery: null);

        result.Should().Contain("\"data_source\":{\"$ne\":\"demo-service\"}");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_DemoEnabled_EmptyBraces_ReturnsDemoFilter()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: true, existingQuery: "{}");

        result.Should().Contain("\"data_source\":\"demo-service\"");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_MergesWithExistingJsonQuery()
    {
        var existing = "{\"type\":\"sgv\"}";

        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: true, existingQuery: existing);

        result.Should().Contain("\"data_source\":\"demo-service\"");
        result.Should().Contain("\"type\":\"sgv\"");
        result.Should().StartWith("{").And.EndWith("}");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_NonJsonQuery_ReturnsDemoFilterOnly()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: true, existingQuery: "not-json");

        result.Should().Contain("\"data_source\":\"demo-service\"");
        result.Should().StartWith("{").And.EndWith("}");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_WhitespaceQuery_ReturnsDemoFilter()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: false, existingQuery: "   ");

        result.Should().Contain("\"data_source\":{\"$ne\":\"demo-service\"}");
    }

    [Fact]
    public void BuildDemoModeFilterQuery_EmptyJsonObject_ReturnsDemoFilter()
    {
        var result = EntryDomainLogic.BuildDemoModeFilterQuery(demoEnabled: true, existingQuery: "{  }");

        result.Should().Contain("\"data_source\":\"demo-service\"");
    }

    // ========================================================================
    // ParseTimeRangeFromFind
    // ========================================================================

    [Fact]
    public void ParseTimeRangeFromFind_NullInput_ReturnsNulls()
    {
        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(null);

        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseTimeRangeFromFind_EmptyInput_ReturnsNulls()
    {
        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind("");

        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseTimeRangeFromFind_GteOnly_ReturnsFromOnly()
    {
        var json = """{"date":{"$gte":1700000000000}}""";

        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(json);

        from.Should().Be(1700000000000);
        to.Should().BeNull();
    }

    [Fact]
    public void ParseTimeRangeFromFind_LteOnly_ReturnsToOnly()
    {
        var json = """{"date":{"$lte":1700000000000}}""";

        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(json);

        from.Should().BeNull();
        to.Should().Be(1700000000000);
    }

    [Fact]
    public void ParseTimeRangeFromFind_BothGteAndLte_ReturnsBoth()
    {
        var json = """{"date":{"$gte":1700000000000,"$lte":1700100000000}}""";

        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(json);

        from.Should().Be(1700000000000);
        to.Should().Be(1700100000000);
    }

    [Fact]
    public void ParseTimeRangeFromFind_NoOperators_ReturnsNulls()
    {
        var json = """{"type":"sgv"}""";

        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(json);

        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseTimeRangeFromFind_MalformedJson_ReturnsNulls()
    {
        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind("not valid json");

        from.Should().BeNull();
        to.Should().BeNull();
    }

    [Fact]
    public void ParseTimeRangeFromFind_NestedInDifferentField_StillParsed()
    {
        var json = """{"mills":{"$gte":100,"$lte":200}}""";

        var (from, to) = EntryDomainLogic.ParseTimeRangeFromFind(json);

        from.Should().Be(100);
        to.Should().Be(200);
    }

    // ========================================================================
    // IsCommonEntryCount
    // ========================================================================

    [Theory]
    [InlineData(10, true)]
    [InlineData(50, true)]
    [InlineData(100, true)]
    [InlineData(1, false)]
    [InlineData(25, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(200, false)]
    public void IsCommonEntryCount_ReturnsExpected(int count, bool expected)
    {
        EntryDomainLogic.IsCommonEntryCount(count).Should().Be(expected);
    }

}
