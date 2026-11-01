using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Caching;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Random entry sequences decomposed two ways leave the same rows in every table an entry writes,
/// once the columns that name a row rather than describe it are normalised.
/// </summary>
[Collection(ApiIntegrationShards.One)]
[Trait("Category", "Integration")]
public class EntryBatchEquivalenceTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables = ["patient_devices", "sensor_glucose", "meter_glucose", "calibrations"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    public EntryBatchEquivalenceTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>
    /// The upload as v1, v3 and the connectors reach it, one entry per call or all in one, including
    /// echoes of a stored reading that has no legacy id, which are skipped.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Ascending_OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async service =>
            {
                foreach (var entry in Generate(seed))
                    await service.CreateEntriesAsync([entry]);
            },
            service => service.CreateEntriesAsync(Generate(seed)));
    }

    /// <remarks>
    /// The edits a v1 PUT and an AAPS PATCH make, one entry at a time, against the same edits uploaded
    /// as one batch. Each PATCHed entry is PATCHed once and never PUT, so the batch can carry what the
    /// PATCH overlays onto the stored entry. An edit that leaves its device string without a serial
    /// keeps the device the stored row was attributed to.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Edits_OneAtATime_EqualsOneBatch(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            async service =>
            {
                await service.CreateEntriesAsync(Generate(seed));
                foreach (var edit in Puts(seed))
                    (await service.UpdateEntryAsync(edit.Id!, edit)).Should().NotBeNull();
                foreach (var (id, sgv) in Patches(seed))
                    (await service.PatchEntryAsync(id, JsonSerializer.SerializeToElement(new { sgv }))).Should().NotBeNull();
            },
            async service =>
            {
                await service.CreateEntriesAsync(Generate(seed));
                var edits = Puts(seed);
                foreach (var (id, sgv) in Patches(seed))
                {
                    var stored = (await service.GetEntryByIdAsync(id))!;
                    stored.Sgv = sgv;
                    edits.Add(stored);
                }
                await service.CreateEntriesAsync(edits);
            });
    }

    /// <remarks>A connector's catch-up window re-sends what it sent before, one at a time or all at once.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Resent_ChangesNoRow(int seed)
    {
        await AssertEquivalentAsync(
            seed,
            service => service.CreateEntriesAsync(Generate(seed)),
            async service =>
            {
                await service.CreateEntriesAsync(Generate(seed));
                var keyed = Generate(seed).Where(e => e.Id is not null).ToList();
                await service.CreateEntriesAsync(keyed);
                foreach (var entry in Generate(seed).Where(e => e.Id is not null))
                    await service.CreateEntriesAsync([entry]);
            });
    }

    private async Task AssertEquivalentAsync(int seed, Func<IEntryService, Task> expected, Func<IEntryService, Task> actual)
    {
        var want = await DecomposeAsync(seed, expected);
        var got = await DecomposeAsync(seed, actual);

        var differences = DecomposedRows.Differences(want, got, Tables);
        if (differences.Length > 0)
        {
            Output.WriteLine($"Seed {seed}, sequence:");
            foreach (var entry in Generate(seed))
                Output.WriteLine($"  {JsonSerializer.Serialize(entry)}");
            Output.WriteLine(differences);
        }

        differences.Should().BeEmpty($"seed {seed} decomposes to the same rows both ways");
        want["sensor_glucose"].Should().NotBeEmpty();
    }

    /// <summary>Decomposes into emptied tables over the seed's devices and unkeyed reading, and reads them back normalised.</summary>
    private async Task<Dictionary<string, List<string>>> DecomposeAsync(int seed, Func<IEntryService, Task> decompose)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await DecomposedRows.TruncateAsync(conn, Tables);

        await WithTenantScopeAsync(async services =>
        {
            await services.GetRequiredService<ITenantCache>().RemoveTenantAsync(Fixture.TenantId);
            var devices = services.GetRequiredService<IPatientDeviceRepository>();
            foreach (var (category, serial) in new[]
                     {
                         (DeviceCategory.CGM, CgmA), (DeviceCategory.CGM, CgmB), (DeviceCategory.GlucoseMeter, Meter),
                     })
            {
                await devices.CreateAsync(new PatientDevice
                {
                    DeviceCategory = category, Manufacturer = "Acme", Model = serial, SerialNumber = serial,
                    StartDate = new DateOnly(2026, 9, 1), IsCurrent = true,
                }, WriteOrigin.Live);
            }

            await services.GetRequiredService<ISensorGlucoseRepository>().BulkCreateAsync(
                [new SensorGlucose { Id = UnkeyedReading(seed), Timestamp = Start(seed).AddMinutes(-5), Mgdl = 111, Device = "native" }],
                WriteOrigin.Backfill);

            await decompose(services.GetRequiredService<IEntryService>());
            return 0;
        });

        var rows = await DecomposedRows.ReadAsync(conn, Fixture.TenantId, Tables);
        return DecomposedRows.Normalise(rows, Tables, (table, row, _) => table == "patient_devices"
            ? $"{row["serial_number"]}"
            : $"{(string?)row["legacy_id"] ?? "-"}@{row["timestamp"]}|{row["mgdl"] ?? row["slope"]}");
    }

    private const string CgmA = "SNCGMAAAA1";
    private const string CgmB = "SNCGMBBBB2";
    private const string Meter = "SNMETER001";

    private static DateTime Start(int seed) => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(seed);

    /// <summary>A uuid whose 24-hex served id is shaped as one, so an upload under it reads as an echo.</summary>
    private static Guid UnkeyedReading(int seed) => Guid.Parse($"{seed:x8}-0000-7000-8000-000000000001");

    /// <remarks>Every ninth id is shaped as a served one, so the echo lookup runs and finds nothing.</remarks>
    private static string Id(int seed, int i) => $"5e{seed:x2}{i:x4}" + (i % 9 == 4 ? "0000700080000000" : "0000000000000000");

    /// <summary>
    /// Entries as uploaders send them, in time order: CGM readings from either of two CGMs, named by
    /// serial or not, with and without glucose-processing hints, an offset or none; meter readings;
    /// calibrations; re-sends of an id with a changed value; readings with no id; an echo of the
    /// stored reading that has no legacy id; and a type Nocturne does not store.
    /// </summary>
    private static List<Entry> Generate(int seed)
    {
        var random = new Random(seed);
        var start = Start(seed);
        var entries = new List<Entry>();
        for (var i = 0; i < 40; i++)
        {
            var at = start.AddMinutes(5 * i).AddSeconds(random.Next(0, 30));
            var mills = new DateTimeOffset(at).ToUnixTimeMilliseconds();
            var id = i % 13 == 7 ? null : Id(seed, i);
            int? offset = random.NextDouble() < 0.3 ? null : 600;
            var roll = random.Next(0, 10);
            entries.Add(roll switch
            {
                <= 5 => Sgv(id, mills, offset, random),
                6 or 7 => new Entry
                {
                    Id = id, Type = "mbg", Mills = mills, Mbg = random.Next(60, 250), UtcOffset = offset,
                    Device = random.NextDouble() < 0.5 ? $"Contour {Meter}" : "Contour",
                },
                8 => new Entry
                {
                    Id = id, Type = "cal", Mills = mills, UtcOffset = offset, Device = "xDrip",
                    Slope = Math.Round(800 + random.NextDouble() * 200, 2), Intercept = Math.Round(random.NextDouble() * 30000, 2), Scale = 1,
                },
                _ => new Entry { Id = id, Type = "foo", Mills = mills, Device = "unknown" },
            });

            if (id is not null && random.NextDouble() < 0.15)
            {
                var resend = entries[^1];
                entries.Add(resend.Type == "sgv" ? Sgv(id, mills, offset, random) : resend);
            }
        }

        entries.Insert(10, new Entry
        {
            Id = MongoObjectId.FromGuid(UnkeyedReading(seed)), Type = "sgv", Mills = new DateTimeOffset(start.AddMinutes(-5)).ToUnixTimeMilliseconds(),
            Sgv = 111, Device = "native",
        });
        return entries;
    }

    private static Entry Sgv(string? id, long mills, int? offset, Random random)
    {
        var device = random.Next(0, 4) switch
        {
            0 => $"xDrip-DexcomG6 {CgmA}",
            1 => $"xDrip-DexcomG6 {CgmB}",
            _ => "xDrip-DexcomG6",
        };
        var entry = new Entry
        {
            Id = id, Type = "sgv", Mills = mills, Sgv = random.Next(40, 400), UtcOffset = offset, Device = device,
            Direction = new[] { "Flat", "FortyFiveUp", "SingleDown", "NOT COMPUTABLE" }[random.Next(0, 4)],
            Noise = random.Next(0, 3), Filtered = random.Next(100000, 200000), Unfiltered = random.Next(100000, 200000), Rssi = 100,
        };
        switch (random.Next(0, 4))
        {
            case 0:
                entry.AdditionalProperties = new() { ["glucoseProcessing"] = JsonSerializer.SerializeToElement("Smoothed") };
                break;
            case 1:
                entry.AdditionalProperties = new()
                {
                    ["smoothedMgdl"] = JsonSerializer.SerializeToElement(entry.Sgv!.Value - 2),
                    ["unsmoothedMgdl"] = JsonSerializer.SerializeToElement(entry.Sgv!.Value + 3),
                };
                break;
        }
        return entry;
    }

    /// <summary>
    /// Full-document edits of about a third of the keyed sgv and mbg entries, a few edited twice, each
    /// keeping the stored device string or dropping its serial.
    /// </summary>
    /// <remarks>
    /// An edit never names a serial other than the stored one: the per-record path kept the stored
    /// device over a serial the edit names, where the batch takes the named one, the more correct.
    /// </remarks>
    private static List<Entry> Puts(int seed)
    {
        var random = new Random(seed * 31);
        var edits = new List<Entry>();
        foreach (var entry in Keyed(seed).Where(e => e.Type is "sgv" or "mbg").GroupBy(e => e.Id).Select(g => g.Last()))
        {
            if (Patched(entry.Id!) || random.NextDouble() > 0.35)
                continue;
            var times = random.NextDouble() < 0.2 ? 2 : 1;
            for (var n = 0; n < times; n++)
            {
                edits.Add(new Entry
                {
                    Id = entry.Id, Type = entry.Type, Mills = entry.Mills, UtcOffset = entry.UtcOffset,
                    Sgv = entry.Type == "sgv" ? random.Next(40, 400) : null,
                    Mbg = entry.Type == "mbg" ? random.Next(60, 250) : null,
                    Direction = entry.Direction, Noise = entry.Noise,
                    Device = random.NextDouble() < 0.5 ? entry.Device : entry.Type == "sgv" ? "xDrip-DexcomG6" : "Contour",
                });
            }
        }
        return edits;
    }

    /// <summary>The generated entries with an id of their own; the echo of the unkeyed reading is not one.</summary>
    private static IEnumerable<Entry> Keyed(int seed) =>
        Generate(seed).Where(e => e.Id is not null && e.Id != MongoObjectId.FromGuid(UnkeyedReading(seed)));

    private static bool Patched(string id) => Convert.ToInt32(id[4..8], 16) % 5 == 2;

    private static List<(string Id, int Sgv)> Patches(int seed)
    {
        var random = new Random(seed * 17);
        return
        [
            .. Keyed(seed)
                .Where(e => e.Type == "sgv" && Patched(e.Id!))
                .DistinctBy(e => e.Id)
                .Select(e => (e.Id!, random.Next(40, 400))),
        ];
    }
}
