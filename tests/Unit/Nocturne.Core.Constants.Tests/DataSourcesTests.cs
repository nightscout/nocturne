using FluentAssertions;
using Xunit;

namespace Nocturne.Core.Constants.Tests;

[Trait("Category", "Unit")]
public class DataSourcesTests
{
    [Theory]
    [InlineData(DataSources.TidepoolConnector, true)]
    [InlineData(DataSources.NightscoutConnector, false)]
    [InlineData(DataSources.GlookoConnector, false)]
    [InlineData(DataSources.Unknown, false)]
    [InlineData(null, false)]
    public void EmitsDuplicateEvents_IsTheTidepoolConnectorOnly(string? source, bool expected) =>
        DataSources.EmitsDuplicateEvents(source).Should().Be(expected);
}
