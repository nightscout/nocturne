using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Nightscout.Configurations;
using Nocturne.Connectors.Nightscout.Services;
using Nocturne.Core.Models;
using Nocturne.Tests.Shared.Connectors;
using Xunit;
using Nocturne.Tests.Shared.Mocks;

namespace Nocturne.Connectors.Nightscout.Tests.Services;

/// <summary>
/// The crawl marks and the full-reconcile stamp kept in the connector's resume state, driven over
/// the shared rig.
/// </summary>
public class NightscoutConnectorStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 30, 0, TimeSpan.Zero);

    private static readonly string Treatments = $$"""
        [{"_id":"t1","syncIdentifier":"loop-t1","enteredBy":"Loop","eventType":"Carb Correction","carbs":20,"created_at":"{{Now.AddMinutes(-60).UtcDateTime:o}}"},
         {"_id":"t2","syncIdentifier":"loop-t2","enteredBy":"Loop","eventType":"Carb Correction","carbs":15,"created_at":"{{Now.AddMinutes(-30).UtcDateTime:o}}"}]
        """;

    private static readonly string Entries = $$"""
        [{"_id":"e1","type":"sgv","sgv":120,"date":{{Now.AddMinutes(-10).ToUnixTimeMilliseconds()}},"dateString":"{{Now.AddMinutes(-10).UtcDateTime:o}}"},
         {"_id":"e2","type":"sgv","sgv":125,"date":{{Now.AddMinutes(-5).ToUnixTimeMilliseconds()}},"dateString":"{{Now.AddMinutes(-5).UtcDateTime:o}}"}]
        """;

    private static readonly List<SyncDataType> Types = [SyncDataType.Glucose, SyncDataType.CarbIntake];

    private static NightscoutConnectorConfiguration Config() => new() { Url = "https://ns.example", ApiSecret = "secret" };

    /// <summary>
    /// Serves each collection's records to the first read of it and nothing after, as a crawl
    /// reaching the end. The sign-in probe reads one entry first.
    /// </summary>
    private static FakeHttpHandler Source() =>
        new FakeHttpHandler(_ => FakeHttpHandler.Json("[]"))
            .On("/api/v1/treatments.json", FakeHttpHandler.Json(Treatments), FakeHttpHandler.Json("[]"))
            .On("/api/v1/entries.json", FakeHttpHandler.Json(Entries), FakeHttpHandler.Json(Entries), FakeHttpHandler.Json("[]"));

    private static NightscoutConnectorService Service(FakeHttpHandler handler, IConnectorPublisher publisher)
    {
        var registration = new Mock<IConnectorRegistration<NightscoutConnectorConfiguration>>();
        registration.Setup(r => r.Defaults).Returns(new NightscoutConnectorConfiguration());

        return new NightscoutConnectorService(
            new HttpClient(handler),
            Mock.Of<IConnectorServerResolver<NightscoutConnectorConfiguration>>(),
            NullLogger<NightscoutConnectorService>.Instance,
            Mock.Of<IRetryDelayStrategy>(),
            registration.Object,
            new NightscoutLookups(),
            MockTenantAccessor.Create().Object,
            publisher,
            new FakeTimeProvider(Now));
    }

    [Fact]
    public async Task Second_sync_of_the_same_window_offers_every_record_again_and_stores_no_new_row()
    {
        var store = new StoringConnectorPublisher();
        var request = new SyncRequest { From = Now.UtcDateTime.AddHours(-2), To = Now.UtcDateTime, DataTypes = Types };

        (await Service(Source(), store).SyncDataAsync(request, Config(), CancellationToken.None)).Success.Should().BeTrue();
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        (await Service(Source(), store).SyncDataAsync(request, Config(), CancellationToken.None)).Success.Should().BeTrue();

        offered.Values.Sum().Should().Be(4, "two entries and two treatments");
        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(o => o.Key, o => o.Value * 2));
        store.Unkeyed.Should().BeEmpty();
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }

    /// <summary>
    /// A reset run after a scheduled one reads the window it was given, offers every record again
    /// and stores none twice. What it forgets is pinned by the reconcile and base reset tests.
    /// </summary>
    [Fact]
    public async Task A_reset_run_after_a_scheduled_run_offers_every_record_again()
    {
        var store = new StoringConnectorPublisher();
        var state = (InMemoryConnectorStateStore)store.State;

        (await Service(Source(), store).SyncDataAsync(Config(), CancellationToken.None)).Success.Should().BeTrue();
        var stored = store.Stored.Keys.ToList();
        var offered = store.Offered;
        state.Resume.Should().NotBeEmpty("a scheduled run records where it stands");

        var reset = SyncRequest.Reset(Now.UtcDateTime.AddHours(-2), Now.UtcDateTime, Types);
        (await Service(Source(), store).SyncDataAsync(reset, Config(), CancellationToken.None)).Success.Should().BeTrue();

        store.Offered.Should().BeEquivalentTo(offered.ToDictionary(
            o => o.Key, o => o.Value + offered.GetValueOrDefault(o.Key)));
        store.Stored.Keys.Should().BeEquivalentTo(stored);
    }

    /// <summary>
    /// A reset of glucose alone re-pulls only glucose, so it leaves the treatment family's crawl
    /// state, owed and reached, and the treatment reconcile stamp as they were.
    /// </summary>
    [Fact]
    public async Task A_reset_of_glucose_alone_leaves_the_treatment_marks_untouched()
    {
        var store = new StoringConnectorPublisher();
        var state = (InMemoryConnectorStateStore)store.State;
        var owedFrom = Now.UtcDateTime.AddYears(-2);
        state.Resume["Treatments"] = owedFrom;
        state.Resume["Treatments.GapFrom"] = owedFrom.AddDays(30);
        state.Resume["Treatments.GapTo"] = owedFrom.AddDays(60);
        state.Resume["Treatments.Through"] = Now.UtcDateTime.AddHours(-1);
        state.Resume["TreatmentsFullReconcile"] = Now.UtcDateTime.AddMinutes(-20);
        state.Resume["Glucose.Through"] = Now.UtcDateTime.AddHours(-1);
        var treatmentsBefore = state.Resume.Where(m => m.Key.StartsWith("Treatments", StringComparison.Ordinal)).ToList();

        var reset = SyncRequest.Reset(Now.UtcDateTime.AddHours(-2), Now.UtcDateTime, [SyncDataType.Glucose]);
        (await Service(Source(), store).SyncDataAsync(reset, Config(), CancellationToken.None)).Success.Should().BeTrue();

        state.Resume.Where(m => m.Key.StartsWith("Treatments", StringComparison.Ordinal))
            .Should().BeEquivalentTo(treatmentsBefore);
        state.Resume.Should().NotContainKey("Glucose.Through", "the reset run crawled glucose");
        store.Offered.Keys.Should().ContainSingle("the run fetched glucose alone");
    }

    [Fact]
    public async Task A_reset_whose_sign_in_is_refused_leaves_the_state_exactly_as_it_was()
    {
        var store = new StoringConnectorPublisher();
        var state = (InMemoryConnectorStateStore)store.State;
        state.Resume["Treatments"] = Now.UtcDateTime.AddYears(-2);
        state.Resume["Treatments.Through"] = Now.UtcDateTime.AddHours(-1);
        state.Resume["TreatmentsFullReconcile"] = Now.UtcDateTime.AddMinutes(-20);
        state.Resume["Glucose.Through"] = Now.UtcDateTime.AddHours(-1);
        state.Vendor["kept"] = System.Text.Json.Nodes.JsonNode.Parse("""{"at":1}""")!;
        var resumeBefore = new Dictionary<string, DateTime>(state.Resume);
        var vendorBefore = state.Vendor.ToDictionary(v => v.Key, v => v.Value.ToJsonString());
        var refusing = new FakeHttpHandler(_ => FakeHttpHandler.Status(System.Net.HttpStatusCode.Unauthorized));

        var result = await Service(refusing, store).SyncDataAsync(
            SyncRequest.Reset(null, Now.UtcDateTime, null), Config(), CancellationToken.None);

        result.Success.Should().BeFalse("the sign-in was refused");
        state.Resume.Should().BeEquivalentTo(resumeBefore);
        state.Vendor.ToDictionary(v => v.Key, v => v.Value.ToJsonString()).Should().BeEquivalentTo(vendorBefore);
        store.Offered.Should().BeEmpty();
    }
}
