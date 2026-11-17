using FluentAssertions;
using Nocturne.Core.Models.Configuration;
using Xunit;

namespace Nocturne.Core.Models.Tests.Configuration;

[Trait("Category", "Unit")]
public class DashboardRailPreferenceTests
{
    [Fact]
    public void MergeWith_replaces_the_rail_whole()
    {
        var existing = new UserDisplayPreferences
        {
            DashboardRail = new DashboardRailPreference
            {
                Order = [RailItemId.Cob, RailItemId.Iob],
                Hidden = [RailItemId.Trackers],
            },
        };

        existing.MergeWith(new UserDisplayPreferences
        {
            DashboardRail = new DashboardRailPreference { Order = [RailItemId.Loop] },
        });

        existing.DashboardRail!.Order.Should().Equal(RailItemId.Loop);
        existing.DashboardRail.Hidden.Should().BeNull("the arrangement is replaced whole, not merged");
    }

    [Fact]
    public void MergeWith_keeps_the_rail_when_incoming_omits_it()
    {
        var existing = new UserDisplayPreferences
        {
            DashboardRail = new DashboardRailPreference { Hidden = [RailItemId.Basal] },
        };

        existing.MergeWith(new UserDisplayPreferences { GlucoseUnits = "mmol" });

        existing.DashboardRail!.Hidden.Should().Equal(RailItemId.Basal);
    }

    [Fact]
    public void MergeWith_an_empty_rail_resets_it()
    {
        var existing = new UserDisplayPreferences
        {
            DashboardRail = new DashboardRailPreference
            {
                Order = [RailItemId.Loop],
                Hidden = [RailItemId.Basal],
            },
        };

        existing.MergeWith(new UserDisplayPreferences { DashboardRail = new DashboardRailPreference() });

        existing.DashboardRail!.Order.Should().BeNull();
        existing.DashboardRail.Hidden.Should().BeNull();
    }

    [Fact]
    public void The_rail_serializes_as_enum_names()
    {
        var prefs = new UserDisplayPreferences
        {
            DashboardRail = new DashboardRailPreference
            {
                Order = [RailItemId.Trackers, RailItemId.Reservoir],
                Hidden = [RailItemId.Cob],
            },
        };

        var json = prefs.Serialize();

        json.Should().Contain("\"dashboardRail\":{\"order\":[\"Trackers\",\"Reservoir\"],\"hidden\":[\"Cob\"]}");
        var restored = UserDisplayPreferences.Deserialize(json).DashboardRail!;
        restored.Order.Should().Equal(RailItemId.Trackers, RailItemId.Reservoir);
        restored.Hidden.Should().Equal(RailItemId.Cob);
    }

    /// <summary>
    /// A newer build may store a rail item or widget this one does not define. Read after a
    /// rollback, that costs the entry, not every preference.
    /// </summary>
    [Fact]
    public void Deserialize_drops_enum_values_this_build_does_not_define()
    {
        const string json = """
            {
              "glucoseUnits": "mmol",
              "dashboardTopWidgets": ["Tdd", "FutureWidget", 4, 999],
              "dashboardRail": { "order": ["Loop", "Insulin2030", "Cob"], "hidden": ["Basal", "Gone", null] }
            }
            """;

        var prefs = UserDisplayPreferences.Deserialize(json);

        prefs.GlucoseUnits.Should().Be("mmol");
        prefs.DashboardTopWidgets.Should().Equal(WidgetId.Tdd, WidgetId.Trackers);
        prefs.DashboardRail!.Order.Should().Equal(RailItemId.Loop, RailItemId.Cob);
        prefs.DashboardRail.Hidden.Should().Equal(RailItemId.Basal);
    }

    [Fact]
    public void ToPresentationOnly_withholds_the_rail()
    {
        var prefs = new UserDisplayPreferences
        {
            GlucoseUnits = "mmol",
            DashboardRail = new DashboardRailPreference { Hidden = [RailItemId.Trackers] },
        };

        var disclosed = prefs.ToPresentationOnly();

        disclosed.GlucoseUnits.Should().Be("mmol");
        disclosed.DashboardRail.Should().BeNull();
    }

    [Fact]
    public void Validate_accepts_every_value_once()
    {
        var prefs = new UserDisplayPreferences
        {
            DashboardTopWidgets = [.. Enum.GetValues<WidgetId>()],
            DashboardRail = new DashboardRailPreference
            {
                Order = [.. Enum.GetValues<RailItemId>()],
                Hidden = [RailItemId.Trackers],
            },
        };

        prefs.Validate().Should().BeNull();
    }

    public static TheoryData<UserDisplayPreferences, string> InvalidLists => new()
    {
        { new() { DashboardRail = new() { Order = [RailItemId.Cob, RailItemId.Cob] } }, "dashboardRail.order" },
        { new() { DashboardRail = new() { Hidden = [RailItemId.Loop, RailItemId.Loop] } }, "dashboardRail.hidden" },
        { new() { DashboardRail = new() { Order = [(RailItemId)42] } }, "dashboardRail.order" },
        { new() { DashboardRail = new() { Hidden = [.. Enum.GetValues<RailItemId>(), RailItemId.Cob] } }, "dashboardRail.hidden" },
        { new() { DashboardTopWidgets = [WidgetId.Tdd, WidgetId.Tdd] }, "dashboardTopWidgets" },
        { new() { DashboardTopWidgets = [(WidgetId)99] }, "dashboardTopWidgets" },
    };

    [Theory]
    [MemberData(nameof(InvalidLists))]
    public void Validate_rejects_unknown_repeated_or_too_many_values(
        UserDisplayPreferences prefs, string expectedField)
    {
        prefs.Validate().Should().NotBeNull().And.StartWith(expectedField);
    }
}
