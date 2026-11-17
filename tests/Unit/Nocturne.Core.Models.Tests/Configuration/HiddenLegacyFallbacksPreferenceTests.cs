using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class HiddenLegacyFallbacksPreferenceTests
{
    [Fact]
    public void MergeWith_replaces_the_list_whole()
    {
        var existing = new UserDisplayPreferences
        {
            HiddenLegacyFallbacks = [LegacyConsumableKind.Site, LegacyConsumableKind.Sensor],
        };

        existing.MergeWith(new UserDisplayPreferences
        {
            HiddenLegacyFallbacks = [LegacyConsumableKind.PumpBattery],
        });

        existing.HiddenLegacyFallbacks.Should().Equal(LegacyConsumableKind.PumpBattery);
    }

    [Fact]
    public void MergeWith_keeps_the_list_when_incoming_omits_it()
    {
        var existing = new UserDisplayPreferences { HiddenLegacyFallbacks = [LegacyConsumableKind.Reservoir] };

        existing.MergeWith(new UserDisplayPreferences { GlucoseUnits = "mmol" });

        existing.HiddenLegacyFallbacks.Should().Equal(LegacyConsumableKind.Reservoir);
    }

    [Fact]
    public void MergeWith_an_empty_list_hides_none()
    {
        var existing = new UserDisplayPreferences { HiddenLegacyFallbacks = [LegacyConsumableKind.Site] };

        existing.MergeWith(new UserDisplayPreferences { HiddenLegacyFallbacks = [] });

        existing.HiddenLegacyFallbacks.Should().BeEmpty();
    }

    [Fact]
    public void The_list_round_trips_as_enum_names()
    {
        var prefs = new UserDisplayPreferences
        {
            HiddenLegacyFallbacks = [LegacyConsumableKind.PumpBattery, LegacyConsumableKind.Site],
        };

        var json = prefs.Serialize();

        json.Should().Contain("\"hiddenLegacyFallbacks\":[\"PumpBattery\",\"Site\"]");
        UserDisplayPreferences.Deserialize(json).HiddenLegacyFallbacks
            .Should().Equal(LegacyConsumableKind.PumpBattery, LegacyConsumableKind.Site);
    }

    [Fact]
    public void Deserialize_drops_kinds_this_build_does_not_define()
    {
        const string json = """{ "hiddenLegacyFallbacks": ["Site", "Transmitter", 7, null, "Sensor"] }""";

        UserDisplayPreferences.Deserialize(json).HiddenLegacyFallbacks
            .Should().Equal(LegacyConsumableKind.Site, LegacyConsumableKind.Sensor);
    }

    [Fact]
    public void ToPresentationOnly_withholds_the_list()
    {
        var prefs = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            HiddenLegacyFallbacks = [LegacyConsumableKind.Sensor],
        };

        var disclosed = prefs.ToPresentationOnly();

        disclosed.GlucoseUnits.Should().Be("mmol");
        disclosed.HiddenLegacyFallbacks.Should().BeNull();
    }

    [Fact]
    public void Validate_accepts_every_kind_once()
    {
        var prefs = new UserDisplayPreferences
        {
            HiddenLegacyFallbacks = [.. Enum.GetValues<LegacyConsumableKind>()],
        };

        prefs.Validate().Should().BeNull();
    }

    public static TheoryData<List<LegacyConsumableKind>> InvalidLists => new()
    {
        { [LegacyConsumableKind.Site, LegacyConsumableKind.Site] },
        { [(LegacyConsumableKind)42] },
        { [.. Enum.GetValues<LegacyConsumableKind>(), LegacyConsumableKind.Sensor] },
    };

    [Theory]
    [MemberData(nameof(InvalidLists))]
    public void Validate_rejects_unknown_repeated_or_too_many_kinds(List<LegacyConsumableKind> kinds)
    {
        var prefs = new UserDisplayPreferences { HiddenLegacyFallbacks = kinds };

        prefs.Validate().Should().NotBeNull().And.StartWith("hiddenLegacyFallbacks");
    }
}
