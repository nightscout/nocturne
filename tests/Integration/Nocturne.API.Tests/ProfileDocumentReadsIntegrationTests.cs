using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Every legacy profile read serves one document per stored profile document, carrying all of its
/// stores, and never serves the snapshot a Profile Switch treatment stored, on real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class ProfileDocumentReadsIntegrationTests : ApiIntegrationTestBase
{
    private const string OlderId = "5f8d0c1e8a7b4c3d9e5f0101";
    private const string NewerId = "5f8d0c1e8a7b4c3d9e5f0102";

    public ProfileDocumentReadsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static object StoreData(double dia) => new
    {
        dia,
        carbratio = new[] { new { time = "00:00", value = 10, timeAsSeconds = 0 } },
        sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
        basal = new[] { new { time = "00:00", value = 1.0, timeAsSeconds = 0 } },
        target_low = new[] { new { time = "00:00", value = 80, timeAsSeconds = 0 } },
        target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
        units = "mg/dl",
        timezone = "UTC",
    };

    private static object Document(string id, string defaultProfile, DateTimeOffset startDate, params string[] stores) => new
    {
        _id = id,
        defaultProfile,
        startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        mills = startDate.ToUnixTimeMilliseconds(),
        store = stores.ToDictionary(s => s, _ => StoreData(3)),
    };

    private static async Task PostAsync(HttpClient client, string path, object body) =>
        (await client.PostAsJsonAsync(path, body)).StatusCode.Should().Be(HttpStatusCode.OK);

    private static async Task<JsonElement> GetAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("result", out var result)
            ? result
            : body;
    }

    private static string[] StoresOf(JsonElement profile) =>
        profile.GetProperty("store").EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>
    /// A two-store document whose default is its second store, then a Profile Switch carrying a
    /// profile inline, written after it.
    /// </summary>
    private async Task<HttpClient> SeedDocumentThenSwitchAsync()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        await PostAsync(client, "/api/v1/profile", Document(NewerId, "Night", now.AddHours(-2), "Day", "Night"));
        await PostAsync(client, "/api/v1/treatments", new[]
        {
            new
            {
                eventType = "Profile Switch",
                profile = "Exercise",
                profileJson = JsonSerializer.Serialize(StoreData(5)),
                created_at = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                mills = now.ToUnixTimeMilliseconds(),
                enteredBy = "AndroidAPS",
            },
        });
        return client;
    }

    private static void ShouldBeTheSeededDocument(JsonElement profile, string id = NewerId)
    {
        profile.GetProperty("_id").GetString().Should().Be(id);
        profile.GetProperty("defaultProfile").GetString().Should().Be("Night");
        StoresOf(profile).Should().BeEquivalentTo("Day", "Night");
    }

    [Fact]
    public async Task V1List_ServesTheDocumentOnceWithEveryStore_AndNoSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var listed = await GetAsync(client, "/api/v1/profile?count=10");

        ShouldBeTheSeededDocument(listed.EnumerateArray().Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task V1ProfilesHistory_ServesTheDocumentOnceWithEveryStore_AndNoSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var listed = await GetAsync(client, "/api/v1/profiles?count=10");

        ShouldBeTheSeededDocument(listed.EnumerateArray().Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task V1Current_AfterAProfileSwitch_ServesTheNewestDocument_NotTheSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        ShouldBeTheSeededDocument(await GetAsync(client, "/api/v1/profile/current"));
    }

    [Fact]
    public async Task V1ById_ServesEveryStoreOfTheDocument()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var found = await GetAsync(client, $"/api/v1/profile/{NewerId}");

        ShouldBeTheSeededDocument(found.EnumerateArray().Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task V3Search_ServesTheDocumentOnceWithEveryStore_AndNoSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var listed = await GetAsync(client, "/api/v3/profile?limit=10");

        var profile = listed.EnumerateArray().Should().ContainSingle().Subject;
        profile.GetProperty("defaultProfile").GetString().Should().Be("Night");
        StoresOf(profile).Should().BeEquivalentTo("Day", "Night");
    }

    [Fact]
    public async Task V3History_ServesTheDocumentOnceWithEveryStore_AndNoSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var history = await GetAsync(client, "/api/v3/profile/history/0?limit=10");

        var profile = history.EnumerateArray().Should().ContainSingle().Subject;
        profile.GetProperty("defaultProfile").GetString().Should().Be("Night");
        StoresOf(profile).Should().BeEquivalentTo("Day", "Night");
    }

    [Fact]
    public async Task V3ById_ServesEveryStoreOfTheDocument()
    {
        var client = await SeedDocumentThenSwitchAsync();
        var identifier = (await GetAsync(client, "/api/v3/profile?limit=10"))
            .EnumerateArray().Single().GetProperty("identifier").GetString();

        var profile = await GetAsync(client, $"/api/v3/profile/{identifier}");

        StoresOf(profile).Should().BeEquivalentTo("Day", "Night");
    }

    [Fact]
    public async Task Count_CountsDocuments_NotStoresOrSnapshots()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var counted = await GetAsync(client, "/api/v1/count/profile/where");

        counted.EnumerateArray().Should().ContainSingle().Which.GetProperty("count").GetInt64().Should().Be(1);
    }

    [Fact]
    public async Task DData_ServesTheDocumentOnceWithEveryStore_AndNoSnapshot()
    {
        var client = await SeedDocumentThenSwitchAsync();

        var ddata = await GetAsync(client, "/api/v2/ddata");

        ShouldBeTheSeededDocument(ddata.GetProperty("profiles").EnumerateArray().Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task ProfileSwitchesNewerThanTheDocument_DoNotCrowdItOutOfAPageOfTen()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        await PostAsync(client, "/api/v1/profile", Document(NewerId, "Night", now.AddDays(-1), "Day", "Night"));
        await PostAsync(client, "/api/v1/treatments", Enumerable.Range(0, 12).Select(i => new
        {
            eventType = "Profile Switch",
            profile = "Exercise",
            profileJson = JsonSerializer.Serialize(StoreData(5)),
            created_at = now.AddMinutes(-i).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = now.AddMinutes(-i).ToUnixTimeMilliseconds(),
            enteredBy = "AndroidAPS",
        }).ToArray());

        var listed = await GetAsync(client, "/api/v1/profile?count=10");
        var ddata = await GetAsync(client, "/api/v2/ddata");

        ShouldBeTheSeededDocument(listed.EnumerateArray().Should().ContainSingle().Subject);
        ShouldBeTheSeededDocument(ddata.GetProperty("profiles").EnumerateArray().Should().ContainSingle().Subject);
    }

    [Fact]
    public async Task Paging_IsByDocument()
    {
        var client = CreateAuthenticatedClient();
        var now = DateTimeOffset.UtcNow;
        await PostAsync(client, "/api/v1/profile", Document(OlderId, "B", now.AddDays(-2), "A", "B"));
        await PostAsync(client, "/api/v1/profile", Document(NewerId, "Night", now.AddDays(-1), "Day", "Night"));

        var first = await GetAsync(client, "/api/v1/profile?count=1");
        var second = await GetAsync(client, "/api/v3/profile?limit=1&skip=1&sort$desc=created_at");

        ShouldBeTheSeededDocument(first.EnumerateArray().Should().ContainSingle().Subject);
        var older = second.EnumerateArray().Should().ContainSingle().Subject;
        StoresOf(older).Should().BeEquivalentTo("A", "B");
        (await GetAsync(client, "/api/v1/count/profile/where"))
            .EnumerateArray().Single().GetProperty("count").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task V1Current_AfterTheOtherStoreIsReStampedThroughV4_StillNamesTheDefaultStore()
    {
        var client = await SeedDocumentThenSwitchAsync();
        var day = JsonNode.Parse(
            (await GetAsync(client, "/api/v4/profile/settings/by-name/Day")).EnumerateArray().Single().GetRawText())!;
        day["timestamp"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        (await client.PutAsJsonAsync($"/api/v4/profile/settings/{day["id"]}", day))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        ShouldBeTheSeededDocument(await GetAsync(client, "/api/v1/profile/current"));
    }

    private async Task SeedTwelveDocumentsAndTwelveSwitchesAsync(HttpClient client)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 12; i++)
            await PostAsync(client, "/api/v1/profile", Document($"5f8d0c1e8a7b4c3d9e5f02{i:x2}", "Night", now.AddDays(i - 12), "Day", "Night"));
        await PostAsync(client, "/api/v1/treatments", Enumerable.Range(0, 12).Select(i => new
        {
            eventType = "Profile Switch",
            profile = "Exercise",
            profileJson = JsonSerializer.Serialize(StoreData(5)),
            created_at = now.AddMinutes(-i).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = now.AddMinutes(-i).ToUnixTimeMilliseconds(),
            enteredBy = "AndroidAPS",
        }).ToArray());
    }

    [Fact]
    public async Task Current_RanksAndLoadsOnlyTheNewestDocument()
    {
        await SeedTwelveDocumentsAndTwelveSwitchesAsync(CreateAuthenticatedClient());

        using var reads = new TherapySettingsRowReads();
        var current = await WithTenantScopeAsync(services =>
            services.GetRequiredService<IProfileProjectionService>().GetCurrentProfileAsync());

        current!.Id.Should().Be("5f8d0c1e8a7b4c3d9e5f020b");
        current.Store.Keys.Should().BeEquivalentTo("Day", "Night");
        reads.RankedIds.Should().Be(1);
        reads.FullRows.Should().Be(2);
    }

    [Fact]
    public async Task APage_RanksAndLoadsOnlyItsDocuments()
    {
        await SeedTwelveDocumentsAndTwelveSwitchesAsync(CreateAuthenticatedClient());

        using var reads = new TherapySettingsRowReads();
        var page = await WithTenantScopeAsync(async services =>
            (await services.GetRequiredService<IProfileProjectionService>().GetProfilesAsync(count: 3, skip: 2)).ToList());

        page.Select(p => p.Id).Should().Equal(
            "5f8d0c1e8a7b4c3d9e5f0209", "5f8d0c1e8a7b4c3d9e5f0208", "5f8d0c1e8a7b4c3d9e5f0207");
        reads.RankedIds.Should().Be(3);
        reads.FullRows.Should().Be(6);
    }

    [Fact]
    public async Task Count_IsOneRowCountedInTheDatabase()
    {
        await SeedTwelveDocumentsAndTwelveSwitchesAsync(CreateAuthenticatedClient());

        using var reads = new TherapySettingsRowReads();
        var count = await WithTenantScopeAsync(services =>
            services.GetRequiredService<IProfileProjectionService>().CountProfilesAsync());

        count.Should().Be(12);
        reads.Rows.Should().Be(1);
    }

    [Fact]
    public async Task DocumentIdsRankedInTheDatabase_MatchDocumentIdOf_OrdinallyOrdered()
    {
        var at = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
        TherapySettings Row(string? legacyId, string store, DateTime timestamp) =>
            new() { LegacyId = legacyId, ProfileName = store, Timestamp = timestamp };
        var rows = new[]
        {
            Row("aDoc:Day", "Day", at),
            Row("aDoc:Night", "Night", at.AddHours(-1)),
            Row("BDoc:Day", "Day", at),
            Row("plain", "Day", at.AddHours(-2)),
            Row("colon:b:c", "b:c", at.AddHours(-3)),
            Row(null, "V4Only", at.AddHours(-4)),
            Row($"snap:Exercise{TherapySettings.ProfileSwitchStoreMarker}1", $"Exercise{TherapySettings.ProfileSwitchStoreMarker}1", at.AddHours(1)),
        };

        var (stored, ranked) = await WithTenantScopeAsync(async services =>
        {
            var repository = services.GetRequiredService<ITherapySettingsRepository>();
            var created = new List<TherapySettings>();
            foreach (var row in rows)
                created.Add(await repository.CreateAsync(row, WriteOrigin.Live));
            return (created, await repository.GetDocumentIdsAsync());
        });

        ranked.Should().Equal(
            "aDoc", "BDoc", "plain", "colon",
            TherapySettings.DocumentIdOf(stored.Single(r => r.ProfileName == "V4Only")));
        string.CompareOrdinal("aDoc", "BDoc").Should().BePositive("the tie between them is broken ordinally, descending");
    }

    /// <summary>
    /// Counts the <c>therapy_settings</c> rows EF reads in the async flow that created it (all rows,
    /// whole rows, and ranked document ids), from
    /// EF's diagnostic events, so reads by other tests running in parallel are not counted. A reader's
    /// read count includes the read that finds the end of the rows.
    /// </summary>
    private sealed class TherapySettingsRowReads
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private static readonly AsyncLocal<TherapySettingsRowReads?> Current = new();
        private readonly List<IDisposable> _subscriptions = [];
        private long _fullRows;
        private long _rankedIds;
        private long _rows;

        public TherapySettingsRowReads()
        {
            Current.Value = this;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public long FullRows => Interlocked.Read(ref _fullRows);

        public long RankedIds => Interlocked.Read(ref _rankedIds);

        public long Rows => Interlocked.Read(ref _rows);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == DbLoggerCategory.Name)
                lock (_subscriptions)
                    _subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key != RelationalEventId.DataReaderDisposing.Name
                || e.Value is not DataReaderDisposingEventData data
                || Current.Value != this
                || !data.Command.CommandText.Contains("therapy_settings"))
            {
                return;
            }

            var rows = data.ReadCount - 1;
            Interlocked.Add(ref _rows, rows);
            if (data.Command.CommandText.Contains("loop_settings_json"))
                Interlocked.Add(ref _fullRows, rows);
            else if (data.Command.CommandText.Contains("GROUP BY"))
                Interlocked.Add(ref _rankedIds, rows);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            Current.Value = null;
            lock (_subscriptions)
                _subscriptions.ForEach(s => s.Dispose());
        }
    }
}
