using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models.Basal;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// With no profile switch in force, every therapy resolver reads the store the newest profile
/// document names as its <c>defaultProfile</c>, as Nightscout's <c>store[defaultProfile]</c> does,
/// whatever that store is called, on real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class DefaultProfileStoreResolutionIntegrationTests : ApiIntegrationTestBase
{
    public DefaultProfileStoreResolutionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private sealed record Store(double Sens, double CarbRatio, double Basal, double Low, double High, double Dia, double CarbsHr);

    private static readonly Store Day = new(40, 8, 0.65, 100, 110, 5.5, 25);
    private static readonly Store Night = new(70, 12, 0.3, 120, 130, 4.5, 35);

    private readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static object StoreData(Store s) => new
    {
        dia = s.Dia,
        carbs_hr = s.CarbsHr,
        timezone = "UTC",
        units = "mg/dl",
        basal = new[] { new { time = "00:00", value = s.Basal } },
        carbratio = new[] { new { time = "00:00", value = s.CarbRatio } },
        sens = new[] { new { time = "00:00", value = s.Sens } },
        target_low = new[] { new { time = "00:00", value = s.Low } },
        target_high = new[] { new { time = "00:00", value = s.High } },
    };

    private static object Document(string id, string defaultProfile, DateTimeOffset startDate, Dictionary<string, Store> stores) => new
    {
        _id = id,
        defaultProfile,
        startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        mills = startDate.ToUnixTimeMilliseconds(),
        store = stores.ToDictionary(s => s.Key, s => StoreData(s.Value)),
    };

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
}
