using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Caching;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Services.V4;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Random treatment sequences decomposed two ways leave the same rows in every table a treatment
/// writes, once the columns that name a row rather than describe it are normalised.
/// </summary>
[Collection(ApiIntegrationShards.Three)]
[Trait("Category", "Integration")]
public partial class TreatmentBatchEquivalenceTests : ApiIntegrationTestBase
{
    private static readonly string[] Tables =
    [
        "boluses", "carb_intakes", "bg_checks", "notes", "bolus_calculations", "device_events", "temp_basals",
        "profile_activations", "treatment_foods", "state_spans", "devices", "patient_devices",
    ];

    /// <summary>The columns that name a row or stamp its write.</summary>
    private static readonly HashSet<string> Unnormalised = ["id", "tenant_id", "created_at", "updated_at", "modified_at"];

    public static TheoryData<int> Seeds => [.. Enumerable.Range(1, 12)];

    public TreatmentBatchEquivalenceTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <remarks>
    /// The generated input differs from an uploader's in two ways, each a case where the batch side is
    /// the more correct, since the old core decomposed each treatment against whatever was stored when
    /// it arrived:
    /// a pump suspend or resume is never sent again, because the old core reopens a suspension a resume
    /// closed when its suspend is re-sent, and ends whatever suspension is open at a re-sent resume's
    /// older time;
    /// a resume never arrives before the suspension it ends, because the old core then leaves that
    /// suspension open for good.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task OldCore_OneAtATime_EqualsBatchOfOne(int seed)
    {
        var sequence = Generate(seed, ascending: false);
        await AssertEquivalentAsync(
            seed, sequence,
            async (decomposer, treatments) =>
            {
                foreach (var treatment in treatments)
                    await decomposer.DecomposeAsync(treatment, WriteOrigin.Live);
            },
            async (decomposer, treatments) =>
            {
                foreach (var treatment in treatments)
                    await decomposer.DecomposeBatchAsync([treatment], WriteOrigin.Live);
            });
    }

