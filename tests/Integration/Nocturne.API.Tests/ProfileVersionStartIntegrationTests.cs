using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.API.Services.Profiles.Resolvers;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Services;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Contracts.V4.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Profile documents posted without <c>mills</c> apply from their <c>startDate</c>, as Nightscout
/// applies them, so the version in force at a time is the one whose document started last before it.
/// Every test names its own profile store, so no profile version a test before it cached answers for it.
/// </summary>
[Trait("Category", "Integration")]
public class ProfileVersionStartIntegrationTests : ApiIntegrationTestBase
{
    public ProfileVersionStartIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DateTimeOffset DaysAgo(double days) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Now.AddDays(-days).ToUnixTimeMilliseconds());

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string StoreName() => $"store-{Guid.NewGuid():N}";

    private static Dictionary<string, object> Store(string store, double basal) => new()
    {
        [store] = new
        {
            dia = 4,
            carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
            sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
            basal = new[] { new { time = "00:00", value = basal, timeAsSeconds = 0 } },
            target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
            target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
            units = "mg/dl",
            timezone = "UTC",
        },
    };

    /// <summary>A document as Nightscout's profile editor saves it: a <c>startDate</c>, no <c>mills</c>.</summary>
    private static object EditorProfile(string store, double basal, DateTimeOffset startDate) => new
    {
        defaultProfile = store,
        store = Store(store, basal),
        startDate = At(startDate),
        units = "mg/dl",
    };

    /// <summary>A document as AAPS uploads it over v3: <c>date</c>, <c>created_at</c> and <c>startDate</c>, no <c>mills</c>.</summary>
    private static object AapsProfile(string store, double basal, DateTimeOffset startDate) => new
    {
        defaultProfile = store,
        date = startDate.ToUnixTimeMilliseconds(),
        created_at = At(startDate),
        startDate = At(startDate),
        store = Store(store, basal),
        units = "mg/dl",
        app = "AAPS",
    };

    private async Task PostAsync(string path, object body)
    {
        var response = await CreateAuthenticatedClient().PostAsJsonAsync(path, body);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private async Task AssertTheLaterDocumentGovernsAfterItsStartAsync(
        string store, DateTimeOffset first, DateTimeOffset second)
    {
        var starts = await WithTenantScopeAsync(async sp =>
            (await sp.GetRequiredService<IBasalScheduleRepository>().GetByProfileNameAsync(store))
                .Select(v => (v.Timestamp, v.Entries.Single().Value))
                .OrderBy(v => v.Timestamp)
                .ToList());
        starts.Should().Equal((first.UtcDateTime, 1.0), (second.UtcDateTime, 2.0));

        var (before, between, after) = await WithTenantScopeAsync(async sp =>
        {
            var basal = sp.GetRequiredService<IBasalRateResolver>();
            return (
                await basal.FindBasalRateAsync(first.AddDays(1).ToUnixTimeMilliseconds()),
                await basal.FindBasalRateAsync(second.AddDays(1).ToUnixTimeMilliseconds()),
                await basal.FindBasalRateAsync(Now.ToUnixTimeMilliseconds()));
        });
        before.Should().Be(1.0, "the first document is in force until the second starts");
        between.Should().Be(2.0, "the second document starts before then");
        after.Should().Be(2.0);
    }

    [Fact]
    public async Task V1Profiles_WithoutMills_ApplyFromTheirStartDates()
    {
        var store = StoreName();
        var first = DaysAgo(20);
        var second = DaysAgo(18);

        await PostAsync("/api/v1/profile", EditorProfile(store, 1.0, first));
        await PostAsync("/api/v1/profile", EditorProfile(store, 2.0, second));

        await AssertTheLaterDocumentGovernsAfterItsStartAsync(store, first, second);
    }

    /// <summary>The newer document arriving first must not change which one governs each time.</summary>
    [Fact]
    public async Task V1Profiles_WithoutMills_PostedNewestFirst_ApplyFromTheirStartDates()
    {
        var store = StoreName();
        var first = DaysAgo(20);
        var second = DaysAgo(18);

        await PostAsync("/api/v1/profile", new[] { EditorProfile(store, 2.0, second), EditorProfile(store, 1.0, first) });

        await AssertTheLaterDocumentGovernsAfterItsStartAsync(store, first, second);
    }

    [Fact]
    public async Task V3AapsProfiles_WithoutMills_ApplyFromTheirStartDates()
    {
        var store = StoreName();
        var first = DaysAgo(20);
        var second = DaysAgo(18);

        await PostAsync("/api/v3/profile", AapsProfile(store, 1.0, first));
        await PostAsync("/api/v3/profile", AapsProfile(store, 2.0, second));

        await AssertTheLaterDocumentGovernsAfterItsStartAsync(store, first, second);
    }

    /// <summary>
    /// Nightscout's profile editor saves a document it read with the <c>mills</c> it read and the
    /// <c>startDate</c> the user set; the document moves to that <c>startDate</c>.
    /// </summary>
    [Fact]
    public async Task EditingAReadDocumentsStartDate_MovesItsVersions()
    {
        var store = StoreName();
        var client = CreateAuthenticatedClient();
        await PostAsync("/api/v1/profile", EditorProfile(store, 1.0, DaysAgo(20)));

        var read = (await client.GetFromJsonAsync<JsonElement>("/api/v1/profile")).EnumerateArray()
            .Single(d => d.GetProperty("defaultProfile").GetString() == store);
        read.GetProperty("mills").GetInt64().Should().BeGreaterThan(0);
        var edited = JsonNode.Parse(read.GetRawText())!.AsObject();
        var moved = DaysAgo(12);
        edited["startDate"] = At(moved);

        var put = await client.PutAsJsonAsync("/api/v1/profile", edited);
        put.IsSuccessStatusCode.Should().BeTrue(await put.Content.ReadAsStringAsync());

        var starts = await WithTenantScopeAsync(async sp =>
            (await sp.GetRequiredService<IBasalScheduleRepository>().GetByProfileNameAsync(store))
                .Select(v => v.Timestamp).ToList());
        starts.Should().Equal(moved.UtcDateTime);
    }

    /// <summary>
    /// A document with no <c>startDate</c> and no <c>mills</c>, as a Nightscout server serves one it
    /// stored that way, starts at its <c>created_at</c> however often it is synced again.
    /// </summary>
    [Fact]
    public async Task ADocumentWithOnlyCreatedAt_StaysAtItAcrossResyncs()
    {
        var store = StoreName();
        var createdAt = DaysAgo(9);
        var document = new
        {
            _id = ObjectId(store),
            defaultProfile = store,
            store = Store(store, 1.0),
            created_at = At(createdAt),
            units = "mg/dl",
        };

        await PostAsync("/api/v1/profile", document);
        var first = await SettingsAsync(store);
        await PostAsync("/api/v1/profile", document);
        var second = await SettingsAsync(store);

        first.Timestamp.Should().Be(createdAt.UtcDateTime);
        second.Should().Be(first, "a re-sync of an unchanged document writes nothing");
    }

    private static string ObjectId(string seed) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..24];

    private Task<(DateTime Timestamp, DateTime Modified)> SettingsAsync(string store) =>
        WithTenantScopeAsync(async sp =>
        {
            var row = (await sp.GetRequiredService<ITherapySettingsRepository>().GetByProfileNameAsync(store)).Single();
            return (row.Timestamp, row.ModifiedAt);
        });

    /// <summary>
    /// A v1 document with no time of its own is stamped with <c>created_at</c> on arrival, as
    /// Nightscout's create stamps it, and starts then rather than at the epoch.
    /// </summary>
    [Fact]
    public async Task AMinimalV1Profile_StartsWhenItArrives()
    {
        var store = StoreName();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var response = await CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/v1/profile", new { defaultProfile = store, store = Store(store, 1.0) });

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        var echoed = (await response.Content.ReadFromJsonAsync<JsonElement>())[0];
        echoed.GetProperty("created_at").GetString().Should().NotBeNullOrEmpty();
        var (start, _) = await SettingsAsync(store);
        start.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
    }

    /// <summary>
    /// A percent temp uploaded without a rate resolves against the version its time falls in.
    /// </summary>
    [Fact]
    public async Task PercentTempBasal_ResolvesAgainstTheDocumentInForceByStartDate()
    {
        var store = StoreName();
        await PostAsync("/api/v1/profile", EditorProfile(store, 1.0, DaysAgo(20)));
        await PostAsync("/api/v1/profile", EditorProfile(store, 2.0, DaysAgo(18)));
        var createdAt = DaysAgo(15);

        await PostAsync("/api/v1/treatments", new
        {
            eventType = "Temp Basal", duration = 30, percent = -50, created_at = At(createdAt), enteredBy = "careportal",
        });

        var tempBasal = await TempBasalAtAsync(createdAt);
        tempBasal.ScheduledRate.Should().Be(2.0);
        tempBasal.Rate.Should().BeApproximately(1.0, 1e-9);
    }

    /// <summary>
    /// A version moved with no re-resolution after it, as <c>RepairEpochProfileVersions</c> moves them
    /// in SQL, has its percent temp rates re-resolved by the startup sweep for the job the migration
    /// left, once.
    /// </summary>
    [Fact]
    public async Task StartupSweep_ReresolvesRatesAgainstAVersionMovedWithoutAWrite()
    {
        var store = StoreName();
        await PostAsync("/api/v1/profile", EditorProfile(store, 1.0, DaysAgo(20)));
        await PostAsync("/api/v1/profile", EditorProfile(store, 2.0, DaysAgo(18)));
        var createdAt = DaysAgo(15);
        await PostAsync("/api/v1/treatments", new
        {
            eventType = "Temp Basal", duration = 30, percent = -50, created_at = At(createdAt), enteredBy = "careportal",
        });
        (await TempBasalAtAsync(createdAt)).ScheduledRate.Should().Be(2.0);

        await WithTenantScopeAsync(async sp =>
        {
            var schedules = sp.GetRequiredService<IBasalScheduleRepository>();
            var second = (await schedules.GetByProfileNameAsync(store)).Single(v => v.Entries.Single().Value == 2.0);
            second.Timestamp = DaysAgo(10).UtcDateTime;
            await schedules.UpdateAsync(second.Id, second, WriteOrigin.Live);
            await sp.GetRequiredService<ProfileVersionCache>().RefreshAsync(schedules, store, default);
            return 0;
        });

        await RunSweepAsync();
        (await TempBasalAtAsync(createdAt)).ScheduledRate.Should().Be(2.0, "no migration left a job for the tenant");

        var factory = Fixture.Services.GetRequiredService<INocturneDbContextFactory>();
        await using (var platform = await factory.CreatePlatformAsync())
        {
            platform.PendingTenantJobs.Add(new PendingTenantJobEntity
            {
                TenantId = Fixture.TenantId,
                Job = PendingTenantJobs.ProfileVersionsMoved,
                RequestedAt = DateTime.UtcNow,
            });
            await platform.SaveChangesAsync();
        }

        await RunSweepAsync();

        var tempBasal = await TempBasalAtAsync(createdAt);
        tempBasal.ScheduledRate.Should().Be(1.0);
        tempBasal.Rate.Should().BeApproximately(0.5, 1e-9);
        await using var after = await factory.CreatePlatformAsync();
        (await after.PendingTenantJobs.CountAsync(j => j.TenantId == Fixture.TenantId))
            .Should().Be(0, "the job runs once");
    }

    private async Task RunSweepAsync()
    {
        var sweep = new TempBasalRateSweepService(Fixture.Services, NullLogger<TempBasalRateSweepService>.Instance);
        await sweep.StartAsync(default);
        await sweep.ExecuteTask!;
    }

    private Task<Nocturne.Core.Models.V4.TempBasal> TempBasalAtAsync(DateTimeOffset createdAt) =>
        WithTenantScopeAsync(async sp =>
            (await sp.GetRequiredService<ITempBasalRepository>().GetAsync(
                from: createdAt.UtcDateTime.AddSeconds(-1), to: createdAt.UtcDateTime.AddSeconds(1), device: null,
                source: null, limit: 10, offset: 0, descending: true, ct: default)).Single());
}
