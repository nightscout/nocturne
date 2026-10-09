using FluentAssertions;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class A1cDisplayValueTests
{
    [Theory]
    [InlineData(5.7, 38.798)]
    [InlineData(7.0, 53.00565)]
    [InlineData(9.0, 74.86365)]
    public void Conversion_preserves_canonical_percent_and_round_trips(double percent, double mmolMol)
    {
        var display = A1cDisplayValue.FromPercent(percent);
        display.Percent.Should().Be(percent);
        display.MmolMol.Should().BeApproximately(mmolMol, 0.002);
        A1cDisplayValue.ToPercent(display.MmolMol).Should().BeApproximately(percent, 0.000001);
    }

    [Theory]
    [InlineData("HbA1c", "percent")]
    [InlineData("A1c", "mmol/mol")]
    public void Preferences_survive_storage_partial_updates_and_presentation_projection(string name, string units)
    {
        var preferences = new UserDisplayPreferences { A1cName = name, A1cUnits = units };
        preferences.Validate().Should().BeNull();
        var restored = UserDisplayPreferences.Deserialize(preferences.Serialize());
        restored.MergeWith(new UserDisplayPreferences { TimeFormat = "24" });
        var presentation = restored.ToPresentationOnly();
        presentation.A1cName.Should().Be(name);
        presentation.A1cUnits.Should().Be(units);
    }

    [Theory]
    [InlineData("eA1c", "percent")]
    [InlineData("GMI", "percent")]
    [InlineData("A1c", "mmol/L")]
    public void Preferences_reject_metric_names_and_glucose_units(string name, string units)
    {
        new UserDisplayPreferences { A1cName = name, A1cUnits = units }.Validate().Should().NotBeNull();
    }
}
