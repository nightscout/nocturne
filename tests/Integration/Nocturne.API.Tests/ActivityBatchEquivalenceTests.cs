using Nocturne.API.Services.Health;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Health;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data.Entities;
using Nocturne.Infrastructure.Data.Mappers;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Random sensor activity sequences decomposed two ways leave the same heart rates and step counts,
/// once the columns that name a row rather than describe it are normalised.
/// </summary>
/// <remarks>
/// Only sensor data is generated: a regular activity goes to the state spans through the activity
/// service, not the decomposer, and a sleep activity to the sleep sessions.
/// </remarks>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public class ActivityBatchEquivalenceTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables = ["heart_rates", "step_counts"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    public ActivityBatchEquivalenceTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>The import a migration runs, one activity per call or all in one.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async services =>
            {
                var decomposer = services.GetRequiredService<IActivityDecomposer>();
                foreach (var activity in Generate(seed))
                    await decomposer.DecomposeBatchAsync([activity], WriteOrigin.Backfill);
                return [];
            },
            async services =>
            {
                await services.GetRequiredService<IActivityDecomposer>().DecomposeBatchAsync(Generate(seed), WriteOrigin.Backfill);
                return [];
            });
    }

    /// <remarks>
    /// The v1 POST, one activity per request or all in one, which also answers with the activities it
    /// stored: every one but those the user deleted.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Live_OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async services =>
            {
                var service = services.GetRequiredService<IActivityService>();
                var stored = new List<string>();
                foreach (var activity in Generate(seed))
                    stored.AddRange((await service.CreateActivitiesAsync([activity])).Select(Describe));
                return stored;
            },
            async services => [.. (await services.GetRequiredService<IActivityService>().CreateActivitiesAsync(Generate(seed))).Select(Describe)]);
    }

    /// <remarks>
    /// An uploader re-sends what it sent before, one at a time or all at once. A record with neither an
    /// id nor a sync key has nothing to be matched by, so it is not re-sent.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Resent_ChangesNoRow(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async services =>
            {
                await services.GetRequiredService<IActivityDecomposer>().DecomposeBatchAsync(Generate(seed), WriteOrigin.Live);
                return [];
            },
            async services =>
            {
                var decomposer = services.GetRequiredService<IActivityDecomposer>();
                await decomposer.DecomposeBatchAsync(Generate(seed), WriteOrigin.Live);
                await decomposer.DecomposeBatchAsync(Matchable(seed), WriteOrigin.Live);
                foreach (var activity in Matchable(seed))
                    await decomposer.DecomposeBatchAsync([activity], WriteOrigin.Live);
                return [];
            });
    }

    private async Task AssertEquivalentAsync(
        int seed, Func<IServiceProvider, Task<List<string>>> expected, Func<IServiceProvider, Task<List<string>>> actual)
    {
        var (want, wantStored) = await DecomposeAsync(seed, expected);
        var (got, gotStored) = await DecomposeAsync(seed, actual);

        var differences = DecomposedRows.Differences(want, got, Tables);
        if (differences.Length > 0)
        {
            Output.WriteLine($"Seed {seed}, sequence:");
            foreach (var activity in Generate(seed))
                Output.WriteLine($"  {JsonSerializer.Serialize(activity)}");
            Output.WriteLine(differences);
        }

        differences.Should().BeEmpty($"seed {seed} decomposes to the same rows both ways");
        gotStored.Should().Equal(wantStored, "both ways answer with the same stored activities");
        want.Values.Sum(rows => rows.Count).Should().BeGreaterThan(0);
    }

    private async Task<(Dictionary<string, List<string>> Rows, List<string> Stored)> DecomposeAsync(
        int seed, Func<IServiceProvider, Task<List<string>>> decompose)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await DecomposedRows.TruncateAsync(conn, Tables);
        await SeedTombstonesAsync(seed);

        var stored = await WithTenantScopeAsync(decompose);

        var rows = await DecomposedRows.ReadAsync(conn, Fixture.TenantId, Tables);
        return (DecomposedRows.Normalise(rows, Tables, (_, row, _) =>
            $"{(string?)row["original_id"] ?? (string?)row["sync_identifier"] ?? "-"}@{row["timestamp"]}"), stored);
    }

    private static string Describe(Activity activity) => $"{activity.Id ?? activity.Type}@{activity.Mills}";

    /// <summary>
    /// The rows the generated ids and sync keys meet: a heart rate and an xDrip step count the user
    /// deleted, and a heart rate by id and by sync key that the system swept.
    /// </summary>
    private async Task SeedTombstonesAsync(int seed)
    {
        var at = Start(seed).AddDays(-1);
        await using var db = Fixture.CreateDbContext(Fixture.TenantId);
        var userHeartRate = new HeartRateEntity
        {
            Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, OriginalId = UserDeletedId(seed), Timestamp = at, Bpm = 60, DeletedAt = at,
        };
        var userSteps = new StepCountEntity
        {
            Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, DataSource = DataSources.XDrip,
            SyncIdentifier = $"steps-total:{XDripMills(seed, 3)}", Timestamp = at, Metric = 10, DeletedAt = at,
        };
        db.AddRange(
            userHeartRate,
            userSteps,
            new HeartRateEntity
            {
                Id = MapperHelpers.ParseIdToGuid(SweptId(seed)),
                TenantId = Fixture.TenantId, OriginalId = SweptId(seed), Timestamp = at, Bpm = 61, DeletedAt = at,
            },
            new HeartRateEntity
            {
                Id = Guid.CreateVersion7(), TenantId = Fixture.TenantId, DataSource = DataSources.XDrip,
                SyncIdentifier = $"hr-bpm:{XDripMills(seed, 5)}", Timestamp = at, Bpm = 62, DeletedAt = at,
            });
        db.Entry(userHeartRate).Property("DeletedByUser").CurrentValue = true;
        db.Entry(userSteps).Property("DeletedByUser").CurrentValue = true;
        await db.SaveChangesAsync();
    }

    private static DateTime Start(int seed) => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(seed);

    private static string Id(int seed, int i) => $"5a{seed:x2}{i:x4}0000000000000000";

    private static string UserDeletedId(int seed) => $"5ade{seed:x4}0000000000000000";

    private static string SweptId(int seed) => $"5aee{seed:x4}0000000000000000";

    private static long XDripMills(int seed, int i) => new DateTimeOffset(Start(seed).AddMinutes(i)).ToUnixTimeMilliseconds();

    /// <summary>
    /// The generated activities a re-send can be matched by: a Mongo id, or an xDrip sync key. A uuid id
    /// is stored as the row's key with no original id, so nothing matches its re-send.
    /// </summary>
    private static IReadOnlyList<Activity> Matchable(int seed) =>
        [.. Generate(seed).Where(a => (a.Id is not null && !Guid.TryParse(a.Id, out _)) || (a.Id is null && a.Type is "hr-bpm" or "steps-total"))];

    /// <summary>
    /// Sensor activities as uploaders send them, in time order: heart rates and step counts with an id,
    /// xDrip heart rates and running step totals keyed by their time, heart rates with neither, a
    /// heart rate under a uuid id, re-sends of an id and of a sync key with a changed value, and the
    /// ids and sync keys of rows the user deleted and the system swept.
    /// </summary>
    private static List<Activity> Generate(int seed)
    {
        var random = new Random(seed);
        var start = Start(seed);
        var activities = new List<Activity>();

        Activity Of(string? id, string type, long mills, Dictionary<string, object> props) => new()
        {
            Id = id, Type = type, Mills = mills, EnteredBy = random.NextDouble() < 0.5 ? "xDrip" : "watch",
            AdditionalProperties = props.ToDictionary(p => p.Key, p => (object)JsonSerializer.SerializeToElement(p.Value)),
        };

        for (var i = 0; i < 30; i++)
        {
            var mills = XDripMills(seed, i);
            var id = Id(seed, i);
            switch (random.Next(0, 5))
            {
                case 0:
                    activities.Add(Of(id, "heartrate", mills, new() { ["bpm"] = random.Next(50, 160), ["accuracy"] = random.Next(0, 4) }));
                    break;
                case 1:
                    activities.Add(Of(null, "hr-bpm", mills, new() { ["bpm"] = random.Next(50, 160) }));
                    break;
                case 2:
                    activities.Add(Of(id, "steps", mills, new() { ["metric"] = random.Next(0, 2000), ["source"] = random.Next(0, 2) }));
                    break;
                case 3:
                    activities.Add(Of(null, "steps-total", mills, new() { ["steps"] = random.Next(0, 9000) }));
                    break;
                default:
                    activities.Add(Of(null, "heartrate", mills, new() { ["bpm"] = random.Next(50, 160) }));
                    break;
            }

            if (random.NextDouble() < 0.2 && activities[^1] is { } last && (last.Id is not null || last.Type is "hr-bpm" or "steps-total"))
            {
                var props = last.AdditionalProperties!.ContainsKey("bpm")
                    ? new Dictionary<string, object> { ["bpm"] = random.Next(50, 160) }
                    : last.Type == "steps-total"
                        ? new Dictionary<string, object> { ["steps"] = random.Next(0, 9000) }
                        : new Dictionary<string, object> { ["metric"] = random.Next(0, 2000) };
                activities.Add(Of(last.Id, last.Type!, last.Mills, props));
            }
        }

        activities.Add(Of(Guid.Parse($"{seed:x8}-0000-7000-8000-0000000000aa").ToString(), "heartrate", XDripMills(seed, 31), new() { ["bpm"] = 77 }));
        activities.Add(Of(UserDeletedId(seed), "heartrate", XDripMills(seed, 32), new() { ["bpm"] = 78 }));
        activities.Add(Of(SweptId(seed), "heartrate", XDripMills(seed, 33), new() { ["bpm"] = 79 }));
        activities.Add(Of(null, "steps-total", XDripMills(seed, 3), new() { ["steps"] = 4000 }));
        activities.Add(Of(null, "hr-bpm", XDripMills(seed, 5), new() { ["bpm"] = 80 }));
        return [.. activities.OrderBy(a => a.Mills)];
    }
}
