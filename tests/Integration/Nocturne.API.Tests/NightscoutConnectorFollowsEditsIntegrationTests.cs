using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Connectors.Nightscout.Tests.TestSupport;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Tests.Shared.Connectors;
using Xunit;
using Xunit.Abstractions;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// The Nightscout connector, publishing into the real store, against a Nightscout 15.0.8
/// (<see cref="FakeNightscoutTreatments"/>). An override is first imported by a ranged sync, as a
/// re-import does, with a recent treatment beside it so the open-ended sync after it crawls only the
/// last minutes and reconciles only the last day. A change to the old override then reaches the store
/// only through the read of open spans by id.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class NightscoutConnectorFollowsEditsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
    : ApiIntegrationTestBase(fixture, output)
{
    private readonly FakeNightscoutTreatments _upstream = new("15.0.8");
    private readonly InMemoryConnectorStateStore _state = new();
    private readonly NightscoutLookups _lookups = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow.AddDays(-3).AddMinutes(-Random.Shared.Next(0, 600));
    private readonly NightscoutConnectorConfiguration _config = new()
    {
        Url = $"https://{Guid.NewGuid():N}.nightscout.example",
        ApiSecret = "integration-secret-0001",
    };

    private AsyncServiceScope OpenScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:nightscout-follows-edits");

    private async Task<SyncResult> SyncAsync(SyncRequest request)
    {
        await using var scope = OpenScope();
        var store = scope.ServiceProvider.GetRequiredService<IConnectorPublisher>();

        var publisher = new Mock<IConnectorPublisher>();
        publisher.Setup(p => p.IsAvailable).Returns(true);
        publisher.Setup(p => p.Glucose).Returns(store.Glucose);
        publisher.Setup(p => p.Treatments).Returns(store.Treatments);
        publisher.Setup(p => p.Device).Returns(store.Device);
        publisher.Setup(p => p.Metadata).Returns(store.Metadata);
        publisher.Setup(p => p.State).Returns(_state);

        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        var connector = new NightscoutConnectorService(
            new HttpClient(_upstream),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            registration.Object,
            _lookups,
            MockTenantAccessor.Create().Object,
            publisher.Object);
        return await connector.SyncDataAsync(request, _config, CancellationToken.None);
    }

    /// <summary>Imports what the upstream holds by a ranged sync from before <see cref="_started"/>, which reads no open spans.</summary>
    private async Task ImportAsync()
    {
        (await SyncAsync(new SyncRequest
        {
            From = _started.UtcDateTime.AddHours(-1),
            To = DateTime.UtcNow,
            DataTypes = [SyncDataType.CarbIntake],
        })).Success.Should().BeTrue();
        _upstream.IdReads.Should().BeEmpty();
    }

    private async Task CatchUpAsync() =>
        (await SyncAsync(new SyncRequest { DataTypes = [SyncDataType.CarbIntake] })).Success.Should().BeTrue();

    private string AddCarbs(DateTimeOffset at)
    {
        var id = MongoObjectId.NewObjectId();
        _upstream.Add(id, at, t =>
        {
            t["eventType"] = "Carb Correction";
            t["carbs"] = 12;
            t["enteredBy"] = "Loop";
        });
        return id;
    }

    private string AddRecentCarbs() => AddCarbs(DateTimeOffset.UtcNow.AddMinutes(-5));

    private static void Indefinite(JsonObject t)
    {
        t["eventType"] = "Temporary Override";
        t["enteredBy"] = "Loop";
        t["reason"] = "Pre-Meal";
        t["durationType"] = "indefinite";
    }

    private static void Ended(JsonObject t)
    {
        t.Remove("durationType");
        t["duration"] = 4320;
    }

    private string AddOverride()
    {
        var id = MongoObjectId.NewObjectId();
        _upstream.Add(id, _started, Indefinite);
        return id;
    }

    private Task<List<JsonObject>> ServedAtStartAsync() => ServedNearAsync(_started);

    /// <summary>Every treatment the v1 read serves within a second of <paramref name="at"/>.</summary>
    private async Task<List<JsonObject>> ServedNearAsync(DateTimeOffset at)
    {
        var from = at.AddSeconds(-1).UtcDateTime.ToString("o");
        var to = at.AddSeconds(1).UtcDateTime.ToString("o");
        var served = await AuthenticatedClient.GetFromJsonAsync<JsonArray>(
            $"/api/v1/treatments?count=50&find[created_at][$gte]={Uri.EscapeDataString(from)}&find[created_at][$lte]={Uri.EscapeDataString(to)}");
        return served!.Select(t => t!.AsObject()).ToList();
    }

    /// <summary>
    /// The only treatment the crawl reads again is the newest, which the store serves under an id of
    /// its own. The delete pass must still find the source's ids in what it served, or it has no
    /// treatment to prove the lookup with.
    /// </summary>
    [Fact]
    public async Task A_treatment_deleted_upstream_is_deleted_when_the_store_has_renamed_every_one_the_crawl_read()
    {
        var gone = DateTimeOffset.UtcNow.AddHours(-3);
        var goneId = AddCarbs(gone);
        AddRecentCarbs();
        await ImportAsync();
        (await ServedNearAsync(gone)).Should().ContainSingle();

        _upstream.Remove(goneId);
        await CatchUpAsync();

        (await ServedNearAsync(gone)).Should().BeEmpty();
    }

    /// <summary>
    /// The newest treatment is the canary and is looked up once, by its <c>_id</c> as the source served
    /// it. One the source served is never looked up as missing, whichever id the store has since given
    /// it. Only the treatment the source deleted is, by each field it can be found under.
    /// </summary>
    [Fact]
    public async Task A_treatment_the_source_served_is_not_looked_up_as_missing_and_the_one_it_deleted_is()
    {
        var gone = DateTimeOffset.UtcNow.AddHours(-3);
        var goneId = AddCarbs(gone);
        AddCarbs(DateTimeOffset.UtcNow.AddHours(-2));
        var recent = AddRecentCarbs();
        await ImportAsync();

        _upstream.Remove(goneId);
        await CatchUpAsync();

        (await ServedNearAsync(gone)).Should().BeEmpty();
        _upstream.IdReads.Select(read => read[(read.LastIndexOf('&') + 1)..]).Should().Equal(
            $"find[_id]={recent}", $"find[_id]={goneId}", $"find[id]={goneId}", $"find[identifier][$in][0]={goneId}");
    }

    [Fact]
    public async Task An_override_ended_upstream_days_after_it_began_is_served_with_its_duration()
    {
        var id = AddOverride();
        AddRecentCarbs();
        await ImportAsync();
        (await ServedAtStartAsync()).Should().ContainSingle().Which["durationType"]!.GetValue<string>().Should().Be("indefinite");

        _upstream.Edit(id, Ended);
        await CatchUpAsync();

        _upstream.IdReads.Should().Contain(read => read.Contains($"find[_id][$in][0]={id}"));
        var served = (await ServedAtStartAsync()).Should().ContainSingle().Subject;
        served["eventType"]!.GetValue<string>().Should().Be("Temporary Override");
        served["duration"]!.GetValue<double>().Should().BeApproximately(4320, 0.001);
        served.ContainsKey("durationType").Should().BeFalse();

        var reads = _upstream.IdReads.Count();
        await CatchUpAsync();
        _upstream.IdReads.Should().HaveCount(reads, "an override with a duration is no longer open");
    }

    /// <summary>
    /// An indefinite override a later one replaced: the store closes its span at the next one's start,
    /// but serves it as indefinite until the source gives it a duration.
    /// </summary>
    [Fact]
    public async Task An_override_a_later_one_replaced_is_served_with_the_duration_it_is_given()
    {
        var id = AddOverride();
        _upstream.Add(MongoObjectId.NewObjectId(), _started.AddHours(2), Indefinite);
        AddRecentCarbs();
        await ImportAsync();
        (await ServedAtStartAsync()).Should().ContainSingle().Which["durationType"]!.GetValue<string>().Should().Be("indefinite");

        _upstream.Edit(id, t =>
        {
            t.Remove("durationType");
            t["duration"] = 120;
        });
        await CatchUpAsync();

        var served = (await ServedAtStartAsync()).Should().ContainSingle().Subject;
        served["duration"]!.GetValue<double>().Should().BeApproximately(120, 0.001);
        served.ContainsKey("durationType").Should().BeFalse();
    }

    /// <summary>
    /// More open overrides than one sync reads. The next sync goes on from the position the last one
    /// kept, through a start two of them share, so the store's order and the position read back from
    /// the connector state agree to the tick.
    /// </summary>
    [Fact]
    public async Task Open_overrides_past_one_sync_are_read_by_the_next_from_where_it_stopped()
    {
        const int perSync = NightscoutConnectorServiceBase<NightscoutConnectorConfiguration>.OpenSpansPerSync;
        var ids = Enumerable.Range(0, perSync + 2).Select(i => $"6a0000000000000000{i:x6}").ToList();
        for (var i = 0; i < ids.Count; i++)
            _upstream.Add(ids[i], _started.AddMinutes(i == perSync ? perSync - 1 : i), Indefinite);
        AddRecentCarbs();
        await ImportAsync();

        await CatchUpAsync();
        var first = _upstream.IdReads.Should().ContainSingle().Subject;
        await CatchUpAsync();
        var second = _upstream.IdReads.Skip(1).Should().ContainSingle().Subject;

        ids.Take(perSync).Should().OnlyContain(id => first.Contains(id));
        ids.Skip(perSync).Should().OnlyContain(id => !first.Contains(id));
        second.Should().Contain($"find[_id][$in][0]={ids[perSync]}").And.Contain($"find[_id][$in][1]={ids[perSync + 1]}");
        second.Should().NotContain("[$in][2]");
        _state.Vendor.Should().NotContainKey("OpenSpansRecheckedThrough", "the newest were read");
    }

    [Fact]
    public async Task The_store_says_which_open_spans_have_no_end_yet()
    {
        var replaced = AddOverride();
        var running = MongoObjectId.NewObjectId();
        _upstream.Add(running, _started.AddHours(2), Indefinite);
        AddRecentCarbs();
        await ImportAsync();

        await using var scope = OpenScope();
        var open = await scope.ServiceProvider.GetRequiredService<IConnectorPublisher>().Treatments
            .GetOpenSpanTreatmentsAsync("nightscout-connector", null, 10, CancellationToken.None);

        open.Select(span => (span.LegacyId, span.Running)).Should().Equal((replaced, false), (running, true));
    }

    [Fact]
    public async Task An_override_the_source_marked_invalid_is_no_longer_served()
    {
        var id = AddOverride();
        AddRecentCarbs();
        await ImportAsync();
        (await ServedAtStartAsync()).Should().ContainSingle();

        _upstream.Edit(id, t => t["isValid"] = false);
        await CatchUpAsync();

        (await ServedAtStartAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_gone_from_the_source_is_no_longer_served()
    {
        var id = AddOverride();
        AddRecentCarbs();
        await ImportAsync();
        (await ServedAtStartAsync()).Should().ContainSingle();

        _upstream.Remove(id);
        await CatchUpAsync();

        (await ServedAtStartAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_the_user_deleted_stays_deleted_when_it_ends_upstream()
    {
        var id = AddOverride();
        AddRecentCarbs();
        await ImportAsync();
        (await AuthenticatedClient.DeleteAsync($"/api/v1/treatments/{id}")).IsSuccessStatusCode.Should().BeTrue();
        (await ServedAtStartAsync()).Should().BeEmpty();

        _upstream.Edit(id, Ended);
        await CatchUpAsync();

        _upstream.IdReads.Should().BeEmpty("a deleted override is not open");
        (await ServedAtStartAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// The user's own override, as write-back left it upstream on 15.0.7 and later: under a
    /// <c>_id</c> the upstream minted, with the override's key as <c>identifier</c>. Its import is
    /// recognised as the echo and stores nothing, so the connector holds no open span for it, and an
    /// end given to the copy upstream is not read back as a second override.
    /// </summary>
    [Fact]
    public async Task An_override_written_back_is_not_stored_again_when_its_copy_ends_upstream()
    {
        var key = MongoObjectId.NewObjectId();
        var created = _started.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        (await AuthenticatedClient.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new { _id = key, eventType = "Temporary Override", created_at = created, durationType = "indefinite", reason = "Exercise", enteredBy = "nocturne" },
        })).IsSuccessStatusCode.Should().BeTrue();

        var copy = MongoObjectId.NewObjectId();
        _upstream.Add(copy, _started, t =>
        {
            Indefinite(t);
            t["identifier"] = key;
            t["reason"] = "Exercise";
            t["enteredBy"] = "nocturne";
        });
        AddRecentCarbs();
        await ImportAsync();

        _upstream.Edit(copy, Ended);
        await CatchUpAsync();
        await CatchUpAsync();

        _upstream.IdReads.Should().NotContain(read => read.Contains(copy) || read.Contains(key),
            "only the connector's own spans are read again");
        var served = (await ServedAtStartAsync()).Should().ContainSingle().Subject;
        served["reason"]!.GetValue<string>().Should().Be("Exercise");
        served["durationType"]!.GetValue<string>().Should().Be("indefinite");
    }
}
