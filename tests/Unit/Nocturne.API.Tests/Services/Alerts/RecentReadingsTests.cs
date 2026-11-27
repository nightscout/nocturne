using FluentAssertions;
using Nocturne.API.Services.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Alerts;

/// <summary>
/// The rule live and replay select the recent readings by (docs/alerts/engine-semantics.md §4,
/// recent readings): one point per duplicate group of the newest reading's stream and uploader.
/// </summary>
[Trait("Category", "Unit")]
public class RecentReadingsTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
    private const string Dexcom = "dexcom-connector";
    private const string Xdrip = "xdrip";

    private static SensorGlucose Reading(string source, double minutesAgo, double mgdl, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Timestamp = Now.AddMinutes(-minutesAgo),
        Mgdl = mgdl,
        DataSource = source,
        Device = "g7",
    };

    private static IEnumerable<(double MinutesAgo, decimal Mgdl)> Points(
        SensorGlucose newest, params (SensorGlucose Reading, Guid? DuplicateOf)[] candidates) =>
        RecentReadings.Of(newest, candidates).Select(p => ((Now - p.At).TotalMinutes, p.Mgdl));

    [Fact]
    public void A_reading_its_uploader_sent_twice_is_one_point_its_groups_primary()
    {
        var first = Reading(Dexcom, 10, 110);
        var again = Reading(Dexcom, 10 - 5 / 60d, 111);
        var newest = Reading(Dexcom, 0, 100);

        Points(newest, (again, first.Id), (newest, null), (first, null))
            .Should().Equal((10d, 110m), (0d, 100m));
    }

    /// <summary>
    /// With two apps taking turns as primary, each copy of the uploader's own is one reading,
    /// whichever app's copy is the group's primary.
    /// </summary>
    [Fact]
    public void Two_uploaders_taking_turns_give_every_reading_once()
    {
        var d10 = Reading(Dexcom, 10, 120);
        var x10 = Reading(Xdrip, 10 - 3 / 60d, 120);
        var x5 = Reading(Xdrip, 5 - 3 / 60d, 110);
        var d5 = Reading(Dexcom, 5, 110);
        var d0 = Reading(Dexcom, 0, 100);
        var x0 = Reading(Xdrip, 0 - 3 / 60d, 100);
        (SensorGlucose, Guid?)[] candidates =
            [(d10, null), (x10, d10.Id), (x5, null), (d5, x5.Id), (d0, null), (x0, d0.Id)];

        Points(d0, candidates).Should().Equal((10d, 120m), (5d, 110m), (0d, 100m));
        RecentReadings.Of(x5, candidates).Select(p => p.At).Should().Equal(x10.Timestamp, x5.Timestamp);
    }

    /// <summary>
    /// A group the stream holds only hidden copies of is the earliest of them, the lowest id breaking
    /// a tie; the newest reading is itself whatever group it is in.
    /// </summary>
    [Fact]
    public void A_group_without_its_primary_is_its_earliest_copy_and_the_newest_reading_is_itself()
    {
        var otherPrimary = Guid.NewGuid();
        var later = Reading(Xdrip, 10 - 5 / 60d, 121);
        var earlier = Reading(Xdrip, 10, 120);
        var low = Reading(Xdrip, 7, 115, new Guid("00000000-0000-0000-0000-000000000001"));
        var high = Reading(Xdrip, 7, 116, new Guid("00000000-0000-0000-0000-000000000002"));
        var tiePrimary = Guid.NewGuid();
        var newestsPrimary = Reading(Xdrip, 0 + 4 / 60d, 99);
        var newest = Reading(Xdrip, 0, 100);

        Points(newest,
                (later, otherPrimary), (earlier, otherPrimary), (high, tiePrimary), (low, tiePrimary),
                (newestsPrimary, null), (newest, newestsPrimary.Id))
            .Should().Equal((10d, 120m), (7d, 115m), (0d, 100m));
    }
}
