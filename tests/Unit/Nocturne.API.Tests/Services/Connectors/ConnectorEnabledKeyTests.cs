using FluentAssertions;
using Nocturne.Core.Contracts.Connectors;
using Xunit;

namespace Nocturne.API.Tests.Services.Connectors;

/// <summary>
/// <see cref="ConnectorEnabledKey"/> is the one reader and writer of the flag's spelling, so a
/// document an older writer spelled <c>Enabled</c> reads as the flag it holds.
/// </summary>
public class ConnectorEnabledKeyTests
{
    [Theory]
    [InlineData("""{"enabled":true}""", true)]
    [InlineData("""{"enabled":false}""", false)]
    [InlineData("""{"Enabled":false}""", false)]
    [InlineData("""{"ENABLED":false,"region":"eu"}""", false)]
    [InlineData("""{"Enabled":true}""", true)]
    [InlineData("""{"region":"eu"}""", true)]
    [InlineData("{}", true)]
    [InlineData("""{"enabled":"false"}""", false)]
    [InlineData("""{"enabled":null}""", false)]
    [InlineData("not json", false)]
    public void IsEnabled_ReadsTheFlagInAnyCaseAndAbsenceAsEnabled(string configuration, bool expected)
    {
        ConnectorEnabledKey.IsEnabled(configuration).Should().Be(expected);
    }

    [Fact]
    public void With_ReplacesEveryCasingWithOneLowercaseKeyAndKeepsTheRest()
    {
        var written = ConnectorEnabledKey.With("""{"Enabled":true,"ENABLED":true,"region":"eu"}""", false);

        written.Should().Be("""{"region":"eu","enabled":false}""");
    }

    [Theory]
    [InlineData("""{"region":"us","enabled":true}""", """{"enabled":false,"region":"eu"}""", """{"region":"us","enabled":false}""")]
    [InlineData("""{"region":"us"}""", """{"Enabled":false}""", """{"region":"us","enabled":false}""")]
    [InlineData("""{"region":"us","Enabled":false}""", """{"region":"eu"}""", """{"region":"us","enabled":false}""")]
    [InlineData("""{"region":"us","enabled":false}""", "not json", """{"region":"us","enabled":false}""")]
    [InlineData("""{"region":"us"}""", """{"region":"eu"}""", """{"region":"us"}""")]
    public void CarryOver_TakesTheStoredFlagElseTheSavedOneSpelledEnabled(string saved, string stored, string expected)
    {
        ConnectorEnabledKey.CarryOver(saved, stored).Should().Be(expected);
    }
}
