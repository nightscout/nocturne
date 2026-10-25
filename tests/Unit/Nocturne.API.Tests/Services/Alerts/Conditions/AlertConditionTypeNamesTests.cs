using FluentAssertions;
using Nocturne.API.Services.Alerts.Conditions;
using Nocturne.Core.Models.Alerts;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts.Conditions;

/// <summary>A condition type is named by exactly its wire string (docs/alerts/engine-semantics.md §1.2).</summary>
public class AlertConditionTypeNamesTests
{
    [Fact]
    public void Every_type_round_trips_through_its_wire_string()
    {
        foreach (var type in Enum.GetValues<AlertConditionType>())
            AlertConditionTypeNames.FromWireString(AlertConditionTypeNames.ToWireString(type)).Should().Be(type);
    }

    [Theory]
    [InlineData("Threshold")]
    [InlineData("RATE_OF_CHANGE")]
    [InlineData("RateOfChange")]
    [InlineData("1")]
    public void Any_other_spelling_names_no_type(string type) =>
        AlertConditionTypeNames.FromWireString(type).Should().BeNull();
}
