using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Nocturne.API.Configuration;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Configuration;

/// <summary><see cref="NightscoutJsonOptions.OmitIndefiniteDuration"/>.</summary>
public class NightscoutJsonOptionsTests
{
    private static JsonObject Serialize(Treatment treatment) =>
        JsonNode.Parse(JsonSerializer.Serialize(treatment, NightscoutJsonOptions.Create()))!.AsObject();

    [Fact]
    public void An_indefinite_override_without_a_duration_is_served_without_one()
    {
        var served = Serialize(new Treatment { EventType = "Temporary Override", DurationType = "indefinite" });

        served.ContainsKey("duration").Should().BeFalse();
        served["durationType"]!.GetValue<string>().Should().Be("indefinite");
    }

    /// <summary>AAPS reads a zero-duration Temporary Target as a cancel and a zero-duration Profile Switch as permanent.</summary>
    [Theory]
    [InlineData("Temporary Target")]
    [InlineData("Profile Switch")]
    [InlineData("Note")]
    public void A_zero_duration_on_a_treatment_that_is_not_indefinite_is_served(string eventType)
    {
        var served = Serialize(new Treatment { EventType = eventType, Duration = 0 });

        served["duration"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public void A_finite_duration_on_an_indefinite_override_is_served()
    {
        var served = Serialize(new Treatment
        {
            EventType = "Temporary Override", DurationType = "indefinite", Duration = 25,
        });

        served["duration"]!.GetValue<double>().Should().Be(25);
    }
}
