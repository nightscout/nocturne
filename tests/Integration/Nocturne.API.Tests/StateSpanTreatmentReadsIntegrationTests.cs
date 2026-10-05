using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Contracts.Profiles.Resolvers;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Temporary Override, Temporary Target and Profile Switch treatments are stored only as state spans.
/// LoopFollow, LoopCaregiver and AAPS followers read them back through the v1 and v3 treatment reads,
/// and Loop deletes them by the id it uploaded them under.
/// </summary>
[Trait("Category", "Integration")]
public class StateSpanTreatmentReadsIntegrationTests : ApiIntegrationTestBase
{
    public StateSpanTreatmentReadsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static string MinutesAgo(int minutes) =>
        DateTime.UtcNow.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public static TheoryData<string> EventTypes =>
        new() { "Temporary Override", "Temporary Target", "Profile Switch" };

    private static object Upload(string eventType, int minutesAgo = 30) => eventType switch
    {
        "Temporary Override" => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "Loop",
            reason = "Running",
            duration = 60,
            insulinNeedsScaleFactor = 0.8,
            targetTop = 140,
            targetBottom = 120,
        },
        "Temporary Target" => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "AndroidAPS",
            reason = "Activity",
            duration = 45,
            targetTop = 8.0,
            targetBottom = 8.0,
            units = "mmol",
        },
        _ => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "AndroidAPS",
            profile = "Weekend",
            duration = 0,
        },
    };

    private async Task PostAsync(HttpClient client, object treatment)
    {
        var response = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonArray> GetArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static async Task<JsonArray> GetV3ResultAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsArray();
    }

    private static async Task<long> CountAsync(HttpClient client, string query = "")
    {
        var rows = await GetArrayAsync(client, $"/api/v1/count/treatments/where{query}");
        return rows.Count == 0 ? 0 : rows[0]!["count"]!.GetValue<long>();
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_is_served_by_the_v1_list_find_and_count(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));

        var all = await GetArrayAsync(client, "/api/v1/treatments");
        all.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);

        var found = await GetArrayAsync(
            client, $"/api/v1/treatments?find[eventType]={Uri.EscapeDataString(eventType)}");
        found.Should().ContainSingle();

        (await CountAsync(client)).Should().Be(1);
        (await CountAsync(client, $"?find[eventType]={Uri.EscapeDataString(eventType)}")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_is_served_by_v3_search_and_history(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));

        var search = await GetV3ResultAsync(client, "/api/v3/treatments");
        search.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);

        var history = await GetV3ResultAsync(client, "/api/v3/treatments/history/0");
        history.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task Deleting_by_the_served_id_removes_the_state_span(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));
        var id = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        (await client.GetAsync($"/api/v1/treatments/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);
    }

    [Fact]
    public async Task A_window_delete_removes_the_state_span_treatments_in_the_window()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Override", minutesAgo: 40));
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 30));
        await PostAsync(client, Upload("Profile Switch", minutesAgo: 20));
        await PostAsync(client, new { eventType = "Note", created_at = MinutesAgo(10), notes = "in the window" });
        await PostAsync(client, new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(180),
            reason = "Before the window",
            duration = 30,
        });

        var delete = await client.DeleteAsync(
            $"/api/v1/treatments?find[created_at][$gte]={MinutesAgo(60)}&find[created_at][$lte]={MinutesAgo(0)}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK, await delete.Content.ReadAsStringAsync());
        var status = (await delete.Content.ReadFromJsonAsync<JsonNode>())!;
        status["deletedCount"]!.GetValue<long>().Should().Be(4);
        status["n"]!.GetValue<long>().Should().Be(4);

        var remaining = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!;
        remaining["reason"]!.GetValue<string>().Should().Be("Before the window");
        (await CountAsync(client)).Should().Be(1);
        (await CountAsync(client, "?find[eventType]=Temporary%20Target")).Should().Be(0);
        (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should()
            .ContainSingle().Which!["reason"]!.GetValue<string>().Should().Be("Before the window");
    }

    [Fact]
    public async Task Updating_by_the_served_id_rewrites_the_state_span_in_place()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Override"));
        var id = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        var put = await client.PutAsJsonAsync($"/api/v1/treatments/{id}", new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(30),
            enteredBy = "Loop",
            reason = "Walking",
            duration = 90,
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!;
        served["_id"]!.GetValue<string>().Should().Be(id);
        served["reason"]!.GetValue<string>().Should().Be("Walking");
        served["duration"]!.GetValue<double>().Should().Be(90);
    }

    [Fact]
    public async Task Loop_deletes_an_override_by_the_uuid_it_uploaded_it_under()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        await PostAsync(client, new
        {
            _id = syncIdentifier,
            eventType = "Temporary Override",
            created_at = MinutesAgo(10),
            enteredBy = "Loop",
            reason = "Pre-Meal",
            durationType = "indefinite",
        });

        var delete = await client.DeleteAsync($"/api/v1/treatments/{syncIdentifier}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task The_create_response_carries_the_id_reads_serve(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var response = await client.PostAsJsonAsync("/api/v1/treatments", new[] { Upload(eventType) });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var createdId = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray()
            .Single()!["_id"]!.GetValue<string>();

        (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle()
            .Which!["_id"]!.GetValue<string>().Should().Be(createdId);
    }

    public static TheoryData<string, bool, bool> DeletedThenReuploaded()
    {
        var data = new TheoryData<string, bool, bool>();
        foreach (var eventType in new[] { "Temporary Override", "Temporary Target", "Profile Switch" })
        foreach (var byServedId in new[] { true, false })
        foreach (var viaV3 in new[] { true, false })
            data.Add(eventType, byServedId, viaV3);
        return data;
    }

    [Theory]
    [MemberData(nameof(DeletedThenReuploaded))]
    public async Task A_deleted_state_span_treatment_is_not_brought_back_by_a_reupload(
        string eventType, bool byServedId, bool viaV3)
    {
        var client = CreateAuthenticatedClient();
        var uploadedId = Guid.NewGuid().ToString().ToUpperInvariant();
        var upload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Upload(eventType)))!.AsObject();
        upload["_id"] = uploadedId;
        await PostAsync(client, upload);
        var servedId = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        var delete = await client.DeleteAsync($"/api/v1/treatments/{(byServedId ? servedId : uploadedId)}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        if (viaV3)
        {
            var reupload = upload.DeepClone().AsObject();
            reupload["identifier"] = uploadedId;
            var post = await client.PostAsJsonAsync("/api/v3/treatments", reupload);
            post.IsSuccessStatusCode.Should().BeTrue(await post.Content.ReadAsStringAsync());
        }
        else
        {
            var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { upload });
            post.StatusCode.Should().Be(HttpStatusCode.OK, await post.Content.ReadAsStringAsync());
            JsonNode.Parse(await post.Content.ReadAsStringAsync())!.AsArray().Should().HaveCount(1);
        }

        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);

        var put = await client.PutAsJsonAsync("/api/v1/treatments", upload);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        JsonNode.Parse(await put.Content.ReadAsStringAsync())!.AsArray().Should().BeEmpty();
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_an_override_with_notes_by_its_uploaded_id_removes_the_span_and_the_note()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        await PostAsync(client, new
        {
            _id = syncIdentifier,
            eventType = "Temporary Override",
            created_at = MinutesAgo(10),
            enteredBy = "Trio",
            reason = "Exercise",
            notes = "Run",
            duration = 60,
        });
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle()
            .Which!["notes"]!.GetValue<string>().Should().Be("Run");

        var delete = await client.DeleteAsync($"/api/v1/treatments/{syncIdentifier}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);
    }

    /// <summary>
    /// Nightscout stores one document per treatment, so a span treatment with <c>notes</c> is served
    /// once, carrying them. AAPS reads <c>notes</c> on temporary targets and profile switches, and
    /// LoopFollow shows a Trio override's name from them.
    /// </summary>
    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_with_notes_is_served_once_carrying_them(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var upload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Upload(eventType)))!.AsObject();
        upload["notes"] = "Synthetic note";
        await PostAsync(client, upload);

        (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle()
            .Which!["notes"]!.GetValue<string>().Should().Be("Synthetic note");
        (await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Note")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(1);
        (await CountAsync(client, $"?find[eventType]={Uri.EscapeDataString(eventType)}")).Should().Be(1);
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle()
            .Which!["notes"]!.GetValue<string>().Should().Be("Synthetic note");
        (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should().ContainSingle()
            .Which!["notes"]!.GetValue<string>().Should().Be("Synthetic note");
    }

    public static TheoryData<string, string> OlderSpanNoteDeletes()
    {
        var data = new TheoryData<string, string>();
        foreach (var eventType in new[] { "Temporary Override", "Temporary Target", "Profile Switch" })
        foreach (var by in new[] { "served", "uploaded", "note" })
            data.Add(eventType, by);
        return data;
    }

    /// <summary>
    /// A span written before notes were kept on it has them in a Note under the same treatment id. It
    /// is served as one treatment carrying them, and a delete by any of its ids removes both.
    /// </summary>
    [Theory]
    [MemberData(nameof(OlderSpanNoteDeletes))]
    public async Task An_older_span_and_the_note_written_beside_it_are_served_and_deleted_as_one(
        string eventType, string deleteBy)
    {
        var client = CreateAuthenticatedClient();
        var uploadedId = Guid.NewGuid().ToString().ToUpperInvariant();
        var upload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Upload(eventType)))!.AsObject();
        upload["_id"] = uploadedId;
        await PostAsync(client, upload);
        var noteId = await WriteNoteBesideAsync(uploadedId, eventType, upload["created_at"]!.GetValue<string>());

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!;
        var servedId = served["_id"]!.GetValue<string>();
        servedId.Should().NotBe(noteId.ToString());
        served["eventType"]!.GetValue<string>().Should().Be(eventType);
        served["notes"]!.GetValue<string>().Should().Be("Written beside");
        (await CountAsync(client)).Should().Be(1);
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle();
        (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should().ContainSingle()
            .Which!["notes"]!.GetValue<string>().Should().Be("Written beside");
        foreach (var id in new[] { servedId, uploadedId, noteId.ToString() })
        {
            var get = await client.GetAsync($"/api/v1/treatments/{id}");
            get.StatusCode.Should().Be(HttpStatusCode.OK, id);
            (await get.Content.ReadFromJsonAsync<JsonNode>())!["_id"]!.GetValue<string>().Should().Be(servedId);
        }

        var deleteId = deleteBy switch { "served" => servedId, "uploaded" => uploadedId, _ => noteId.ToString() };
        var delete = await client.DeleteAsync($"/api/v1/treatments/{deleteId}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);
        (await WithScopeAsync(async sp => await sp.GetRequiredService<INoteRepository>()
            .GetByIdAsync(noteId))).Should().BeNull();
    }

    [Fact]
    public async Task A_window_delete_counts_a_span_and_the_note_written_beside_it_once()
    {
        var client = CreateAuthenticatedClient();
        var uploadedId = Guid.NewGuid().ToString().ToUpperInvariant();
        var upload = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Upload("Temporary Override", 20)))!.AsObject();
        upload["_id"] = uploadedId;
        await PostAsync(client, upload);
        await WriteNoteBesideAsync(uploadedId, "Temporary Override", upload["created_at"]!.GetValue<string>());
        var withNotes = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Upload("Temporary Target", 10)))!.AsObject();
        withNotes["notes"] = "Kept on the span";
        await PostAsync(client, withNotes);

        var delete = await client.DeleteAsync(
            $"/api/v1/treatments?find[created_at][$gte]={MinutesAgo(60)}&find[created_at][$lte]={MinutesAgo(0)}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK, await delete.Content.ReadAsStringAsync());
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(2);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);
    }

    /// <summary>The Note an older version of the treatment decomposer wrote beside a span.</summary>
    private Task<Guid> WriteNoteBesideAsync(string treatmentId, string eventType, string createdAt) =>
        WithScopeAsync(async sp => (await sp.GetRequiredService<INoteRepository>().CreateAsync(
            new Note
            {
                LegacyId = treatmentId,
                EventType = eventType,
                Text = "Written beside",
                Timestamp = DateTime.Parse(createdAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            },
            WriteOrigin.Live)).Id);

    /// <summary>
    /// NightscoutKit's <c>OverrideTreatment</c> uploads and reads <c>correctionRange</c> and
    /// <c>remoteAddress</c>, and LoopFollow draws an override at its <c>correctionRange</c>, falling
    /// back to <c>[targetBottom, targetTop]</c>, which Loop never uploads, so without it every Loop
    /// override is drawn at [0, 0].
    /// </summary>
    [Fact]
    public async Task A_loop_override_is_served_its_correction_range_and_remote_address_as_uploaded()
    {
        var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var timestamp = LoopTimestamp(10);
        await PostAsync(client, new
        {
            _id = id, eventType = "Temporary Override", created_at = timestamp, timestamp,
            enteredBy = "Loop", reason = "Running", duration = 60.0, insulinNeedsScaleFactor = 0.8,
            correctionRange = new[] { 140, 160 }, remoteAddress = "synthetic-remote-address",
            syncIdentifier = id,
        });

        var v1 = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!.AsObject();
        var v3 = (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle().Which!.AsObject();
        var byIdentifier = await client.GetAsync($"/api/v3/treatments/{v3["identifier"]!.GetValue<string>()}");
        byIdentifier.StatusCode.Should().Be(HttpStatusCode.OK, await byIdentifier.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await byIdentifier.Content.ReadAsStringAsync())!;
        var v3Get = (body["result"] ?? body).AsObject();
        var served = new[]
        {
            v1,
            v3,
            (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should().ContainSingle().Which!.AsObject(),
            v3Get,
        };

        foreach (var treatment in served)
        {
            treatment["correctionRange"]!.ToJsonString().Should().Be("[140,160]");
            treatment["remoteAddress"]!.GetValue<string>().Should().Be("synthetic-remote-address");
            treatment["syncIdentifier"]!.GetValue<string>().Should().Be(id);
            NightscoutKitParsesOverride(treatment).Should().BeTrue(treatment.ToJsonString());
        }
    }

    [Fact]
    public async Task Saving_by_the_served_id_in_the_body_rewrites_the_state_span_in_place()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Target"));
        var id = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new
        {
            _id = id,
            eventType = "Temporary Target",
            created_at = MinutesAgo(30),
            reason = "Eating Soon",
            duration = 20,
            targetTop = 5.0,
            targetBottom = 5.0,
            units = "mmol",
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        JsonNode.Parse(await put.Content.ReadAsStringAsync())!["_id"]!.GetValue<string>().Should().Be(id);
        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!;
        served["_id"]!.GetValue<string>().Should().Be(id);
        served["reason"]!.GetValue<string>().Should().Be("Eating Soon");
        served["duration"]!.GetValue<double>().Should().Be(20);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_temp_target_resolves_by_the_object_id_its_uploaded_uuid_is_served_as(bool upperCase)
    {
        var client = CreateAuthenticatedClient();
        var uuid = Guid.NewGuid();
        var uploadedId = upperCase ? uuid.ToString().ToUpperInvariant() : uuid.ToString();
        await PostAsync(client, new
        {
            _id = uploadedId,
            eventType = "Temporary Target",
            created_at = MinutesAgo(10),
            duration = 30,
            targetTop = 7.0,
            targetBottom = 7.0,
        });
        var objectId = uuid.ToString("N")[..24];

        (await client.GetAsync($"/api/v1/treatments/{objectId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var delete = await client.DeleteAsync($"/api/v1/treatments/{objectId}");

        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_is_served_with_the_fields_it_was_uploaded_with()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Override"));

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!;

        served["duration"]!.GetValue<double>().Should().Be(60);
        served["reason"]!.GetValue<string>().Should().Be("Running");
        served["insulinNeedsScaleFactor"]!.GetValue<double>().Should().BeApproximately(0.8, 0.0001);
        served["targetTop"]!.GetValue<double>().Should().Be(140);
        served["targetBottom"]!.GetValue<double>().Should().Be(120);
        served["enteredBy"]!.GetValue<string>().Should().Be("Loop");
    }

    /// <summary>
    /// Loop uploads an indefinite override with <c>durationType</c> and no <c>duration</c>, and
    /// NightscoutKit reads any numeric duration as a finite override, so none is served even once a
    /// later override has ended it.
    /// </summary>
    [Fact]
    public async Task An_indefinite_override_is_served_without_a_duration_even_once_superseded()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(50),
            enteredBy = "Loop",
            reason = "Indefinite",
            durationType = "indefinite",
        });
        await PostAsync(client, new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(20),
            enteredBy = "Loop",
            reason = "Next",
            duration = 60,
        });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Override");

        var indefinite = served.Single(t => t!["reason"]!.GetValue<string>() == "Indefinite")!.AsObject();
        indefinite.ContainsKey("duration").Should().BeFalse();
        indefinite["durationType"]!.GetValue<string>().Should().Be("indefinite");
        served.Single(t => t!["reason"]!.GetValue<string>() == "Next")!["duration"]!
            .GetValue<double>().Should().BeApproximately(60, 0.001);
    }

    /// <summary>Loop ends an indefinite override by uploading it again under the same id with its duration.</summary>
    [Fact]
    public async Task An_indefinite_override_reuploaded_with_a_duration_is_served_with_that_duration()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        await PostAsync(client, new
        {
            _id = syncIdentifier,
            eventType = "Temporary Override",
            created_at = MinutesAgo(50),
            enteredBy = "Loop",
            reason = "Pre-Meal",
            durationType = "indefinite",
        });
        await PostAsync(client, new
        {
            _id = syncIdentifier,
            eventType = "Temporary Override",
            created_at = MinutesAgo(50),
            enteredBy = "Loop",
            reason = "Pre-Meal",
            duration = 25,
        });

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!.AsObject();

        served["duration"]!.GetValue<double>().Should().BeApproximately(25, 0.001);
        served.ContainsKey("durationType").Should().BeFalse();
    }

    [Fact]
    public async Task A_temp_target_cancel_is_served_under_its_own_event_type()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 30));
        await PostAsync(client, new
        {
            eventType = "Temporary Target Cancel",
            created_at = MinutesAgo(10),
            enteredBy = "Nightscout",
            duration = 0,
        });

        var target = (await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Target"))
            .Should().ContainSingle().Which!;
        target["duration"]!.GetValue<double>().Should().BeApproximately(45, 0.001);
        target["units"]!.GetValue<string>().Should().Be("mmol");

        var cancel = (await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Target%20Cancel"))
            .Should().ContainSingle().Which!;
        cancel["duration"]!.GetValue<double>().Should().Be(0);
        (await CountAsync(client, "?find[eventType]=Temporary%20Target%20Cancel")).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_by_the_cancel_event_type_removes_only_the_cancel()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 30));
        await PostAsync(client, new { eventType = "Temporary Target Cancel", created_at = MinutesAgo(10), duration = 0 });

        var delete = await client.DeleteAsync("/api/v1/treatments?find[eventType]=Temporary%20Target%20Cancel");

        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle()
            .Which!["eventType"]!.GetValue<string>().Should().Be("Temporary Target");
    }

    /// <summary>AAPS and the careportal cancel with a zero-duration "Temporary Target".</summary>
    [Fact]
    public async Task A_zero_duration_temp_target_is_served_as_a_cancel_of_that_event_type()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 30));
        await PostAsync(client, new
        {
            eventType = "Temporary Target",
            created_at = MinutesAgo(10),
            enteredBy = "AndroidAPS",
            duration = 0,
        });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Target");

        served.Select(t => (int)Math.Round(t!["duration"]!.GetValue<double>()))
            .Should().BeEquivalentTo(new[] { 45, 0 });
    }

    [Fact]
    public async Task A_profile_switch_is_served_with_its_profile_name()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Profile Switch"));

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!;

        served["profile"]!.GetValue<string>().Should().Be("Weekend");
        served["duration"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task A_superseded_permanent_profile_switch_keeps_duration_zero()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, new { eventType = "Profile Switch", created_at = MinutesAgo(50), profile = "Weekday", duration = 0 });
        await PostAsync(client, new { eventType = "Profile Switch", created_at = MinutesAgo(20), profile = "Weekend", duration = 0 });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Profile%20Switch");

        served.Should().HaveCount(2);
        served.Select(t => t!["duration"]!.GetValue<double>()).Should().AllSatisfy(d => d.Should().Be(0));
    }

    [Fact]
    public async Task Connector_profile_spans_are_not_served_or_deleted_as_treatments()
    {
        var client = CreateAuthenticatedClient();
        var start = DateTime.UtcNow.AddMinutes(-40);
        string[] connectorIds =
        [
            $"glooko_active_profile_{Guid.NewGuid()}_{start.Ticks}",
            $"mylife_active_profile_pump_{start.Ticks}",
        ];
        await WithScopeAsync(async sp =>
        {
            var spans = sp.GetRequiredService<IStateSpanService>();
            await spans.UpsertStateSpanAsync(ConnectorProfile(connectorIds[0], DataSources.GlookoConnector, start));
            await spans.UpsertStateSpanAsync(ConnectorProfile(connectorIds[1], DataSources.MyLifeConnector, start.AddMinutes(5)));
            return 0;
        });
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 10));
        await PostAsync(client, Upload("Profile Switch", minutesAgo: 5));

        var list = await GetArrayAsync(client, "/api/v1/treatments");
        list.Select(t => t!["eventType"]!.GetValue<string>()).Should()
            .BeEquivalentTo(new[] { "Temporary Target", "Profile Switch" });
        list.Single(t => t!["eventType"]!.GetValue<string>() == "Profile Switch")!["profile"]!
            .GetValue<string>().Should().Be("Weekend");
        (await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Profile%20Switch")).Should().ContainSingle();
        (await CountAsync(client)).Should().Be(2);
        (await CountAsync(client, "?find[eventType]=Profile%20Switch")).Should().Be(1);
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().HaveCount(2);
        (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should().HaveCount(2);

        foreach (var id in connectorIds)
        {
            (await client.GetAsync($"/api/v1/treatments/{id}")).StatusCode.Should().NotBe(HttpStatusCode.OK);
            var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");
            (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(0);
        }

        var stored = await WithScopeAsync(async sp => (await sp.GetRequiredService<IStateSpanService>()
            .GetStateSpansAsync(category: StateSpanCategory.Profile, count: 100)).ToList());
        stored.Select(s => s.OriginalId).Should().Contain(connectorIds);
    }

    private static StateSpan ConnectorProfile(string originalId, string source, DateTime start) => new()
    {
        OriginalId = originalId,
        Category = StateSpanCategory.Profile,
        State = ProfileState.Active.ToString(),
        StartTimestamp = start,
        Source = source,
        Metadata = new Dictionary<string, object> { ["profileName"] = "Unknown" },
    };

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        return await action(scope.ServiceProvider);
    }

    /// <summary>Loop's upload format: NightscoutKit's <c>TimeFormat.timestampStrFromDate</c>.</summary>
    private static string LoopTimestamp(int minutesAgo) =>
        DateTime.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>
    /// NightscoutKit 4ec9fd1 <c>TimeFormat.dateFromTimestamp</c>: ISO 8601 internet date time, with or
    /// without fractional seconds.
    /// </summary>
    private static readonly Regex NightscoutKitDate =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$");

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out _);

    /// <summary>
    /// <c>NightscoutTreatment.init?(_:)</c> at NightscoutKit 4ec9fd1: nil unless <c>_id</c>,
    /// <c>eventType</c>, a parseable <c>timestamp</c> and <c>enteredBy</c> are all strings.
    /// </summary>
    private static bool NightscoutKitParsesTreatment(JsonObject t) =>
        IsString(t["_id"])
        && IsString(t["eventType"])
        && IsString(t["timestamp"]) && NightscoutKitDate.IsMatch(t["timestamp"]!.GetValue<string>())
        && IsString(t["enteredBy"]);

    /// <summary>
    /// <c>OverrideTreatment.init?(_:)</c> at NightscoutKit 4ec9fd1: a string <c>reason</c>, then a
    /// numeric <c>duration</c> or <c>durationType: "indefinite"</c>, then the base treatment.
    /// </summary>
    private static bool NightscoutKitParsesOverride(JsonObject t) =>
        t["eventType"]?.GetValue<string>() == "Temporary Override"
        && IsString(t["reason"])
        && ((t["duration"] is JsonValue d && d.TryGetValue<double>(out _))
            || t["durationType"]?.GetValue<string>() == "indefinite")
        && NightscoutKitParsesTreatment(t);

    public static TheoryData<string> LoopUploads =>
        new() { "Temporary Override", "Indefinite Override", "Temporary Target", "Profile Switch" };

    private static object LoopUpload(string kind, string id, string timestamp) => kind switch
    {
        "Temporary Override" => new
        {
            _id = id, eventType = "Temporary Override", created_at = timestamp, timestamp,
            enteredBy = "Loop", reason = "Running", duration = 60.0, insulinNeedsScaleFactor = 0.8,
        },
        "Indefinite Override" => new
        {
            _id = id, eventType = "Temporary Override", created_at = timestamp, timestamp,
            enteredBy = "Loop", reason = "Pre-Meal", durationType = "indefinite",
        },
        "Temporary Target" => new
        {
            _id = id, eventType = "Temporary Target", created_at = timestamp, timestamp,
            enteredBy = "Nightscout", reason = "Activity", duration = 45, targetTop = 140, targetBottom = 140,
        },
        _ => new
        {
            _id = id, eventType = "Profile Switch", created_at = timestamp, timestamp,
            enteredBy = "Nightscout", reason = "Weekend", profile = "Weekend", duration = 30,
        },
    };

    /// <summary>
    /// NightscoutKit's <c>fetchTreatments</c>, behind LoopCaregiver, drops a treatment missing any of
    /// <c>_id</c>, <c>eventType</c>, <c>timestamp</c> or <c>enteredBy</c>. Nightscout serves the
    /// uploaded <c>timestamp</c> as stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoopUploads))]
    public async Task A_state_span_treatment_is_served_with_the_keys_NightscoutKit_requires(string kind)
    {
        var client = CreateAuthenticatedClient();
        var timestamp = LoopTimestamp(20);
        await PostAsync(client, LoopUpload(kind, Guid.NewGuid().ToString().ToUpperInvariant(), timestamp));

        var v1 = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!.AsObject();
        var v3 = (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle().Which!.AsObject();

        foreach (var served in new[] { v1, v3 })
        {
            served["timestamp"]!.GetValue<string>().Should().Be(timestamp);
            served["reason"]!.GetValue<string>().Should().NotBeNullOrEmpty();
            NightscoutKitParsesTreatment(served).Should().BeTrue(served.ToJsonString());
            if (kind == "Indefinite Override")
            {
                served.ContainsKey("duration").Should().BeFalse();
                served["durationType"]!.GetValue<string>().Should().Be("indefinite");
            }
            else
            {
                served["duration"]!.GetValue<double>().Should().BeGreaterThan(0);
            }

            if (kind.EndsWith("Override", StringComparison.Ordinal))
                NightscoutKitParsesOverride(served).Should().BeTrue(served.ToJsonString());
        }
    }

    /// <summary>
    /// Overrides written before the uploaded timestamp was kept have none, so they are served their
    /// start in a form NightscoutKit parses rather than dropped.
    /// </summary>
    [Fact]
    public async Task An_override_uploaded_without_a_timestamp_is_served_one_NightscoutKit_parses()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = MinutesAgo(20);
        await PostAsync(client, new
        {
            _id = Guid.NewGuid().ToString().ToUpperInvariant(),
            eventType = "Temporary Override",
            created_at = createdAt,
            enteredBy = "Loop",
            reason = "Running",
            duration = 60,
        });

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!.AsObject();

        NightscoutKitParsesOverride(served).Should().BeTrue(served.ToJsonString());
        DateTimeOffset.Parse(served["timestamp"]!.GetValue<string>())
            .Should().Be(DateTimeOffset.Parse(createdAt));
    }

    /// <summary>
    /// AAPS uploads temporary targets and profile switches without a <c>timestamp</c>, and its v3
    /// client reads that field as a number, so none is invented for them.
    /// </summary>
    [Theory]
    [InlineData("Temporary Target")]
    [InlineData("Profile Switch")]
    public async Task An_aaps_upload_without_a_timestamp_is_served_without_one(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));

        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle()
            .Which!.AsObject().ContainsKey("timestamp").Should().BeFalse();
    }

    [Theory]
    [InlineData("Correction Bolus")]
    [InlineData("Carb Correction")]
    [InlineData("Note")]
    public async Task A_record_treatment_is_served_with_the_timestamp_it_was_uploaded_with(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var timestamp = LoopTimestamp(15);
        await PostAsync(client, new
        {
            eventType,
            created_at = timestamp,
            timestamp,
            enteredBy = "Loop",
            insulin = eventType == "Correction Bolus" ? 1.5 : (double?)null,
            carbs = eventType == "Carb Correction" ? 12 : (double?)null,
            notes = eventType == "Note" ? "synthetic" : null,
        });

        (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle()
            .Which!["timestamp"]!.GetValue<string>().Should().Be(timestamp);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_is_served_by_v3_get_by_identifier(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));
        var identifier = (await GetV3ResultAsync(client, "/api/v3/treatments")).Single()!["identifier"]!
            .GetValue<string>();

        var response = await client.GetAsync($"/api/v3/treatments/{identifier}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var served = (body["result"] ?? body)!;
        served["eventType"]!.GetValue<string>().Should().Be(eventType);
        served["identifier"]!.GetValue<string>().Should().Be(identifier);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_v3_patch_rewrites_the_state_span_in_place(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));
        var identifier = (await GetV3ResultAsync(client, "/api/v3/treatments")).Single()!["identifier"]!
            .GetValue<string>();

        var patch = await client.PatchAsJsonAsync($"/api/v3/treatments/{identifier}", new { duration = 15 });

        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        var served = (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().ContainSingle().Which!;
        served["identifier"]!.GetValue<string>().Should().Be(identifier);
        served["eventType"]!.GetValue<string>().Should().Be(eventType);
        served["duration"]!.GetValue<double>().Should().BeApproximately(15, 0.001);
        (await CountAsync(client)).Should().Be(1);
    }

    /// <summary>
    /// A credential limited to the last 24 hours reads overrides, temporary targets and profile
    /// switches that ended before that window not at all, as it does every other treatment.
    /// </summary>
    [Fact]
    public async Task A_history_clamped_credential_reads_only_state_span_treatments_overlapping_the_last_24_hours()
    {
        var owner = CreateAuthenticatedClient();
        await PostAsync(owner, Upload("Temporary Override", minutesAgo: 3 * 24 * 60));
        await PostAsync(owner, Upload("Temporary Target", minutesAgo: 3 * 24 * 60));
        await PostAsync(owner, new
        {
            eventType = "Profile Switch", created_at = MinutesAgo(3 * 24 * 60), profile = "Weekend", duration = 60,
        });
        foreach (var eventType in new[] { "Temporary Override", "Temporary Target", "Profile Switch" })
            await PostAsync(owner, Upload(eventType, minutesAgo: 60));

        using var clamped = await CreateHistoryClampedClientAsync();

        (await GetArrayAsync(owner, "/api/v1/treatments?count=100")).Should().HaveCount(6);
        var v1 = await GetArrayAsync(clamped, "/api/v1/treatments?count=100");
        v1.Should().HaveCount(3);
        v1.Select(t => DateTimeOffset.FromUnixTimeMilliseconds(t!["mills"]!.GetValue<long>()))
            .Should().OnlyContain(at => at > DateTimeOffset.UtcNow.AddHours(-24));
        (await GetArrayAsync(clamped, "/api/v1/treatments?find[eventType]=Profile%20Switch")).Should().ContainSingle();
        (await GetV3ResultAsync(clamped, "/api/v3/treatments/history/0")).Should().HaveCount(3);
        (await GetV3ResultAsync(clamped, "/api/v3/treatments?limit=100")).Should().HaveCount(3);
    }

    private static object ProfileStore(double carbRatio, double basal) => new
    {
        dia = 5,
        carbratio = new[] { new { time = "00:00", value = carbRatio, timeAsSeconds = 0 } },
        sens = new[] { new { time = "00:00", value = 50, timeAsSeconds = 0 } },
        basal = new[] { new { time = "00:00", value = basal, timeAsSeconds = 0 } },
        target_low = new[] { new { time = "00:00", value = 100, timeAsSeconds = 0 } },
        target_high = new[] { new { time = "00:00", value = 120, timeAsSeconds = 0 } },
        units = "mg/dl",
        timezone = "UTC",
    };

    /// <summary>
    /// The therapy resolvers find the active profile from the profile switch span, so a clamped member
    /// must still see a switch that started three days ago and is still running, or its carb ratio and
    /// basal fall back to the default profile.
    /// </summary>
    [Fact]
    public async Task A_history_clamped_member_resolves_a_profile_switch_still_running_from_three_days_ago()
    {
        var owner = CreateAuthenticatedClient();
        var startDate = DateTimeOffset.UtcNow.AddDays(-5);
        var profile = await owner.PostAsJsonAsync("/api/v1/profile", new
        {
            defaultProfile = "Default",
            startDate = startDate.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            mills = startDate.ToUnixTimeMilliseconds(),
            store = new Dictionary<string, object>
            {
                ["Default"] = ProfileStore(carbRatio: 10, basal: 1.0),
                ["Weekend"] = ProfileStore(carbRatio: 7, basal: 1.6),
            },
        });
        profile.StatusCode.Should().Be(HttpStatusCode.OK, await profile.Content.ReadAsStringAsync());
        await PostAsync(owner, new
        {
            eventType = "Profile Switch", created_at = MinutesAgo(3 * 24 * 60), enteredBy = "Loop",
            profile = "Weekend", duration = 0,
        });
        await PostAsync(owner, new
        {
            eventType = "Temporary Override", created_at = MinutesAgo(2 * 24 * 60 + 60), enteredBy = "Loop",
            reason = "Ended two days ago", duration = 60,
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var resolved = await WithHistoryClampedScopeAsync(async sp =>
        {
            var spans = await sp.GetRequiredService<IStateSpanService>().GetStateSpansAsync(count: 100);
            var segments = new List<Nocturne.Core.Models.Basal.BasalSegment>();
            await foreach (var segment in sp.GetRequiredService<IBasalSegmentService>()
                .GetSegmentsAsync(now - 3_600_000, now))
                segments.Add(segment);
            return (
                Spans: spans.ToList(),
                Profile: await sp.GetRequiredService<IActiveProfileResolver>().GetActiveProfileNameAsync(now),
                CarbRatio: await sp.GetRequiredService<ICarbRatioResolver>().GetCarbRatioAsync(now),
                Basal: await sp.GetRequiredService<IBasalRateResolver>().GetBasalRateAsync(now),
                Segments: segments);
        });

        resolved.Spans.Should().ContainSingle(s => s.Category == StateSpanCategory.Profile);
        resolved.Spans.Should().NotContain(s => s.Category == StateSpanCategory.Override,
            "an override that ended two days ago is outside a clamped member's window");
        resolved.Profile.Should().Be("Weekend");
        resolved.CarbRatio.Should().BeApproximately(7, 0.001);
        resolved.Basal.Should().BeApproximately(1.6, 0.001);
        resolved.Segments.Should().NotBeEmpty().And.OnlyContain(s => s.ProfileName == "Weekend" && Math.Abs(s.UnitsPerHour - 1.6) < 0.001);

        using var clamped = await CreateHistoryClampedClientAsync();
        (await GetArrayAsync(clamped, "/api/v1/treatments?count=100")).Should().ContainSingle()
            .Which!["profile"]!.GetValue<string>().Should().Be("Weekend");
    }

    /// <summary>A scope clamped as <c>MemberScopeMiddleware</c> clamps a 24-hour-limited member's request.</summary>
    private async Task<T> WithHistoryClampedScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<ICategoryReadContext>().ClampMemberHistory();
        var db = scope.ServiceProvider.GetRequiredService<NocturneDbContext>();
        db.TenantId = Fixture.TenantId;
        db.HistoryClamped = true;
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_history_clamped_credential_reads_only_the_last_24_hours_of_notes()
    {
        var owner = CreateAuthenticatedClient();
        await PostAsync(owner, new { eventType = "Note", created_at = MinutesAgo(3 * 24 * 60), notes = "old" });
        await PostAsync(owner, new { eventType = "Note", created_at = MinutesAgo(60), notes = "recent" });
        await PostAsync(owner, Upload("Temporary Target", minutesAgo: 30));

        using var clamped = await CreateHistoryClampedClientAsync();

        (await GetArrayAsync(owner, "/api/v1/treatments?count=100")).Should().HaveCount(3);
        var v1 = await GetArrayAsync(clamped, "/api/v1/treatments?count=100");
        v1.Should().HaveCount(2);
        v1.Where(t => t!["eventType"]!.GetValue<string>() == "Note")
            .Should().ContainSingle().Which!["notes"]!.GetValue<string>().Should().Be("recent");
        (await GetArrayAsync(clamped, "/api/v1/treatments?find[eventType]=Note")).Should().ContainSingle();
        (await GetV3ResultAsync(clamped, "/api/v3/treatments/history/0")).Should().HaveCount(2);
    }

    /// <summary>
    /// Nightscout serves <c>timestamp</c> with the JSON type it was uploaded with: AAPS's v1 client
    /// uploads a temporary target's as epoch milliseconds, Loop a bolus's as an ISO string.
    /// </summary>
    [Theory]
    [InlineData("Temporary Target", true)]
    [InlineData("Temporary Target", false)]
    [InlineData("Correction Bolus", true)]
    [InlineData("Correction Bolus", false)]
    public async Task The_uploaded_timestamp_keeps_its_json_type(string eventType, bool numeric)
    {
        var client = CreateAuthenticatedClient();
        var at = DateTimeOffset.UtcNow.AddMinutes(-20);
        var millis = at.ToUnixTimeMilliseconds();
        JsonNode timestamp = numeric ? JsonValue.Create(millis) : JsonValue.Create(millis.ToString())!;
        var upload = new JsonObject
        {
            ["eventType"] = eventType,
            ["created_at"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["timestamp"] = timestamp,
            ["enteredBy"] = "AndroidAPS",
            ["duration"] = 30,
            ["targetTop"] = 100,
            ["targetBottom"] = 100,
            ["insulin"] = eventType == "Correction Bolus" ? 1.0 : null,
        };
        await PostAsync(client, upload);

        void ShouldKeepType(JsonNode served)
        {
            var value = served["timestamp"]!.AsValue();
            if (numeric)
                value.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.Number, served.ToJsonString());
            else
                value.GetValueKind().Should().Be(System.Text.Json.JsonValueKind.String, served.ToJsonString());
            value.ToJsonString().Trim('"').Should().Be(millis.ToString());
        }

        ShouldKeepType((await GetArrayAsync(client, "/api/v1/treatments")).Single()!);
        var v3 = (await GetV3ResultAsync(client, "/api/v3/treatments")).Single()!;
        ShouldKeepType(v3);

        var patch = await client.PatchAsJsonAsync(
            $"/api/v3/treatments/{v3["identifier"]!.GetValue<string>()}", new { reason = "patched" });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        ShouldKeepType((await GetArrayAsync(client, "/api/v1/treatments")).Single()!);
    }

    private async Task<HttpClient> CreateHistoryClampedClientAsync()
    {
        await using var conn = new NpgsqlConnection(await GetPostgresConnectionStringAsync());
        await conn.OpenAsync();
        var (subjectId, token) = await AuthTestHelpers.SeedAuthenticatedSubjectAsync(
            conn, Fixture.TenantId, "Clamped Follower");

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT set_config('app.current_tenant_id', @tenant, false);
                UPDATE oauth_grants SET limit_to_24_hours = true WHERE subject_id = @subject;
                """;
            cmd.Parameters.AddWithValue("tenant", Fixture.TenantId.ToString());
            cmd.Parameters.AddWithValue("subject", subjectId);
            await cmd.ExecuteNonQueryAsync();
        }

        return AuthTestHelpers.CreateAuthenticatedSubjectClient(Fixture, token);
    }

    [Fact]
    public async Task A_devicestatus_override_is_not_served_as_a_treatment()
    {
        var client = CreateAuthenticatedClient();
        var at = MinutesAgo(5);
        var response = await client.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            device = "loop://iPhone",
            created_at = at,
            @override = new { active = true, name = "Workout", timestamp = at, duration = 3600, multiplier = 1.2 },
        });
        response.EnsureSuccessStatusCode();
        await PostAsync(client, new { eventType = "Note", created_at = at, notes = "beside the status" });

        (await GetArrayAsync(client, "/api/v1/treatments")).Should()
            .ContainSingle().Which!["eventType"]!.GetValue<string>().Should().Be("Note");
        (await CountAsync(client)).Should().Be(1);
    }
}
