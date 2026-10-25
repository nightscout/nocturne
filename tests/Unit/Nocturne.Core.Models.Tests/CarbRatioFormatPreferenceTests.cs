using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests;

public class CarbRatioFormatPreferenceTests
{
    [Theory]
    [InlineData("1:x")]
    [InlineData("g/U")]
    [InlineData("U/10g")]
    public void Format_survives_storage_partial_updates_and_presentation_projection(string format)
    {
        var preferences = new UserDisplayPreferences { CarbRatioFormat = format };
        preferences.Validate().Should().BeNull();
        var restored = UserDisplayPreferences.Deserialize(preferences.Serialize());
        restored.MergeWith(new UserDisplayPreferences { TimeFormat = "24" });
        restored.ToPresentationOnly().CarbRatioFormat.Should().Be(format);
    }

    [Theory]
    [InlineData("U/g")]
    [InlineData("1:X")]
    [InlineData("")]
    public void Unknown_formats_are_rejected(string format)
    {
        new UserDisplayPreferences { CarbRatioFormat = format }.Validate().Should().Contain("carbRatioFormat");
    }
}
