using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Temp basals set as a percentage: stored relative to the schedule, read back as uploaded, and
/// their schedule-derived rates following later schedule and activation writes. Every test names its
/// own profile store, so no profile version a test before it cached answers for it.
/// </summary>
[Trait("Category", "Integration")]
public class PercentTempBasalIntegrationTests : ApiIntegrationTestBase
{
    public PercentTempBasalIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static DateTimeOffset DaysAgo(double days) =>
        DateTimeOffset.UtcNow.AddDays(-days).AddMilliseconds(Random.Shared.Next(1, 60_000));

    private static string StoreName() => $"store-{Guid.NewGuid():N}";

    private static object FlatProfile(string store, double basal, DateTimeOffset startDate) => new
    {
        defaultProfile = store,
        store = new Dictionary<string, object>
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
        },
        startDate = startDate.ToString("o"),
        mills = startDate.ToUnixTimeMilliseconds(),
    };

    private static object CareportalTemp(string createdAt, double percent) => new
    {
        eventType = "Temp Basal", duration = 30, percent, created_at = createdAt, enteredBy = "careportal",
    };

    private static object AapsTemp(string createdAt, double percent, double rate) => new Dictionary<string, object>
    {
        ["eventType"] = "Temp Basal",
        ["created_at"] = createdAt,
        ["enteredBy"] = "openaps://AndroidAPS",
        ["duration"] = 30,
        ["percent"] = percent,
        ["rate"] = rate,
        ["type"] = "NORMAL",
    };

    private async Task PostAsync(string path, object body) =>
        (await CreateAuthenticatedClient().PostAsJsonAsync(path, body)).EnsureSuccessStatusCode();

    private Task<V4Models.TempBasal> StoredAtAsync(string createdAt)
    {
        var at = DateTimeOffset.Parse(createdAt).UtcDateTime;
        return WithTenantScopeAsync(async sp => (await sp.GetRequiredService<ITempBasalRepository>().GetAsync(
            from: at.AddSeconds(-1), to: at.AddSeconds(1), device: null, source: null, limit: 10, offset: 0,
            descending: true, ct: default)).Single());
    }

    private static string ObjectIdFor(string seed) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)))[..24];

    /// <summary>
    /// A percent temp changes the scheduled basal rather than suspending it, and reads back with the
    /// percent it was uploaded with, as Nightscout stores and serves it.
    /// </summary>
    [Fact]
    public async Task PercentTempBasal_ResolvesAgainstTheScheduleAndReadsBackAsUploaded()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync("/api/v1/profile", FlatProfile(StoreName(), 0.8, DaysAgo(2)));
        var createdAt = At(DaysAgo(0.2));

        var post = await client.PostAsJsonAsync("/api/v1/treatments", CareportalTemp(createdAt, -50));
        post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
        var echoed = (await post.Content.ReadFromJsonAsync<JsonElement>())[0];
        echoed.GetProperty("percent").GetDouble().Should().Be(-50);
        echoed.TryGetProperty("absolute", out _).Should().BeFalse("Nightscout clients take absolute over percent");

        var served = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[created_at]={Uri.EscapeDataString(createdAt)}")).EnumerateArray().Single();
        served.GetProperty("percent").GetDouble().Should().Be(-50);
        served.TryGetProperty("absolute", out _).Should().BeFalse();
        served.TryGetProperty("rate", out _).Should().BeFalse("the upload carried no rate");

        var tempBasal = await StoredAtAsync(createdAt);
        tempBasal.Percent.Should().Be(-50);
        tempBasal.ScheduledRate.Should().Be(0.8);
        tempBasal.Rate.Should().BeApproximately(0.4, 1e-9);
        tempBasal.RateFromSchedule.Should().BeTrue();
    }

    /// <summary>
    /// AAPS sends a percent temp with its own conversion as <c>rate</c>, profile switch percentage
    /// included; that rate is what the pump delivered, and it reads back as sent.
    /// </summary>
    [Fact]
    public async Task AapsPercentTempBasal_KeepsTheRateItSentAndReadsBackAsSent()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = At(DaysAgo(0.2));
        await PostAsync("/api/v1/treatments", AapsTemp(createdAt, -30, 0.735));

        var served = (await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[created_at]={Uri.EscapeDataString(createdAt)}")).EnumerateArray().Single();
        served.GetProperty("percent").GetDouble().Should().Be(-30);
        served.GetProperty("rate").GetDouble().Should().Be(0.735);
        served.TryGetProperty("absolute", out _).Should().BeFalse("AAPS reads a temp with absolute as an absolute temp");

        var tempBasal = await StoredAtAsync(createdAt);
        tempBasal.Rate.Should().Be(0.735);
        tempBasal.RateFromSchedule.Should().BeFalse();
    }

    /// <summary>
    /// A Nightscout import lands the treatments before the profile; a percent temp stored before any
    /// schedule was in force at its start resolves when the profile arrives.
    /// </summary>
    [Fact]
    public async Task PercentTempBasalUploadedBeforeItsSchedule_ResolvesWhenTheProfileArrives()
    {
        var createdAt = At(DaysAgo(30));
        await PostAsync("/api/v1/treatments", CareportalTemp(createdAt, -50));
        (await StoredAtAsync(createdAt)).Rate.Should().BeNull("no schedule is in force yet");

        await PostAsync("/api/v1/profile", FlatProfile(StoreName(), 1.0, DaysAgo(40)));

        var resolved = await StoredAtAsync(createdAt);
        resolved.Rate.Should().BeApproximately(0.5, 1e-9);
        resolved.ScheduledRate.Should().Be(1.0);
        resolved.RateFromSchedule.Should().BeTrue();
    }

    /// <summary>
    /// Nightscout reads a percent temp against the schedule in force when it is read, so editing the
    /// schedule moves a rate taken from it; a rate the uploader sent stays as sent.
    /// </summary>
    [Fact]
    public async Task AScheduleEdit_MovesScheduleDerivedRates_ButNotUploadedOnes()
    {
        var store = StoreName();
        await PostAsync("/api/v1/profile", FlatProfile(store, 1.0, DaysAgo(20)));
        var careportalAt = At(DaysAgo(15));
        var aapsAt = At(DaysAgo(15).AddMinutes(1));
        await PostAsync("/api/v1/treatments", CareportalTemp(careportalAt, -50));
        await PostAsync("/api/v1/treatments", AapsTemp(aapsAt, -30, 0.7));
        (await StoredAtAsync(careportalAt)).Rate.Should().BeApproximately(0.5, 1e-9);

        await PostAsync("/api/v1/profile", FlatProfile(store, 2.0, DaysAgo(18)));

        (await StoredAtAsync(careportalAt)).Rate.Should().BeApproximately(1.0, 1e-9);
        (await StoredAtAsync(aapsAt)).Rate.Should().Be(0.7);
    }

    /// <summary>
    /// An import pages treatments newest first, so a temp can land a page before the profile switch it
    /// ran under. Writing the switch re-resolves the temp, percentage included. Runs the real
    /// activation resolver and profile version cache.
    /// </summary>
    [Fact]
    public async Task ImportWithTheSwitchOnAnOlderPageThanTheTemp_EndsWithTheSwitchedRate()
    {
        var store = StoreName();
        await PostAsync("/api/v1/profile", FlatProfile(store, 1.0, DaysAgo(10)));
        var switchAt = DaysAgo(9);
        var createdAt = At(switchAt.AddMinutes(1));

        Task Import(Treatment treatment) => WithTenantScopeAsync(sp => sp.GetRequiredService<ITreatmentDecomposer>()
            .DecomposeBatchAsync([treatment], WriteOrigin.Backfill, CancellationToken.None));

        await Import(new Treatment
        {
            Id = ObjectIdFor(createdAt), EventType = "Temp Basal", Duration = 30, Percent = -50,
            Created_at = createdAt, EnteredBy = "careportal",
        });
        (await StoredAtAsync(createdAt)).Rate.Should().BeApproximately(0.5, 1e-9);

        await Import(new Treatment
        {
            Id = ObjectIdFor(At(switchAt)), EventType = "Profile Switch", Profile = store, Percentage = 150,
            Duration = 60, Created_at = At(switchAt), EnteredBy = "careportal",
        });

        (await StoredAtAsync(createdAt)).Rate.Should().BeApproximately(0.75, 1e-9);
    }

    /// <summary>
    /// A permanent switch applies again once a temporary one ends, so importing the permanent one
    /// after the temporary one moves a temp that runs after the temporary one ended.
    /// </summary>
    [Fact]
    public async Task APermanentSwitchImportedAfterATemporaryOne_ReachesPastItsEnd()
    {
        var store = StoreName();
        await PostAsync("/api/v1/profile", FlatProfile(store, 1.0, DaysAgo(10)));
        var permanentAt = DaysAgo(8);
        var temporaryAt = permanentAt.AddHours(1);
        var createdAt = At(temporaryAt.AddMinutes(90));

        Task Import(Treatment treatment) => WithTenantScopeAsync(sp => sp.GetRequiredService<ITreatmentDecomposer>()
            .DecomposeBatchAsync([treatment], WriteOrigin.Backfill, CancellationToken.None));

        await Import(new Treatment
        {
            Id = ObjectIdFor(At(temporaryAt)), EventType = "Profile Switch", Profile = store, Percentage = 50,
            Duration = 60, Created_at = At(temporaryAt), EnteredBy = "careportal",
        });
        await Import(new Treatment
        {
            Id = ObjectIdFor(createdAt), EventType = "Temp Basal", Duration = 30, Percent = -50,
            Created_at = createdAt, EnteredBy = "careportal",
        });
        (await StoredAtAsync(createdAt)).Rate.Should().BeApproximately(0.5, 1e-9);

        await Import(new Treatment
        {
            Id = ObjectIdFor(At(permanentAt)), EventType = "Profile Switch", Profile = store, Percentage = 150,
            Duration = 0, Created_at = At(permanentAt), EnteredBy = "careportal",
        });

        (await StoredAtAsync(createdAt)).Rate.Should().BeApproximately(0.75, 1e-9);
    }

    /// <summary>
    /// The conditional write never lands on a row that changed under it: a new percent, an uploaded
    /// rate or a delete stands.
    /// </summary>
    [Fact]
    public async Task ARateWriteRacingAnEdit_LeavesTheEditStanding()
    {
        await PostAsync("/api/v1/profile", FlatProfile(StoreName(), 1.0, DaysAgo(3)));
        var resolvedAt = At(DaysAgo(1));
        var uploadedAt = At(DaysAgo(1).AddMinutes(1));
        await PostAsync("/api/v1/treatments", CareportalTemp(resolvedAt, -50));
        await PostAsync("/api/v1/treatments", AapsTemp(uploadedAt, -30, 0.7));
        var resolved = await StoredAtAsync(resolvedAt);
        var uploaded = await StoredAtAsync(uploadedAt);

        var (stalePercent, staleRate, uploadedRate) = await WithTenantScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<ITempBasalRepository>();
            return (await repo.SetScheduleDerivedRateAsync(resolved.Id, -20, resolved.Rate, 0.8, 1.0, default),
                await repo.SetScheduleDerivedRateAsync(resolved.Id, -50, 0.45, 0.9, 1.8, default),
                await repo.SetScheduleDerivedRateAsync(uploaded.Id, -30, uploaded.Rate, 0.9, 1.0, default));
        });

        stalePercent.Should().BeFalse("the row no longer has the percent the rate was resolved for");
        staleRate.Should().BeFalse("an overlapping re-resolution already moved the rate it read");
        uploadedRate.Should().BeFalse("an uploaded rate is never overwritten");
        (await StoredAtAsync(resolvedAt)).Rate.Should().BeApproximately(0.5, 1e-9);
        (await StoredAtAsync(uploadedAt)).Rate.Should().Be(0.7);
    }

    /// <summary>The window is read a page at a time on real Postgres; every page is resolved.</summary>
    [Fact]
    public async Task MoreUnknownRatesThanOnePage_AllResolve()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-60);
        var temps = Enumerable.Range(0, 520).Select(i => new V4Models.TempBasal
        {
            LegacyId = ObjectIdFor($"paged-{i}"),
            StartTimestamp = start.AddMinutes(5 * i).UtcDateTime,
            EndTimestamp = start.AddMinutes(5 * i + 30).UtcDateTime,
            Percent = -50,
            Origin = V4Models.TempBasalOrigin.Manual,
        }).ToList();
        await WithTenantScopeAsync(sp => sp.GetRequiredService<ITempBasalRepository>()
            .BulkCreateAsync(temps, WriteOrigin.Backfill, CancellationToken.None));

        await PostAsync("/api/v1/profile", FlatProfile(StoreName(), 1.0, start.AddDays(-1)));

        var stored = await WithTenantScopeAsync(async sp => (await sp.GetRequiredService<ITempBasalRepository>().GetAsync(
            from: start.AddMinutes(-1).UtcDateTime, to: start.AddDays(3).UtcDateTime, device: null, source: null,
            limit: 1000, offset: 0, descending: false, ct: default)).ToList());
        stored.Should().HaveCount(520);
        stored.Should().OnlyContain(t => t.Rate == 0.5 && t.RateFromSchedule);
    }

    /// <summary>A temp judged while its rate was unknown is judged again once the rate resolves.</summary>
    [Fact]
    public async Task AnUnknownRateResolving_SendsAnUncorroboratedTempBackToBeJudged()
    {
        var start = DateTimeOffset.UtcNow.AddDays(-20);
        await WithTenantScopeAsync(sp => sp.GetRequiredService<ITempBasalRepository>().BulkCreateAsync(
            [new V4Models.TempBasal
            {
                LegacyId = ObjectIdFor("uncorroborated-percent"),
                StartTimestamp = start.UtcDateTime,
                EndTimestamp = start.AddMinutes(30).UtcDateTime,
                Percent = -50,
                Origin = V4Models.TempBasalOrigin.Algorithm,
                OriginEvidence = V4Models.TempBasalOriginEvidence.Uncorroborated,
            }],
            WriteOrigin.Backfill, CancellationToken.None));

        await PostAsync("/api/v1/profile", FlatProfile(StoreName(), 1.0, start.AddDays(-1)));

        var stored = await StoredAtAsync(start.ToString("O"));
        stored.Rate.Should().Be(0.5);
        stored.OriginEvidence.Should().Be(V4Models.TempBasalOriginEvidence.Uploader);
    }
}
