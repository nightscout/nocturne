using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.Basal;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Cache.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Every therapy resolver reads the store in force at the time asked: the one a profile switch names
/// when the profile document in force then holds it, else that document's default store, as
/// Nightscout's <c>profileFromTime</c> and <c>activeProfileToTime</c> do, whatever the store is
/// called, on real PostgreSQL.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class DefaultProfileStoreResolutionIntegrationTests : ApiIntegrationTestBase
{
    public DefaultProfileStoreResolutionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>
    /// The resolvers' tenant caches outlive the rows each test truncates, and these tests reuse store
    /// names, so each starts with them empty.
    /// </remarks>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (Fixture.Services.GetRequiredService<IMemoryCache>() as MemoryCache)?.Compact(1.0);
        await Fixture.Services.GetRequiredService<ICacheService>().RemoveByPatternAsync($"{Fixture.TenantId:D}:*");
    }

    private sealed record Store(double Sens, double CarbRatio, double Basal, double Low, double High, double Dia, double CarbsHr);

    private static readonly Store Day = new(40, 8, 0.65, 100, 110, 5.5, 25);
    private static readonly Store Night = new(70, 12, 0.3, 120, 130, 4.5, 35);
    private static readonly Store Old = new(45, 9, 0.75, 95, 105, 6, 28);

    private readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static object StoreData(Store s) => StoreData(s, "mg/dl");

    private static object StoreData(Store s, string units) => new
    {
        dia = s.Dia,
        carbs_hr = s.CarbsHr,
        timezone = "UTC",
        units,
        basal = new[] { new { time = "00:00", value = s.Basal } },
        carbratio = new[] { new { time = "00:00", value = s.CarbRatio } },
        sens = new[] { new { time = "00:00", value = s.Sens } },
        target_low = new[] { new { time = "00:00", value = s.Low } },
        target_high = new[] { new { time = "00:00", value = s.High } },
    };

    private static object Document(string id, string defaultProfile, DateTimeOffset startDate, Dictionary<string, Store> stores) =>
        RawDocument(id, defaultProfile, startDate, stores.ToDictionary(s => s.Key, s => StoreData(s.Value)));

    private static object RawDocument(string id, string defaultProfile, DateTimeOffset startDate, Dictionary<string, object> stores) => new
    {
        _id = id,
        defaultProfile,
        startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        mills = startDate.ToUnixTimeMilliseconds(),
        store = stores,
    };

    private static async Task SwitchAsync(HttpClient client, string profile, DateTimeOffset at, int minutes) =>
        (await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile,
                duration = minutes,
                created_at = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = at.ToUnixTimeMilliseconds(),
                enteredBy = "careportal",
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

    private async Task<HttpClient> SeedAsync(params object[] documents)
    {
        var client = CreateAuthenticatedClient();
        foreach (var document in documents)
            (await client.PostAsJsonAsync("/api/v1/profile", document)).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private sealed record Answers(
        double Sens, double CarbRatio, double Basal, double BatchBasal, (double, double) Targets,
        double Dia, double CarbsHr, double TimelineSens, double TimelineCarbRatio, double TimelineBasal,
        double TimelineDia, IReadOnlyList<BasalSegment> Segments);

    private Task<Answers> ResolveAtAsync(long at) =>
        WithTenantScopeAsync(async services =>
        {
            var batch = await services.GetRequiredService<IBasalRateResolver>()
                .BuildResolverAsync(at - 3_600_000, at + 1);
            var timeline = await services.GetRequiredService<ITherapyTimelineResolver>().GetSnapshotAtAsync(at);
            var segments = new List<BasalSegment>();
            await foreach (var segment in services.GetRequiredService<IBasalSegmentService>()
                               .GetSegmentsAsync(at - 3_600_000, at))
                segments.Add(segment);

            return new Answers(
                await services.GetRequiredService<ISensitivityResolver>().GetSensitivityAsync(at),
                await services.GetRequiredService<ICarbRatioResolver>().GetCarbRatioAsync(at),
                await services.GetRequiredService<IBasalRateResolver>().GetBasalRateAsync(at),
                batch(at),
                await services.GetRequiredService<ITargetRangeResolver>().GetBGTargetRangeAsync(at),
                await services.GetRequiredService<ITherapySettingsResolver>().GetDIAAsync(at),
                await services.GetRequiredService<ITherapySettingsResolver>().GetCarbAbsorptionRateAsync(at),
                timeline.SensitivityAt(at),
                timeline.CarbRatioAt(at),
                timeline.BasalRateAt(at),
                timeline.Dia,
                segments);
        });

    private static void ShouldBe(Answers answers, Store store, string name)
    {
        answers.Sens.Should().Be(store.Sens);
        answers.CarbRatio.Should().Be(store.CarbRatio);
        answers.Basal.Should().Be(store.Basal);
        answers.BatchBasal.Should().Be(store.Basal);
        answers.Targets.Should().Be((store.Low, store.High));
        answers.Dia.Should().Be(store.Dia);
        answers.CarbsHr.Should().Be(store.CarbsHr);
        answers.TimelineSens.Should().Be(store.Sens);
        answers.TimelineCarbRatio.Should().Be(store.CarbRatio);
        answers.TimelineBasal.Should().Be(store.Basal);
        answers.TimelineDia.Should().Be(store.Dia);
        answers.Segments.Should().NotBeEmpty()
            .And.OnlyContain(s => s.UnitsPerHour == store.Basal && s.ProfileName == name);
    }

    [Fact]
    public async Task TheOnlyStore_NamedOtherThanDefault_IsRead()
    {
        await SeedAsync(Document("5f8d0c1e8a7b4c3d9e5f1876", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));

        ShouldBe(await ResolveAtAsync(Now.AddHours(-1).ToUnixTimeMilliseconds()), Day, "Loop Profile");
    }

    [Fact]
    public async Task OfSeveralStores_TheOneTheDocumentNamesItsDefault_IsRead()
    {
        await SeedAsync(Document(
            "5f8d0c1e8a7b4c3d9e5f1877", "Night", Now.AddDays(-2), new() { ["Day"] = Day, ["Night"] = Night }));

        ShouldBe(await ResolveAtAsync(Now.AddHours(-1).ToUnixTimeMilliseconds()), Night, "Night");
    }

    [Fact]
    public async Task ANewerDocument_MovesTheDefaultToTheStoreItNames()
    {
        var stores = new Dictionary<string, Store> { ["Day"] = Day, ["Night"] = Night };
        await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f1878", "Night", Now.AddDays(-3), stores),
            Document("5f8d0c1e8a7b4c3d9e5f1879", "Day", Now.AddDays(-2), stores));

        ShouldBe(await ResolveAtAsync(Now.AddHours(-1).ToUnixTimeMilliseconds()), Day, "Day");
    }

    [Fact]
    public async Task OutsideAProfileSwitch_TheDefaultStoreIsRead_AndInsideIt_TheSwitchedOne()
    {
        var client = await SeedAsync(Document(
            "5f8d0c1e8a7b4c3d9e5f187a", "Night", Now.AddDays(-2), new() { ["Day"] = Day, ["Night"] = Night }));
        var switchedAt = Now.AddHours(-3);
        (await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile = "Day",
                duration = 60,
                created_at = switchedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = switchedAt.ToUnixTimeMilliseconds(),
                enteredBy = "careportal",
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var inside = await WithTenantScopeAsync(async services =>
            await services.GetRequiredService<ISensitivityResolver>()
                .GetSensitivityAsync(switchedAt.AddMinutes(30).ToUnixTimeMilliseconds()));
        inside.Should().Be(Day.Sens);

        ShouldBe(await ResolveAtAsync(Now.AddMinutes(-30).ToUnixTimeMilliseconds()), Night, "Night");

        var acrossTheEnd = await WithTenantScopeAsync(async services =>
        {
            var segments = new List<BasalSegment>();
            await foreach (var segment in services.GetRequiredService<IBasalSegmentService>().GetSegmentsAsync(
                               switchedAt.AddMinutes(30).ToUnixTimeMilliseconds(),
                               switchedAt.AddMinutes(90).ToUnixTimeMilliseconds()))
                segments.Add(segment);
            var batch = await services.GetRequiredService<IBasalRateResolver>().BuildResolverAsync(
                switchedAt.AddMinutes(30).ToUnixTimeMilliseconds(), switchedAt.AddMinutes(90).ToUnixTimeMilliseconds());
            return (segments, Before: batch(switchedAt.AddMinutes(45).ToUnixTimeMilliseconds()),
                After: batch(switchedAt.AddMinutes(75).ToUnixTimeMilliseconds()));
        });
        acrossTheEnd.Before.Should().Be(Day.Basal);
        acrossTheEnd.After.Should().Be(Night.Basal);
        acrossTheEnd.segments.Select(s => (s.ProfileName, s.UnitsPerHour)).Distinct()
            .Should().Equal(("Day", Day.Basal), ("Night", Night.Basal));
    }

    [Fact]
    public async Task ADefaultStoreFirstUploadedInsideTheWindow_LeavesTheEarlierTimeOnTheStoreInForceThen()
    {
        var uploaded = Now.AddHours(-2);
        await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f187b", "Default", Now.AddDays(-3), new() { ["Default"] = Old }),
            Document("5f8d0c1e8a7b4c3d9e5f187c", "Loop Profile", uploaded, new() { ["Loop Profile"] = Day }));

        var before = Now.AddHours(-4).ToUnixTimeMilliseconds();
        var after = Now.AddHours(-1).ToUnixTimeMilliseconds();
        ShouldBe(await ResolveAtAsync(before), Old, "Default");
        ShouldBe(await ResolveAtAsync(after), Day, "Loop Profile");

        var range = await WithTenantScopeAsync(async services =>
        {
            var from = Now.AddHours(-24).ToUnixTimeMilliseconds();
            var to = Now.ToUnixTimeMilliseconds();
            var batch = await services.GetRequiredService<IBasalRateResolver>().BuildResolverAsync(from, to);
            var timeline = await services.GetRequiredService<ITherapyTimelineResolver>().BuildAsync(from, to + 1);
            var segments = new List<BasalSegment>();
            await foreach (var segment in services.GetRequiredService<IBasalSegmentService>().GetSegmentsAsync(from, to))
                segments.Add(segment);
            return (batch, timeline, segments);
        });

        range.batch(before).Should().Be(Old.Basal);
        range.batch(after).Should().Be(Day.Basal);
        foreach (var (at, store) in new[] { (before, Old), (after, Day) })
        {
            var snapshot = range.timeline.SnapshotAt(at);
            snapshot.SensitivityAt(at).Should().Be(store.Sens);
            snapshot.CarbRatioAt(at).Should().Be(store.CarbRatio);
            snapshot.BasalRateAt(at).Should().Be(store.Basal);
            snapshot.Dia.Should().Be(store.Dia);
        }
        range.segments.Select(s => (s.ProfileName, s.UnitsPerHour)).Distinct()
            .Should().Equal(("Default", Old.Basal), ("Loop Profile", Day.Basal));
    }

    [Fact]
    public async Task ASwitchToAStoreThatDoesNotExist_ReadsTheDefaultStore()
    {
        var client = await SeedAsync(Document(
            "5f8d0c1e8a7b4c3d9e5f187d", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));
        await SwitchAsync(client, "Default", Now.AddHours(-3), 120);

        ShouldBe(await ResolveAtAsync(Now.AddHours(-2).ToUnixTimeMilliseconds()), Day, "Loop Profile");
    }

    [Fact]
    public async Task ASwitchToAStoreTheDocumentInForceDropped_ReadsTheDefaultStore()
    {
        var client = await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f187e", "Default", Now.AddDays(-3), new() { ["Default"] = Old }),
            Document("5f8d0c1e8a7b4c3d9e5f187f", "Renamed", Now.AddDays(-1), new() { ["Renamed"] = Day }));
        await SwitchAsync(client, "Default", Now.AddHours(-3), 120);

        ShouldBe(await ResolveAtAsync(Now.AddHours(-2).ToUnixTimeMilliseconds()), Day, "Renamed");
    }

    [Fact]
    public async Task DeletingTheNewestDocument_LeavesTheNextNewestDocumentsStoreAsTheDefault()
    {
        var client = await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f1880", "Loop Profile", Now.AddDays(-3), new() { ["Loop Profile"] = Day }),
            Document("5f8d0c1e8a7b4c3d9e5f1881", "Other", Now.AddDays(-1), new() { ["Other"] = Night }));
        (await client.DeleteAsync("/api/v1/profile/5f8d0c1e8a7b4c3d9e5f1881")).StatusCode.Should().Be(HttpStatusCode.OK);

        ShouldBe(await ResolveAtAsync(Now.AddHours(-1).ToUnixTimeMilliseconds()), Day, "Loop Profile");
    }

    [Fact]
    public async Task AnMmolDefaultStoreNamedOtherThanDefault_IsReadInMgdl()
    {
        var mmol = new
        {
            dia = 6.0,
            carbs_hr = 28,
            timezone = "UTC",
            units = "mmol",
            basal = new[] { new { time = "00:00", value = 0.75 } },
            carbratio = new[] { new { time = "00:00", value = 9.0 } },
            sens = new[] { new { time = "00:00", value = 2.5 } },
            target_low = new[] { new { time = "00:00", value = 5.5 } },
            target_high = new[] { new { time = "00:00", value = 6.5 } },
        };
        await SeedAsync(RawDocument(
            "5f8d0c1e8a7b4c3d9e5f1882", "Mmol Profile", Now.AddDays(-2), new Dictionary<string, object> { ["Mmol Profile"] = mmol }));

        var at = Now.AddHours(-1).ToUnixTimeMilliseconds();
        ShouldBe(await ResolveAtAsync(at), new Store(45, 9, 0.75, 99, 117, 6, 28), "Mmol Profile");
        (await WithTenantScopeAsync(services => services.GetRequiredService<ITherapySettingsResolver>().GetUnitsAsync()))
            .Should().Be("mmol");
    }

    [Fact]
    public async Task AnEmptyBatchRange_ReadsTheStoreAtItsInstant()
    {
        await SeedAsync(Document("5f8d0c1e8a7b4c3d9e5f1883", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));
        var at = Now.AddHours(-1).ToUnixTimeMilliseconds();

        var rate = await WithTenantScopeAsync(async services =>
            (await services.GetRequiredService<IBasalRateResolver>().BuildResolverAsync(at, at))(at));

        rate.Should().Be(Day.Basal);
    }

    [Fact]
    public async Task AScheduledBasalPublishedAlone_KeepsItsOriginAndReadsTheProgrammedRate()
    {
        await SeedAsync(Document("5f8d0c1e8a7b4c3d9e5f1884", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));
        var start = Now.AddHours(-1).UtcDateTime;

        var stored = await WithTenantScopeAsync(async services =>
        {
            var published = await services.GetRequiredService<ITreatmentPublisher>().PublishTempBasalsAsync(
                [
                    new TempBasal
                    {
                        LegacyId = "scheduled-basal-1876",
                        StartTimestamp = start,
                        EndTimestamp = start.AddMinutes(30),
                        Rate = Day.Basal,
                        Origin = TempBasalOrigin.Scheduled,
                        DataSource = "test-connector",
                    },
                ],
                "test-connector",
                WriteOrigin.Live);
            published.Should().BeTrue();
            return await services.GetRequiredService<ITempBasalRepository>().GetByLegacyIdAsync("scheduled-basal-1876");
        });

        stored!.Origin.Should().Be(TempBasalOrigin.Scheduled);
        stored.ScheduledRate.Should().Be(Day.Basal);
    }

    [Fact]
    public async Task BeforeTheFirstProfileDocument_TheNewestDocumentIsRead()
    {
        await SeedAsync(Document("5f8d0c1e8a7b4c3d9e5f1885", "Loop Profile", Now.AddHours(-1), new() { ["Loop Profile"] = Day }));
        var past = Now.AddDays(-60).ToUnixTimeMilliseconds();

        ShouldBe(await ResolveAtAsync(past), Day, "Loop Profile");

        var range = await WithTenantScopeAsync(async services =>
        {
            var from = Now.AddDays(-90).ToUnixTimeMilliseconds();
            var to = Now.ToUnixTimeMilliseconds();
            var batch = await services.GetRequiredService<IBasalRateResolver>().BuildResolverAsync(from, to);
            var timeline = await services.GetRequiredService<ITherapyTimelineResolver>().BuildAsync(from, to + 1);
            return (Basal: batch(past), Snapshot: timeline.SnapshotAt(past));
        });
        range.Basal.Should().Be(Day.Basal);
        range.Snapshot.SensitivityAt(past).Should().Be(Day.Sens);
        range.Snapshot.BasalRateAt(past).Should().Be(Day.Basal);
        range.Snapshot.Dia.Should().Be(Day.Dia);
    }

    [Fact]
    public async Task AnOlderDocumentReadsTheDefaultItWasUploadedWith()
    {
        var stores = new Dictionary<string, Store> { ["Day"] = Day, ["Night"] = Night };
        var client = await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f1886", "Night", Now.AddDays(-3), stores),
            Document("5f8d0c1e8a7b4c3d9e5f1887", "Day", Now.AddDays(-2), stores));

        ShouldBe(await ResolveAtAsync(Now.AddHours(-60).ToUnixTimeMilliseconds()), Night, "Night");
        ShouldBe(await ResolveAtAsync(Now.AddHours(-1).ToUnixTimeMilliseconds()), Day, "Day");

        var documents = JsonNode.Parse(await client.GetStringAsync("/api/v1/profile?count=10"))!.AsArray();
        documents.Select(d => (d!["_id"]!.GetValue<string>(), d["defaultProfile"]!.GetValue<string>()))
            .Should().BeEquivalentTo([("5f8d0c1e8a7b4c3d9e5f1887", "Day"), ("5f8d0c1e8a7b4c3d9e5f1886", "Night")]);

        foreach (var path in new[] { "/api/v3/profile?limit=10", "/api/v3/profile/history/0?limit=10" })
        {
            var v3 = JsonNode.Parse(await client.GetStringAsync(path))!["result"]!.AsArray();
            v3.Select(d => (d!["identifier"]!.GetValue<string>(), d["defaultProfile"]!.GetValue<string>()))
                .Should().BeEquivalentTo(
                    [("5f8d0c1e8a7b4c3d9e5f1887", "Day"), ("5f8d0c1e8a7b4c3d9e5f1886", "Night")], path);
        }
    }

    [Fact]
    public async Task OlderDocumentsWithoutARecordedDefault_AreServedTheDefaultTheResolversRead()
    {
        var client = await SeedAsync(
            Document("5f8d0c1e8a7b4c3d9e5f188d", "Night", Now.AddDays(-4), new() { ["Day"] = Day, ["Night"] = Night }),
            Document("5f8d0c1e8a7b4c3d9e5f188e", "Default", Now.AddDays(-3), new() { ["Alpha"] = Day, ["Default"] = Old }),
            Document("5f8d0c1e8a7b4c3d9e5f188f", "Night", Now.AddDays(-2), new() { ["Day"] = Day, ["Night"] = Night }));
        await using (var db = Fixture.CreateDbContext(Fixture.TenantId))
            await db.TherapySettings
                .Where(t => t.LegacyId!.StartsWith("5f8d0c1e8a7b4c3d9e5f188d:")
                            || t.LegacyId!.StartsWith("5f8d0c1e8a7b4c3d9e5f188e:"))
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.DocumentDefaultProfile, (string?)null));

        ShouldBe(await ResolveAtAsync(Now.AddHours(-84).ToUnixTimeMilliseconds()), Night, "Night");
        ShouldBe(await ResolveAtAsync(Now.AddHours(-60).ToUnixTimeMilliseconds()), Old, "Default");
        (string, string)[] expected =
        [
            ("5f8d0c1e8a7b4c3d9e5f188f", "Night"),
            ("5f8d0c1e8a7b4c3d9e5f188e", "Default"),
            ("5f8d0c1e8a7b4c3d9e5f188d", "Night"),
        ];

        var documents = JsonNode.Parse(await client.GetStringAsync("/api/v1/profile?count=10"))!.AsArray();
        documents.Select(d => (d!["_id"]!.GetValue<string>(), d["defaultProfile"]!.GetValue<string>()))
            .Should().BeEquivalentTo(expected);

        foreach (var path in new[] { "/api/v3/profile?limit=10", "/api/v3/profile/history/0?limit=10" })
        {
            var v3 = JsonNode.Parse(await client.GetStringAsync(path))!["result"]!.AsArray();
            v3.Select(d => (d!["identifier"]!.GetValue<string>(), d["defaultProfile"]!.GetValue<string>()))
                .Should().BeEquivalentTo(expected, path);
        }
    }

    [Fact]
    public async Task AnAapsSwitchCarryingItsOwnDia_IsReadWithItByTheTherapyTimeline()
    {
        var client = await SeedAsync(Document(
            "5f8d0c1e8a7b4c3d9e5f1888", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));
        var switchedAt = Now.AddHours(-3);
        const string profileJson =
            """{"units":"mg/dl","dia":7,"timezone":"UTC","sens":[{"time":"00:00","value":55}],"carbratio":[{"time":"00:00","value":11}],"basal":[{"time":"00:00","value":0.9}],"target_low":[{"time":"00:00","value":100}],"target_high":[{"time":"00:00","value":120}]}""";
        (await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile = "Rhys",
                duration = 180,
                profileJson,
                created_at = switchedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = switchedAt.ToUnixTimeMilliseconds(),
                enteredBy = "openaps://AndroidAPS",
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var at = Now.AddHours(-1).ToUnixTimeMilliseconds();
        var (point, timeline) = await WithTenantScopeAsync(async services =>
        {
            var dia = await services.GetRequiredService<ITherapySettingsResolver>().GetDIAAsync(at);
            var built = await services.GetRequiredService<ITherapyTimelineResolver>()
                .BuildAsync(Now.AddHours(-24).ToUnixTimeMilliseconds(), Now.ToUnixTimeMilliseconds());
            return (dia, built.SnapshotAt(at));
        });

        point.Should().Be(7);
        timeline.Dia.Should().Be(7);
        timeline.BasalRateAt(at).Should().Be(0.9);
    }

    [Fact]
    public async Task ScheduledBasalsPublishedTogether_ReadTheSwitchStartingAtTheLastOne()
    {
        var client = await SeedAsync(Document(
            "5f8d0c1e8a7b4c3d9e5f1889", "Loop Profile", Now.AddDays(-2), new() { ["Loop Profile"] = Day }));
        var first = Now.AddHours(-3);
        var last = Now.AddHours(-1);
        (await client.PostAsJsonAsync("/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile = "Loop Profile",
                percentage = 150,
                duration = 0,
                created_at = last.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = last.ToUnixTimeMilliseconds(),
                enteredBy = "careportal",
            },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await WithTenantScopeAsync(async services =>
        {
            (await services.GetRequiredService<ITreatmentPublisher>().PublishTempBasalsAsync(
                [Scheduled("scheduled-first", first, Day.Basal), Scheduled("scheduled-last", last, Day.Basal * 1.5)],
                "test-connector",
                WriteOrigin.Live)).Should().BeTrue();
            var repository = services.GetRequiredService<ITempBasalRepository>();
            return (First: await repository.GetByLegacyIdAsync("scheduled-first"),
                Last: await repository.GetByLegacyIdAsync("scheduled-last"));
        });

        stored.First!.Origin.Should().Be(TempBasalOrigin.Scheduled);
        stored.First.ScheduledRate.Should().BeApproximately(Day.Basal, 1e-9);
        stored.Last!.Origin.Should().Be(TempBasalOrigin.Scheduled);
        stored.Last.ScheduledRate.Should().BeApproximately(Day.Basal * 1.5, 1e-9);
    }

    private static TempBasal Scheduled(string legacyId, DateTimeOffset start, double rate) => new()
    {
        LegacyId = legacyId,
        StartTimestamp = start.UtcDateTime,
        EndTimestamp = start.UtcDateTime.AddMinutes(30),
        Rate = rate,
        Origin = TempBasalOrigin.Scheduled,
        DataSource = "test-connector",
    };

    [Fact]
    public async Task ThePatientsTimezone_IsReadByThePointBatchAndTimelineResolversAlike()
    {
        var store = new
        {
            dia = 5.5,
            carbs_hr = 25,
            timezone = "UTC",
            units = "mg/dl",
            basal = new[] { new { time = "00:00", value = 0.5 }, new { time = "12:00", value = 1.5 } },
            carbratio = new[] { new { time = "00:00", value = 8.0 } },
            sens = new[] { new { time = "00:00", value = 40.0 }, new { time = "12:00", value = 60.0 } },
            target_low = new[] { new { time = "00:00", value = 100.0 } },
            target_high = new[] { new { time = "00:00", value = 110.0 } },
        };
        await SeedAsync(RawDocument(
            "5f8d0c1e8a7b4c3d9e5f188a", "Loop Profile", Now.AddDays(-3), new Dictionary<string, object> { ["Loop Profile"] = store }));

        // 14:00 UTC is before noon in New York, so the morning entries apply there and the afternoon ones in UTC.
        var day = Now.UtcDateTime.Date;
        var at = new DateTimeOffset(day.AddHours(14) <= Now.UtcDateTime.AddHours(-1) ? day.AddHours(14) : day.AddDays(-1).AddHours(14));
        var atMills = at.ToUnixTimeMilliseconds();

        await SetPatientTimezoneAsync("America/New_York");
        try
        {
            var answers = await WithTenantScopeAsync(async services =>
            {
                var batch = await services.GetRequiredService<IBasalRateResolver>()
                    .BuildResolverAsync(atMills - 3_600_000, atMills + 1);
                var timeline = (await services.GetRequiredService<ITherapyTimelineResolver>()
                    .BuildAsync(atMills - 3_600_000, atMills + 1)).SnapshotAt(atMills);
                var segments = new List<BasalSegment>();
                await foreach (var segment in services.GetRequiredService<IBasalSegmentService>()
                                   .GetSegmentsAsync(atMills, atMills + 1_800_000))
                    segments.Add(segment);
                return (
                    Point: await services.GetRequiredService<IBasalRateResolver>().GetBasalRateAsync(atMills),
                    PointSens: await services.GetRequiredService<ISensitivityResolver>().GetSensitivityAsync(atMills),
                    Batch: batch(atMills),
                    TimelineBasal: timeline.BasalRateAt(atMills),
                    TimelineSens: timeline.SensitivityAt(atMills),
                    Segments: segments);
            });

            answers.Point.Should().Be(0.5);
            answers.PointSens.Should().Be(40);
            answers.Batch.Should().Be(0.5);
            answers.TimelineBasal.Should().Be(0.5);
            answers.TimelineSens.Should().Be(40);
            answers.Segments.Should().NotBeEmpty().And.OnlyContain(s => s.UnitsPerHour == 0.5);
        }
        finally
        {
            await SetPatientTimezoneAsync(null);
        }
    }

    private Task SetPatientTimezoneAsync(string? timezone) =>
        WithTenantScopeAsync(async services =>
        {
            var patients = services.GetRequiredService<IPatientRecordRepository>();
            var patient = await patients.GetOrCreateAsync();
            patient.Timezone = timezone;
            return await patients.UpdateAsync(patient, WriteOrigin.Live);
        });

    [Fact]
    public async Task AReuploadChangingAnOlderDocumentsDefault_IsReadAndServed()
    {
        var stores = new Dictionary<string, Store> { ["Day"] = Day, ["Night"] = Night };
        var older = Document("5f8d0c1e8a7b4c3d9e5f188b", "Night", Now.AddDays(-3), stores);
        var client = await SeedAsync(older, Document("5f8d0c1e8a7b4c3d9e5f188c", "Day", Now.AddDays(-2), stores));
        ShouldBe(await ResolveAtAsync(Now.AddHours(-60).ToUnixTimeMilliseconds()), Night, "Night");

        (await client.PutAsJsonAsync("/api/v1/profile", Document("5f8d0c1e8a7b4c3d9e5f188b", "Day", Now.AddDays(-3), stores)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Another window than before: the basal assignments are cached for 5 s per window.
        ShouldBe(await ResolveAtAsync(Now.AddHours(-59).ToUnixTimeMilliseconds()), Day, "Day");
        var documents = JsonNode.Parse(await client.GetStringAsync("/api/v1/profile?count=10"))!.AsArray();
        documents.Single(d => d!["_id"]!.GetValue<string>() == "5f8d0c1e8a7b4c3d9e5f188b")!["defaultProfile"]!
            .GetValue<string>().Should().Be("Day");
    }
}
