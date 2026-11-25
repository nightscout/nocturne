using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.MyFitnessPal.Configurations;
using Nocturne.Core.Models;
using Nocturne.Tests.Shared.Connectors;
using Xunit;

namespace Nocturne.Connectors.MyFitnessPal.Tests.Services;

/// <summary>
/// The full-walk stamp kept in the connector's resume state, driven over the shared rig.
/// </summary>
public class MyFitnessPalConnectorStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<(string Id, string Date)> Diary =
    [
        ("breakfast", "2026-07-17"),
        ("lunch", "2026-07-18"),
        ("dinner", "2026-07-19"),
    ];

    private static SyncRequest FoodRequest() => new() { DataTypes = [SyncDataType.Food] };

    [Fact]
    public async Task Second_sync_offers_every_entry_again_and_stores_no_new_row()
    {
        var store = new StoringConnectorPublisher();
        var fixture = new MyFitnessPalClockTests.Fixture(new FakeTimeProvider(Now), Diary, store);

        (await fixture.Service.SyncDataAsync(FoodRequest(), fixture.Config, CancellationToken.None)).Success.Should().BeTrue();
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        (await fixture.Service.SyncDataAsync(FoodRequest(), fixture.Config, CancellationToken.None)).Success.Should().BeTrue();

        stored.Should().HaveCount(Diary.Count);
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Stored.Keys.Should().BeEquivalentTo(stored);
        fixture.State.Resume.Keys.Should().Equal(MyFitnessPalConstants.FullWalkStamp);
    }

    /// <summary>
    /// A reset forgets the walk stamp before the run reads it, so the reset run walks the diary to
    /// its end and stamps the walk afresh; a run that is not a reset finds the stamp in place.
    /// </summary>
    [Fact]
    public async Task A_reset_run_forgets_the_walk_stamp_and_walks_again()
    {
        var writes = new List<(string Key, bool Present)>();
        InMemoryConnectorStateStore state = null!;
        state = new InMemoryConnectorStateStore
        {
            OnWrite = key => writes.Add((key, state.Resume.ContainsKey(key))),
        };
        var store = new StoringConnectorPublisher(state);
        var clock = new FakeTimeProvider(Now);
        var fixture = new MyFitnessPalClockTests.Fixture(clock, Diary, store);

        await fixture.Service.SyncDataAsync(FoodRequest(), fixture.Config, CancellationToken.None);
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        clock.Advance(TimeSpan.FromHours(1));
        writes.Clear();

        var reset = SyncRequest.Reset(Now.UtcDateTime.AddDays(-3), clock.GetUtcNow().UtcDateTime, [SyncDataType.Food]);
        (await fixture.Service.SyncDataAsync(reset, fixture.Config, CancellationToken.None)).Success.Should().BeTrue();

        writes.Should().Equal(
            (MyFitnessPalConstants.FullWalkStamp, false),
            (MyFitnessPalConstants.FullWalkStamp, true));
        state.Resume[MyFitnessPalConstants.FullWalkStamp].Should().Be(clock.GetUtcNow().UtcDateTime);
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }

    /// <summary>A token MyFitnessPal refuses, and one it issues without saying which account it is for.</summary>
    public static TheoryData<HttpStatusCode, string> RefusedSignIns => new()
    {
        { HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}""" },
        { HttpStatusCode.OK, """{"access_token":"token","refresh_token":"refresh","expires_in":7200}""" },
    };

    [Theory]
    [MemberData(nameof(RefusedSignIns))]
    public async Task A_reset_whose_sign_in_is_refused_keeps_the_walk_stamp(HttpStatusCode status, string tokenBody)
    {
        var state = new InMemoryConnectorStateStore();
        state.Resume[MyFitnessPalConstants.FullWalkStamp] = Now.UtcDateTime.AddHours(-1);
        var before = new Dictionary<string, DateTime>(state.Resume);
        var store = new StoringConnectorPublisher(state);
        var fixture = new MyFitnessPalClockTests.Fixture(
            new FakeTimeProvider(Now), Diary, store, new MyFitnessPalClockTests.DiaryHandler(Diary, (status, tokenBody)));

        var result = await fixture.Service.SyncDataAsync(
            SyncRequest.Reset(null, Now.UtcDateTime, [SyncDataType.Food]), fixture.Config, CancellationToken.None);

        result.Success.Should().BeFalse("the sign-in was refused");
        state.Resume.Should().BeEquivalentTo(before);
        store.Offered.Should().BeEmpty();
    }
}
