using FluentAssertions;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.Profiles.Resolvers;

/// <summary>
/// Which store <see cref="ProfileStoreTimeline"/> reads at a time, from the profile document in force then.
/// </summary>
public class ProfileStoreTimelineTests
{
    private const long Hour = 3_600_000;
    private const long T0 = 1_705_320_000_000;

    private static DateTime At(long offset) => DateTimeOffset.FromUnixTimeMilliseconds(T0 + offset).UtcDateTime;

    private static ProfileStoreRow Document(
        string id, long at, string store, bool isDefault = false, string? documentDefault = null) =>
        new(id, true, store, At(at), isDefault, documentDefault);

    private static ProfileStoreRow V4Row(long at, string store, bool isDefault = false) =>
        new(Guid.NewGuid().ToString(), false, store, At(at), isDefault);

    [Fact]
    public void WithNoDocument_DefaultIsReadAndASwitchReadsTheStoreItNames()
    {
        var timeline = ProfileStoreTimeline.From([]);

        timeline.DefaultAt(T0).Should().Be("Default");
        timeline.StoreFor("Weekend", T0).Should().Be("Weekend");
    }

    [Fact]
    public void ANewerDocumentNamingItsOwnStore_LeavesTheEarlierTimeOnTheEarlierDocumentsDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -72 * Hour, "Default"),
            Document("b", -2 * Hour, "Loop Profile", isDefault: true),
        ]);

        timeline.DefaultAt(T0 - 4 * Hour).Should().Be("Default");
        timeline.DefaultAt(T0 - Hour).Should().Be("Loop Profile");
        timeline.ChangesWithin(T0 - 24 * Hour, T0).Should().Equal(T0 - 2 * Hour);
    }

    [Fact]
    public void WithNoDefaultRowLeft_TheNewestDocumentDefaultsToItsStore()
    {
        var timeline = ProfileStoreTimeline.From([Document("a", -72 * Hour, "Loop Profile")]);

        timeline.DefaultAt(T0).Should().Be("Loop Profile");
    }

    [Fact]
    public void AnOlderDocument_DefaultsToTheChosenStore_ElseToTheDefaultOfTheDocumentBeforeIt()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -96 * Hour, "Night"),
            Document("b", -72 * Hour, "Default"),
            Document("b", -72 * Hour, "Night"),
            Document("c", -48 * Hour, "Day"),
            Document("c", -48 * Hour, "Night"),
            Document("d", -24 * Hour, "Day", isDefault: true),
        ]);

        timeline.DefaultAt(T0 - 84 * Hour).Should().Be("Night");
        timeline.DefaultAt(T0 - 60 * Hour).Should().Be("Night");
        timeline.DefaultAt(T0 - 36 * Hour).Should().Be("Day");
        timeline.DefaultAt(T0).Should().Be("Day");
    }

    [Fact]
    public void AStoreWrittenOnItsOwn_IsHeldButDoesNotMoveTheDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -72 * Hour, "Default", isDefault: true),
            V4Row(-24 * Hour, "Exercise"),
        ]);

        timeline.DefaultAt(T0).Should().Be("Default");
        timeline.StoreFor("Exercise", T0).Should().Be("Exercise");
        timeline.StoreFor("Exercise", T0 - 48 * Hour).Should().Be("Default");
    }

    [Fact]
    public void ADefaultPickedFromAnOlderDocument_IsTheNewestDocumentsDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -72 * Hour, "Old", isDefault: true),
            Document("b", -24 * Hour, "Default"),
        ]);

        timeline.DefaultAt(T0).Should().Be("Old");
        timeline.StoreFor("Old", T0).Should().Be("Old");
    }

    [Fact]
    public void ASwitchToAStoreTheDocumentInForceDoesNotHold_ReadsItsDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -72 * Hour, "Default"),
            Document("b", -24 * Hour, "Renamed", isDefault: true),
        ]);

        timeline.StoreFor("Default", T0 - 48 * Hour).Should().Be("Default");
        timeline.StoreFor("Default", T0).Should().Be("Renamed");
        timeline.Holds("Default", T0).Should().BeFalse();
        timeline.DefaultAt(T0 - 96 * Hour).Should().Be("Renamed", "before the first document the newest is in force");
        timeline.Holds("Default", T0 - 96 * Hour).Should().BeFalse();
    }

    [Fact]
    public void AnOlderDocument_ReadsItsRecordedDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -48 * Hour, "Day", documentDefault: "Night"),
            Document("a", -48 * Hour, "Night", documentDefault: "Night"),
            Document("b", -24 * Hour, "Day", isDefault: true, documentDefault: "Day"),
            Document("b", -24 * Hour, "Night", documentDefault: "Day"),
        ]);

        timeline.DefaultAt(T0 - 36 * Hour).Should().Be("Night");
        timeline.DefaultAt(T0).Should().Be("Day");
    }

    [Fact]
    public void ADefaultPickedSinceTheNewestUpload_IsTheNewestDocumentsDefault()
    {
        var timeline = ProfileStoreTimeline.From(
        [
            Document("a", -24 * Hour, "Day", documentDefault: "Day"),
            Document("a", -24 * Hour, "Night", isDefault: true, documentDefault: "Day"),
        ]);

        timeline.DefaultAt(T0).Should().Be("Night");
    }
}
