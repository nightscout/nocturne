using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Caching;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Random profile document sequences decomposed two ways leave the same settings and schedules,
/// once the columns that name a row rather than describe it are normalised.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public class ProfileBatchEquivalenceTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables =
        ["therapy_settings", "basal_schedules", "carb_ratio_schedules", "sensitivity_schedules", "target_range_schedules"];

    private static readonly string[] StoreNames = ["Default", "Sport", "Night"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    public ProfileBatchEquivalenceTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>The import a migration runs, one document per call or all in one.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async services =>
            {
                var decomposer = services.GetRequiredService<IProfileDecomposer>();
                foreach (var profile in Generate(seed))
                    await decomposer.DecomposeAsync(profile, WriteOrigin.Backfill);
            },
            services => services.GetRequiredService<IProfileDecomposer>().DecomposeBatchAsync(Generate(seed), WriteOrigin.Backfill));
    }

    /// <remarks>The v1 and v3 POST, one document per request or all in one.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Live_OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async services =>
            {
                var writes = services.GetRequiredService<IProfileWriteService>();
                foreach (var profile in Generate(seed))
                    await writes.CreateProfilesAsync([profile]);
            },
            services => services.GetRequiredService<IProfileWriteService>().CreateProfilesAsync(Generate(seed)));
    }

    /// <remarks>A connector re-publishes its profile set, one at a time or all at once.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Resent_ChangesNoRow(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            services => services.GetRequiredService<IProfileDecomposer>().DecomposeBatchAsync(Generate(seed), WriteOrigin.Live),
            async services =>
            {
                var decomposer = services.GetRequiredService<IProfileDecomposer>();
                await decomposer.DecomposeBatchAsync(Generate(seed), WriteOrigin.Live);
                await decomposer.DecomposeBatchAsync(Generate(seed), WriteOrigin.Live);
                foreach (var profile in Generate(seed))
                    await decomposer.DecomposeAsync(profile, WriteOrigin.Live);
            });
    }

    /// <remarks>
    /// A document replaced by PUT a few times reads as its last version stored fresh: the stores it
    /// dropped are gone, the ones it kept or added hold its values. A dropped store is deleted rather
    /// than absent, so only live rows are compared.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Replaced_EqualsLastVersionStoredFresh(int seed)
    {
        var versions = Versions(seed);
        await AssertEquivalentAsync(
            seed,
            services => services.GetRequiredService<IProfileWriteService>().CreateProfilesAsync([Versions(seed)[^1]]),
            async services =>
            {
                var writes = services.GetRequiredService<IProfileWriteService>();
                await writes.CreateProfilesAsync([Versions(seed)[0]]);
                foreach (var version in Versions(seed).Skip(1))
                    (await writes.UpdateProfileAsync(version.Id!, version)).Should().NotBeNull();
            },
            liveOnly: true,
            sequence: versions);
    }

    private async Task AssertEquivalentAsync(
        int seed, Func<IServiceProvider, Task> expected, Func<IServiceProvider, Task> actual,
        bool liveOnly = false, IReadOnlyList<Profile>? sequence = null)
    {
        var want = await DecomposeAsync(expected, liveOnly);
        var got = await DecomposeAsync(actual, liveOnly);

        var differences = DecomposedRows.Differences(want, got, Tables);
        if (differences.Length > 0)
        {
            Output.WriteLine($"Seed {seed}, sequence:");
            foreach (var profile in sequence ?? Generate(seed))
                Output.WriteLine($"  {JsonSerializer.Serialize(profile)}");
            Output.WriteLine(differences);
        }

        differences.Should().BeEmpty($"seed {seed} decomposes to the same rows both ways");
        want["therapy_settings"].Should().NotBeEmpty();
    }

    private async Task<Dictionary<string, List<string>>> DecomposeAsync(Func<IServiceProvider, Task> decompose, bool liveOnly)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await DecomposedRows.TruncateAsync(conn, Tables);

        await WithTenantScopeAsync(async services =>
        {
            await services.GetRequiredService<ITenantCache>().RemoveTenantAsync(Fixture.TenantId);
            await decompose(services);
            return 0;
        });

        var rows = await DecomposedRows.ReadAsync(conn, Fixture.TenantId, Tables, liveOnly);
        WithoutStoreGroups(rows);
        return DecomposedRows.Normalise(rows, Tables, (_, row, _) => $"{row["legacy_id"]}@{row["timestamp"]}");
    }

    /// <summary>
    /// Checks that each store's five rows share one correlation id, then drops the column.
    /// </summary>
    /// <remarks>
    /// Which stores of a document share an id is history: one at a time, a store keeps the id of the
    /// version that created it, where a batch holding two versions of a document writes each store
    /// under the id of the version that last wrote it. Reads join a store's rows by its own id, so
    /// only that is compared.
    /// </remarks>
    private static void WithoutStoreGroups(Dictionary<string, List<JsonObject>> rows)
    {
        var groupOf = rows["therapy_settings"]
            .Where(row => row["deleted_at"] is null)
            .ToDictionary(row => (string)row["legacy_id"]!, row => (string?)row["correlation_id"]);
        foreach (var table in Tables)
        {
            foreach (var row in rows[table])
            {
                if (row["deleted_at"] is null && table != "therapy_settings")
                    ((string?)row["correlation_id"]).Should().Be(groupOf[(string)row["legacy_id"]!], $"{table} {row["legacy_id"]} joins its settings row");
                row.Remove("correlation_id");
            }
        }
    }

    private static DateTime Start(int seed) => new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(seed);

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static ProfileData Store(Random random) => new()
    {
        Dia = new[] { 3.0, 4.0, 5.0 }[random.Next(0, 3)],
        CarbsHr = 20,
        Timezone = random.NextDouble() < 0.5 ? "Australia/Melbourne" : null,
        Units = random.NextDouble() < 0.3 ? "mmol" : null,
        Basal = [new() { Time = "00:00", Value = Math.Round(random.NextDouble() + 0.3, 2) }, new() { Time = "06:00", Value = Math.Round(random.NextDouble() + 0.3, 2) }],
        CarbRatio = [new() { Time = "00:00", Value = random.Next(5, 15) }],
        Sens = [new() { Time = "00:00", Value = random.Next(30, 80) }],
        TargetLow = [new() { Time = "00:00", Value = random.Next(80, 100) }],
        TargetHigh = [new() { Time = "00:00", Value = random.Next(110, 140) }],
    };

    private static Profile Document(Random random, string id, DateTime start, IReadOnlyList<string>? storeNames = null)
    {
        var pool = storeNames ?? StoreNames;
        var names = pool.Where(_ => random.NextDouble() < 0.6).DefaultIfEmpty(pool[0]).ToList();
        var profile = new Profile
        {
            Id = id,
            DefaultProfile = random.NextDouble() < 0.85 ? names[random.Next(0, names.Count)] : "Gone",
            Units = "mg/dl",
            EnteredBy = "Loop",
            Store = names.ToDictionary(name => name, _ => Store(random)),
        };
        if (random.NextDouble() < 0.15)
            profile.Mills = new DateTimeOffset(start).ToUnixTimeMilliseconds();
        else
            profile.StartDate = Iso(start);
        if (random.NextDouble() < 0.1)
            profile.Store[$"Default@@@@@{new DateTimeOffset(start).ToUnixTimeMilliseconds()}"] = Store(random);
        return profile;
    }

    /// <summary>
    /// Profile documents as uploaders send them: one to three stores each, a default naming one of
    /// them or none, a start by startDate or by mills alone, a Profile Switch's inline store now and
    /// then, and re-sends of an earlier document, some changing its default, placed later in the
    /// sequence than newer documents.
    /// </summary>
    private static List<Profile> Generate(int seed)
    {
        var random = new Random(seed);
        var documents = new List<(string Id, DateTime Start)>();
        var profiles = new List<Profile>();
        for (var i = 0; i < 8; i++)
        {
            var start = Start(seed).AddHours(6 * i);
            var id = $"5f{seed:x2}{i:x4}0000000000000000";
            documents.Add((id, start));
            profiles.Add(Document(random, id, start));
            if (i > 1 && random.NextDouble() < 0.3)
            {
                var (resendId, resendStart) = documents[random.Next(0, documents.Count)];
                profiles.Add(Document(random, resendId, resendStart));
            }
        }
        return profiles;
    }

    /// <summary>
    /// One document's versions: stores kept, changed, dropped and added from one to the next. A store
    /// once dropped is not added back, since a PUT refuses to recreate one.
    /// </summary>
    private static List<Profile> Versions(int seed)
    {
        var random = new Random(seed * 7);
        var id = $"5f{seed:x2}ffff0000000000000000";
        var versions = new List<Profile>();
        var available = StoreNames.ToList();
        for (var i = 0; i < 4 && available.Count > 0; i++)
        {
            var version = Document(random, id, Start(seed), available);
            if (versions.Count > 0)
                available.RemoveAll(name => versions[^1].Store.ContainsKey(name) && !version.Store.ContainsKey(name));
            versions.Add(version);
        }
        return versions;
    }
}
