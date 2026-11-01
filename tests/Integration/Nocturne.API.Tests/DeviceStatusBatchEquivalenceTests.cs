using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Random device status sequences decomposed two ways leave the same rows in every table a device
/// status writes, once the columns that name a row rather than describe it are normalised.
/// </summary>
[Collection(ApiIntegrationShards.Two)]
[Trait("Category", "Integration")]
public partial class DeviceStatusBatchEquivalenceTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables =
    [
        "aps_snapshots", "pump_snapshots", "uploader_snapshots", "device_status_extras", "state_spans",
        "devices", "patient_devices",
    ];

    /// <summary>The columns that name a row or stamp its write: ids, and the span's own write stamps.</summary>
    private static readonly HashSet<string> Unnormalised = ["id", "tenant_id", "created_at", "updated_at"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    public DeviceStatusBatchEquivalenceTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>
    /// The generated input differs from a connector's catch-up batch in three ways, each a case where
    /// the batch is the more correct of the two, since a status decomposed on its own sees only what
    /// was decomposed before it:
    /// timestamps ascend, because a resume decomposed before the suspension it ends leaves that
    /// suspension open for good where the batch closes it;
    /// a re-send never changes the pump's state, because the batch keeps only the latest version of a
    /// status where one at a time leaves the spans the superseded version opened;
    /// an override's timestamp is its start, as Loop sends it, because a status restating it with a
    /// later start and an end past the next status is covered without extending the span one at a
    /// time, leaving a gap the batch fills.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Ascending_OneAtATime_EqualsOneBatch(int seed)
    {
        var sequence = Generate(seed);
        await AssertEquivalentAsync(
            seed, sequence,
            async (service, statuses) =>
            {
                foreach (var status in statuses)
                    await service.CreateDeviceStatusesAsync([status]);
            },
            (service, statuses) => service.CreateDeviceStatusesAsync(statuses));
    }

    /// <remarks>
    /// A connector's catch-up window re-sends what it sent before, one status at a time or all at once.
    /// A status with no id and a snapshot has nothing to be matched by, so it is not re-sent.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Resent_ChangesNoRow(int seed)
    {
        var sequence = Generate(seed);
        await AssertEquivalentAsync(
            seed, sequence,
            (service, statuses) => service.CreateDeviceStatusesAsync(statuses),
            async (service, statuses) =>
            {
                await service.CreateDeviceStatusesAsync(statuses);
                var keyed = statuses.Where(s => s.Id is not null || (s.Pump == null && s.Loop == null && s.OpenAps == null && s.Uploader == null)).ToList();
                await service.CreateDeviceStatusesAsync(keyed);
                foreach (var status in keyed)
                    await service.CreateDeviceStatusesAsync([status]);
            });
    }

    private async Task AssertEquivalentAsync(
        int seed, IReadOnlyList<string> sequence,
        Func<IDeviceStatusService, IReadOnlyList<DeviceStatus>, Task> expected,
        Func<IDeviceStatusService, IReadOnlyList<DeviceStatus>, Task> actual)
    {
        var want = await DecomposeAsync(sequence, expected);
        var got = await DecomposeAsync(sequence, actual);

        var differences = new StringBuilder();
        foreach (var table in Tables)
        {
            var missing = want[table].Except(got[table]).ToList();
            var extra = got[table].Except(want[table]).ToList();
            if (missing.Count == 0 && extra.Count == 0 && want[table].Count == got[table].Count)
                continue;
            differences.AppendLine($"{table}: {want[table].Count} rows expected, {got[table].Count} written");
            foreach (var row in missing)
                differences.AppendLine($"  - {row}");
            foreach (var row in extra)
                differences.AppendLine($"  + {row}");
        }

        if (differences.Length > 0)
        {
            Output.WriteLine($"Seed {seed}, sequence:");
            foreach (var status in sequence)
                Output.WriteLine($"  {status}");
            Output.WriteLine(differences.ToString());
        }

        differences.ToString().Should().BeEmpty($"seed {seed} decomposes to the same rows both ways");
        want.Values.Sum(rows => rows.Count).Should().BeGreaterThan(0);
    }

    /// <summary>Decomposes a fresh copy of <paramref name="sequence"/> into emptied tables and reads them back normalised.</summary>
    private async Task<Dictionary<string, List<string>>> DecomposeAsync(
        IReadOnlyList<string> sequence, Func<IDeviceStatusService, IReadOnlyList<DeviceStatus>, Task> decompose)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using (var truncate = conn.CreateCommand())
        {
            truncate.CommandText = $"TRUNCATE TABLE {string.Join(", ", Tables)}, linked_records CASCADE";
            await truncate.ExecuteNonQueryAsync();
        }

        var statuses = sequence.Select(json => JsonSerializer.Deserialize<DeviceStatus>(json)!).ToList();
        await WithTenantScopeAsync(async services =>
        {
            await decompose(services.GetRequiredService<IDeviceStatusService>(), statuses);
            return 0;
        });

        var rows = new Dictionary<string, List<JsonObject>>();
        foreach (var table in Tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT row_to_json(t)::text FROM {table} t WHERE tenant_id = @tenant";
            cmd.Parameters.AddWithValue("tenant", Fixture.TenantId);
            await using var reader = await cmd.ExecuteReaderAsync();
            var list = new List<JsonObject>();
            while (await reader.ReadAsync())
                list.Add(JsonNode.Parse(reader.GetString(0))!.AsObject());
            rows[table] = list;
        }

        return Normalise(rows);
    }

    /// <summary>
    /// Replaces every id with a name built from what the row describes, so two runs that wrote the same
    /// rows read the same: a snapshot by its legacy id and timestamp, a group by its first snapshot, a
    /// device by its identity, a span by what it covers. <see cref="Unnormalised"/> columns are dropped.
    /// </summary>
    private static Dictionary<string, List<string>> Normalise(Dictionary<string, List<JsonObject>> rows)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Name(JsonObject row, string name)
        {
            var id = (string)row["id"]!;
            names[id] = name;
            names[MongoObjectId.FromGuid(Guid.Parse(id))] = name;
        }

        foreach (var row in rows["devices"])
            Name(row, $"device:{row["category"]}|{row["type"]}|{row["serial"]}");

        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in new[] { "aps_snapshots", "pump_snapshots", "uploader_snapshots" })
        {
            foreach (var row in rows[table])
            {
                var key = $"{(string?)row["legacy_id"] ?? "-"}@{row["timestamp"]}";
                Name(row, $"{table}:{key}");
                if ((string?)row["correlation_id"] is { } correlation
                    && (!groups.TryGetValue(correlation, out var group) || string.CompareOrdinal(key, group) < 0))
                    groups[correlation] = key;
            }
        }

        foreach (var row in rows["device_status_extras"])
        {
            if ((string?)row["correlation_id"] is { } correlation)
                groups.TryAdd(correlation, $"-@{row["timestamp"]}");
        }

        foreach (var (correlation, key) in groups)
            names[correlation] = $"group:{key}";

        foreach (var row in rows["device_status_extras"])
            Name(row, $"extras:{Rename((string?)row["correlation_id"])}");
        foreach (var row in rows["patient_devices"])
            Name(row, $"patient-device:{Rename((string?)row["device_id"])}|{row["start_date"]}");
        foreach (var row in rows["state_spans"])
            Name(row, $"span:{row["category"]}|{row["state"]}|{Rename((string?)row["original_id"])}|{row["start_timestamp"]}");

        return rows.ToDictionary(
            t => t.Key,
            t => t.Value
                .Select(row => new JsonObject(row
                    .Where(p => !Unnormalised.Contains(p.Key) && !p.Key.StartsWith("sys_", StringComparison.Ordinal))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, Renamed(p.Value)))).ToJsonString())
                .Order(StringComparer.Ordinal)
                .ToList());

        string? Rename(string? value) =>
            value is null ? null : IdPattern().Replace(value, m => names.TryGetValue(m.Value, out var name) ? name : m.Value);

        JsonNode? Renamed(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Rename(text)),
            JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Renamed(p.Value)))),
            JsonArray array => new JsonArray([.. array.Select(Renamed)]),
            _ => node?.DeepClone(),
        };
    }

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\\b[0-9a-f]{24}\\b")]
    private static partial Regex IdPattern();

    private enum Uploader { Aaps, Loop, CareLink }

    /// <summary>
    /// A device status sequence as one uploader would send it, serialised so each run decomposes its
    /// own copy. The seed picks the uploader, which fixes the sections each status carries.
    /// </summary>
    private static List<string> Generate(int seed)
    {
        var random = new Random(seed);
        var uploader = (Uploader)(seed % 3);
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(seed);
        string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        var suspended = false;
        var automatic = true;
        var overrideActive = false;
        var overrideName = "Workout";
        DateTime overrideStart = start;
        double? overrideDuration = null;

        var sequence = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            var at = start.AddMinutes(5 * i).AddSeconds(random.Next(0, 60));
            var ds = new DeviceStatus
            {
                Id = random.NextDouble() < 0.05 ? null : $"6a{seed:x2}{i:x4}0000000000000000"[..24],
                CreatedAt = Iso(at),
                Device = uploader switch
                {
                    Uploader.Aaps => "openaps://Pixel",
                    Uploader.Loop => "loop://iPhone",
                    _ => "CareLink Connect",
                },
            };
            if (uploader == Uploader.Aaps && random.NextDouble() < 0.3)
                (ds.Mills, ds.Date) = (0, new DateTimeOffset(at).ToUnixTimeMilliseconds());
            else
                ds.Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds();

            if (random.NextDouble() < 0.08)
            {
                // A status carrying no legacy-keyed snapshot.
                ds.Cgm = new CgmStatus { Manufacturer = "Dexcom", Model = "G7", Serial = "SN-G7" };
                ds.XDripJs = new XDripJsStatus { State = random.Next(0, 4), StateString = "OK" };
                sequence.Add(JsonSerializer.Serialize(ds));
                continue;
            }

            if (random.NextDouble() < 0.15)
            {
                suspended = !suspended;
            }

            if (uploader == Uploader.CareLink && random.NextDouble() < 0.2)
            {
                automatic = !automatic;
            }

            ds.Pump = new PumpStatus
            {
                Clock = Iso(at.AddSeconds(-random.Next(0, 30))),
                Reservoir = Math.Round(random.NextDouble() * 200, 1),
                Battery = new PumpBattery { Percent = random.Next(10, 100) },
                Status = new PumpStatusDetails { Suspended = suspended, Status = suspended ? "suspended" : "normal" },
                Manufacturer = uploader == Uploader.CareLink ? "Medtronic" : "Insulet",
                Model = uploader == Uploader.CareLink ? "MiniMed 780G" : "Omnipod DASH",
                Serial = uploader == Uploader.CareLink ? "NG4114235H" : null,
                PumpMode = uploader == Uploader.CareLink ? (automatic ? "Automatic" : "Manual") : null,
            };

            if (uploader != Uploader.CareLink || random.NextDouble() < 0.5)
                ds.Cgm = new CgmStatus { Manufacturer = "Dexcom", Model = "G7", Serial = "SN-G7" };

            if (random.NextDouble() < 0.2)
                ds.XDripJs = new XDripJsStatus { State = random.Next(0, 4), StateString = "OK" };

            if (uploader == Uploader.Aaps)
            {
                var enacted = random.NextDouble() < 0.5;
                ds.OpenAps = new OpenApsStatus
                {
                    Iob = new OpenApsIobData { Iob = Math.Round(random.NextDouble() * 5, 2), BasalIob = 0.5, BolusIob = 1.0, Time = Iso(at) },
                    Suggested = new OpenApsSuggested { Bg = random.Next(70, 250), EventualBG = random.Next(70, 250), Timestamp = Iso(at) },
                    Enacted = enacted
                        ? new OpenApsEnacted { Received = true, Rate = Math.Round(random.NextDouble() * 3, 2), Duration = 30, Timestamp = Iso(at) }
                        : null,
                };
            }
            else if (uploader == Uploader.Loop)
            {
                ds.Loop = new LoopStatus
                {
                    Iob = new LoopIob { Iob = Math.Round(random.NextDouble() * 5, 2), Timestamp = Iso(at) },
                    Cob = new LoopCob { Cob = random.Next(0, 60) },
                    Predicted = new LoopPredicted { Values = [random.Next(70, 250), random.Next(70, 250)], StartDate = Iso(at) },
                    Timestamp = Iso(at),
                    Enacted = random.NextDouble() < 0.5
                        ? new LoopEnacted { Received = true, Rate = Math.Round(random.NextDouble() * 3, 2), Duration = 30, Timestamp = Iso(at) }
                        : null,
                };

                if (random.NextDouble() < 0.25)
                {
                    overrideActive = !overrideActive;
                    if (overrideActive)
                    {
                        overrideName = random.NextDouble() < 0.5 ? "Workout" : "Pre-Meal";
                        overrideStart = at;
                        overrideDuration = random.NextDouble() < 0.5 ? null : random.Next(600, 3600);
                    }
                }

                if (random.NextDouble() < 0.1)
                    ds.Override = null;
                else
                    ds.Override = new OverrideStatus
                {
                    Active = overrideActive,
                    Name = overrideActive ? overrideName : null,
                    Multiplier = overrideActive ? (overrideName == "Workout" ? 0.5 : 1.2) : null,
                    Timestamp = overrideActive ? Iso(overrideStart) : null,
                    Duration = overrideActive ? overrideDuration : null,
                };
            }

            if (uploader == Uploader.CareLink && random.NextDouble() < 0.5)
                ds.UploaderBattery = random.Next(10, 100);
            else if (uploader != Uploader.CareLink)
                ds.Uploader = new UploaderStatus
                {
                    Battery = random.Next(10, 100),
                    Name = uploader == Uploader.Aaps ? "AndroidAPS" : "iPhone",
                    Type = "phone",
                };

            sequence.Add(JsonSerializer.Serialize(ds));

            if (random.NextDouble() < 0.15 && ds.Id is not null)
            {
                // A re-send, exact or with readings that moved since.
                if (random.NextDouble() < 0.5)
                {
                    ds.Pump.Reservoir = Math.Round(random.NextDouble() * 200, 1);
                    ds.Pump.Battery = new PumpBattery { Percent = random.Next(10, 100) };
                }

                sequence.Add(JsonSerializer.Serialize(ds));
            }
        }

        return sequence;
    }
}