    /// <remarks>
    /// Timestamps ascend, the order in which one at a time is defined; besides the carve-outs of
    /// <see cref="OldCore_OneAtATime_EqualsBatchOfOne"/>, a re-send never moves a span or changes its
    /// kind, because the batch writes only a legacy id's last version where one at a time leaves what
    /// the superseded version's span ended.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task OldCore_Ascending_EqualsOneBatch(int seed)
    {
        var sequence = Generate(seed, ascending: true);
        await AssertEquivalentAsync(
            seed, sequence,
            async (decomposer, treatments) =>
            {
                foreach (var treatment in treatments)
                    await decomposer.DecomposeAsync(treatment, WriteOrigin.Live);
            },
            (decomposer, treatments) => decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live));
    }

    /// <remarks>
    /// The old batch body differs from the old core where the new batch follows the core: it writes a
    /// bolus under a temp basal's id, inserts a second carb intake for a meal a connector keyed apart,
    /// and lets a fallback stamp displace a stored attribution. The generator sends none of those.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task OldBatch_EqualsNewBatch(int seed)
    {
        var sequence = Generate(seed, ascending: true);
        await AssertEquivalentAsync(
            seed, sequence,
            (decomposer, treatments) => ((TreatmentDecomposer)decomposer).DecomposeBatchLegacyAsync(treatments, WriteOrigin.Live),
            (decomposer, treatments) => decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live));
    }

    /// <remarks>A connector's catch-up window re-sends what it sent before, one at a time or all at once.</remarks>
    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Resent_ChangesNoRow(int seed)
    {
        var sequence = Generate(seed, ascending: true);
        await AssertEquivalentAsync(
            seed, sequence,
            (decomposer, treatments) => decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live),
            async (decomposer, treatments) =>
            {
                await decomposer.DecomposeBatchAsync(treatments, WriteOrigin.Live);
                var again = sequence.Select(json => JsonSerializer.Deserialize<Treatment>(json)!).ToList();
                await decomposer.DecomposeBatchAsync(again, WriteOrigin.Live);
                foreach (var json in sequence)
                    await decomposer.DecomposeBatchAsync([JsonSerializer.Deserialize<Treatment>(json)!], WriteOrigin.Live);
            });
    }

    private async Task AssertEquivalentAsync(
        int seed, IReadOnlyList<string> sequence,
        Func<ITreatmentDecomposer, IReadOnlyList<Treatment>, Task> expected,
        Func<ITreatmentDecomposer, IReadOnlyList<Treatment>, Task> actual)
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
            foreach (var treatment in sequence)
                Output.WriteLine($"  {treatment}");
            Output.WriteLine(differences.ToString());
        }

        differences.ToString().Should().BeEmpty($"seed {seed} decomposes to the same rows both ways");
        want.Values.Sum(rows => rows.Count).Should().BeGreaterThan(0);
    }

    /// <summary>Decomposes a fresh copy of <paramref name="sequence"/> into emptied tables and reads them back normalised.</summary>
    private async Task<Dictionary<string, List<string>>> DecomposeAsync(
        IReadOnlyList<string> sequence, Func<ITreatmentDecomposer, IReadOnlyList<Treatment>, Task> decompose)
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        await using (var truncate = conn.CreateCommand())
        {
            truncate.CommandText = $"TRUNCATE TABLE {string.Join(", ", Tables)}, linked_records CASCADE";
            await truncate.ExecuteNonQueryAsync();
        }

        var treatments = sequence.Select(json => JsonSerializer.Deserialize<Treatment>(json)!).ToList();
        await WithTenantScopeAsync(async services =>
        {
            await services.GetRequiredService<ITenantCache>().RemoveTenantAsync(Fixture.TenantId);
            // A pump in use, so records the batch attributes to no registered device are stamped to it.
            await services.GetRequiredService<IPatientDeviceRepository>().CreateAsync(new PatientDevice
            {
                DeviceCategory = DeviceCategory.InsulinPump,
                Manufacturer = "Insulet",
                Model = "Omnipod DASH",
                StartDate = new DateOnly(2026, 9, 1),
                IsCurrent = true,
            }, WriteOrigin.Live);
            await decompose(services.GetRequiredService<ITreatmentDecomposer>(), treatments);
            return 0;
        });

        var rows = new Dictionary<string, List<JsonObject>>();
        foreach (var table in Tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = table == "treatment_foods"
                ? "SELECT row_to_json(t)::text FROM treatment_foods t"
                : $"SELECT row_to_json(t)::text FROM {table} t WHERE tenant_id = @tenant";
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
    /// rows read the same: a record by its table, legacy id and time, a group by its first record's
    /// name, a device by its identity, a span by what it covers. <see cref="Unnormalised"/> and
    /// <c>sys_</c> columns are dropped, and a deletion stamp reads only as deleted.
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
        foreach (var table in new[] { "boluses", "carb_intakes", "bg_checks", "notes", "bolus_calculations", "device_events", "temp_basals", "profile_activations" })
        {
            foreach (var row in rows[table])
            {
                var key = $"{(string?)row["legacy_id"] ?? "-"}@{row["timestamp"] ?? row["start_timestamp"]}|{(row["deleted_at"] is null ? "live" : "deleted")}";
                Name(row, $"{table}:{key}");
                if ((string?)row["correlation_id"] is { } correlation
                    && (!groups.TryGetValue(correlation, out var group) || string.CompareOrdinal(key, group) < 0))
                    groups[correlation] = key;
            }
        }

        foreach (var (correlation, key) in groups)
            names[correlation] = $"group:{key}";

        foreach (var row in rows["patient_devices"])
            Name(row, $"patient-device:{Rename((string?)row["device_id"])}|{row["start_date"]}");
        foreach (var row in rows["state_spans"])
            Name(row, $"span:{row["category"]}|{row["state"]}|{Rename((string?)row["original_id"])}|{row["start_timestamp"]}");
        foreach (var row in rows["treatment_foods"])
            Name(row, $"food:{Rename((string?)row["carb_intake_id"])}|{row["note"]}");

        return rows.ToDictionary(
            t => t.Key,
            t => t.Value
                .Select(row => new JsonObject(row
                    .Where(p => !Unnormalised.Contains(p.Key) && !p.Key.StartsWith("sys_", StringComparison.Ordinal))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => KeyValuePair.Create(p.Key, p.Key == "deleted_at"
                        ? (p.Value is null ? null : JsonValue.Create("deleted"))
                        : Renamed(p.Value)))).ToJsonString())
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

    /// <summary>
    /// A treatment sequence as an uploader sends it, serialised so each run decomposes its own copy:
    /// boluses with and without a wizard's calculation, carbs with a food, BG checks, notes, temp basals
    /// (absolute, percent, zero, cancel), profile switches, overrides, temporary targets, pump suspends
    /// and resumes, site and sensor changes, re-sends of a legacy id, records with a client id, records
    /// with no id, and a type Nocturne does not store. Unless <paramref name="ascending"/>, neighbours
    /// arrive out of order, but never a pump suspend and resume.
    /// </summary>
    private static List<string> Generate(int seed, bool ascending)
    {
        var random = new Random(seed);
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(seed);
        var aaps = seed % 2 == 0;
        var suspended = false;
        var items = new List<(DateTime At, bool Pinned, string Json)>();

        string Iso(DateTime at) => at.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        for (var i = 0; i < 40; i++)
        {
            var at = start.AddMinutes(7 * i).AddSeconds(random.Next(0, 60));
            var id = $"6c{seed:x2}{i:x4}0000000000000000"[..24];
            var common = new Dictionary<string, object?>
            {
                ["_id"] = id,
                ["created_at"] = Iso(at),
                ["enteredBy"] = aaps ? "AAPS" : "Loop",
            };
            if (aaps)
            {
                common["pumpType"] = "OMNIPOD_DASH";
                common["pumpSerial"] = "P-" + seed;
                common["app"] = "AAPS";
            }
            if (random.NextDouble() < 0.15)
                common["id"] = $"client-{seed}-{i}";

            var pinned = false;
            var roll = random.Next(0, 17);
            Dictionary<string, object?> body = roll switch
            {
                0 => new() { ["eventType"] = "Correction Bolus", ["insulin"] = Math.Round(random.NextDouble() * 3 + 0.1, 2) },
                1 => new() { ["eventType"] = "SMB", ["insulin"] = Math.Round(random.NextDouble() + 0.05, 2), ["automatic"] = true },
                2 => new()
                {
                    ["eventType"] = "Bolus Wizard", ["insulin"] = Math.Round(random.NextDouble() * 5 + 0.5, 2),
                    ["carbs"] = random.Next(10, 80), ["bloodGlucoseInput"] = random.Next(80, 250), ["CR"] = 10,
                    ["insulinOnBoard"] = Math.Round(random.NextDouble(), 2),
                    ["notes"] = random.NextDouble() < 0.5 ? "lunch" : null, ["foodType"] = random.NextDouble() < 0.5 ? "pasta" : null,
                },
                3 => new() { ["eventType"] = "Bolus Wizard", ["carbs"] = random.Next(10, 40), ["bloodGlucoseInput"] = random.Next(80, 250) },
                4 => new() { ["eventType"] = "Meal Bolus", ["insulin"] = Math.Round(random.NextDouble() * 4 + 0.5, 2), ["carbs"] = random.Next(10, 60), ["foodType"] = random.NextDouble() < 0.5 ? "rice" : null },
                5 => new() { ["eventType"] = "Carb Correction", ["carbs"] = random.Next(5, 30), ["foodType"] = random.NextDouble() < 0.5 ? "juice" : null },
                6 => new() { ["eventType"] = "BG Check", ["glucose"] = random.Next(60, 250), ["glucoseType"] = "Finger", ["units"] = "mg/dl" },
                7 => new() { ["eventType"] = random.NextDouble() < 0.7 ? "Note" : "Announcement", ["notes"] = $"note {i}" },
                8 => random.Next(0, 4) switch
                {
                    0 => new() { ["eventType"] = "Temp Basal", ["absolute"] = Math.Round(random.NextDouble() * 2, 2), ["rate"] = 1.0, ["duration"] = 30 },
                    1 => new() { ["eventType"] = "Temp Basal", ["percent"] = -50, ["duration"] = 30 },
                    2 => new() { ["eventType"] = "Temp Basal", ["absolute"] = 0, ["rate"] = 0, ["duration"] = 30 },
                    _ => new() { ["eventType"] = "Temp Basal", ["duration"] = 0 },
                },
                9 => new()
                {
                    ["eventType"] = "Profile Switch", ["profile"] = random.NextDouble() < 0.5 ? "Default" : "Sport",
                    ["duration"] = random.NextDouble() < 0.5 ? 0 : 60, ["percentage"] = new[] { 80, 100, 110 }[random.Next(0, 3)],
                },
                10 => new() { ["eventType"] = "Temporary Override", ["duration"] = random.Next(20, 90), ["reason"] = "Workout", ["insulinNeedsScaleFactor"] = 0.6 },
                11 => random.NextDouble() < 0.8
                    ? new() { ["eventType"] = "Temporary Target", ["duration"] = random.Next(20, 90), ["targetTop"] = 140, ["targetBottom"] = 120, ["units"] = "mg/dl", ["reason"] = "Activity" }
                    : new() { ["eventType"] = "Temporary Target Cancel", ["duration"] = 0 },
                12 => new() { ["eventType"] = (suspended = !suspended) ? "Pump Suspend" : "Pump Resume" },
                13 => new() { ["eventType"] = random.NextDouble() < 0.5 ? "Site Change" : "Sensor Change", ["notes"] = random.NextDouble() < 0.5 ? "new" : null },
                14 => new() { ["eventType"] = "Exercise Unsupported Kind" },
                15 => new() { ["eventType"] = "BG Check", ["glucose"] = random.Next(60, 250), ["glucoseType"] = "Sensor", ["units"] = "mg/dl", ["_id"] = null },
                _ => new() { ["eventType"] = "Correction Bolus", ["insulin"] = Math.Round(random.NextDouble() * 2 + 0.1, 2), ["notes"] = "with a note" },
            };
            pinned = roll == 12;

            foreach (var (key, value) in body)
                common[key] = value;
            var json = new JsonObject(common
                .Where(p => p.Value is not null)
                .Select(p => KeyValuePair.Create(p.Key, JsonSerializer.SerializeToNode(p.Value))));
            items.Add((at, pinned, json.ToJsonString()));

            if (!pinned && body.ContainsKey("insulin") && random.NextDouble() < 0.2)
            {
                // A re-send, exact or with the dose moved since.
                if (random.NextDouble() < 0.5)
                    json["insulin"] = Math.Round(random.NextDouble() * 3 + 0.1, 2);
                items.Add((at, false, json.ToJsonString()));
            }
            else if (!pinned && random.NextDouble() < 0.08)
            {
                items.Add((at, false, json.ToJsonString()));
            }
        }

        if (!ascending)
        {
            for (var i = 0; i + 1 < items.Count; i++)
            {
                if (!items[i].Pinned && !items[i + 1].Pinned && random.NextDouble() < 0.2)
                    (items[i], items[i + 1]) = (items[i + 1], items[i]);
            }
        }

        return [.. items.Select(i => i.Json)];
    }
}
