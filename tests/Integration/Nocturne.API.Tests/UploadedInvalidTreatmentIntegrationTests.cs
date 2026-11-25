using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Castle.DynamicProxy;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Services.Treatments;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.API.Tests.Services.V4;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Services;
using Xunit;
using Xunit.Abstractions;
using TreatmentModel = Nocturne.Core.Models.Treatment;
using V4Models = Nocturne.Core.Models.V4;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// A treatment uploaded with <c>isValid: false</c> is stored deleted, as Nightscout stores it: no read
/// or calculation serves it, v3 history serves it as deleted, and an upload of its id without
/// <c>isValid</c> brings it back. AndroidAPS sends one for a record it invalidated before uploading.
/// Only the client's own id deletes a live treatment: a synthetic id is derived from content, which
/// another record can share.
/// </summary>
[Collection(ApiIntegrationShards.Four)]
[Trait("Category", "Integration")]
public class UploadedInvalidTreatmentIntegrationTests : ApiIntegrationTestBase
{
    public UploadedInvalidTreatmentIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static readonly string[] Kinds =
    [
        "Correction Bolus", "Meal Bolus", "Temp Basal", "Profile Switch", "Temporary Target",
        "Temporary Override", "Site Change",
    ];

    public static TheoryData<string, bool> Uploads()
    {
        var data = new TheoryData<string, bool>();
        foreach (var eventType in Kinds)
        {
            data.Add(eventType, false);
            data.Add(eventType, true);
        }

        return data;
    }

    public static TheoryData<string, bool> RecordAndSpanUploads()
    {
        var data = new TheoryData<string, bool>();
        foreach (var eventType in new[] { "Correction Bolus", "Temporary Override" })
        {
            data.Add(eventType, false);
            data.Add(eventType, true);
        }

        return data;
    }

    private readonly DateTimeOffset _createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);

    private JsonObject Treatment(string eventType, string? id = null, bool? isValid = null, DateTimeOffset? at = null)
    {
        var createdAt = at ?? _createdAt;
        var treatment = new JsonObject
        {
            ["eventType"] = eventType,
            ["created_at"] = createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["date"] = createdAt.ToUnixTimeMilliseconds(),
            ["enteredBy"] = "AndroidAPS",
        };
        if (id is not null)
            treatment["_id"] = id;

        switch (eventType)
        {
            case "Correction Bolus":
                treatment["insulin"] = 2.0;
                break;
            case "Meal Bolus":
                treatment["insulin"] = 3.0;
                treatment["carbs"] = 30;
                break;
            case "Temp Basal":
                treatment["absolute"] = 0.8;
                treatment["duration"] = 30;
                break;
            case "Profile Switch":
                treatment["profile"] = "Default";
                treatment["duration"] = 60;
                break;
            case "Temporary Target":
                treatment["reason"] = "Activity";
                treatment["duration"] = 45;
                treatment["targetTop"] = 140;
                treatment["targetBottom"] = 140;
                treatment["units"] = "mg/dl";
                break;
            case "Temporary Override":
                treatment["duration"] = 60;
                treatment["reason"] = "Running";
                treatment["insulinNeedsScaleFactor"] = 0.8;
                break;
            case "Site Change":
                treatment["notes"] = "left arm";
                break;
        }

        if (isValid is { } valid)
            treatment["isValid"] = valid;
        return treatment;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..24];

    private static async Task<JsonNode> UploadAsync(HttpClient client, JsonObject treatment, bool v3)
    {
        var response = v3
            ? await client.PostAsJsonAsync("/api/v3/treatments", treatment)
            : await client.PostAsJsonAsync("/api/v1/treatments", new JsonArray(treatment.DeepClone()));
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonNode.Parse(body)!;
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>What every live read a client serves a treatment from, and the IOB and COB inputs, hold.</summary>
    private sealed record Live(
        int V1, int V3Range, int V3Modified, double Iob, int Boluses, int CarbIntakes, int TempBasals, double Cob)
    {
        public static Live None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
    }

    private async Task<Live> LiveAsync(HttpClient client)
    {
        await SettleHistoryAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var onBoard = await WithTenantScopeAsync(async sp =>
        {
            var reader = sp.GetRequiredService<IIobCobReader>();
            return (Inputs: await reader.ReadInputsAsync(now), Total: await reader.ReadTotalAsync(now, live: false));
        });
        return new Live(
            (await GetAsync(client, "/api/v1/treatments")).AsArray().Count,
            (await GetAsync(client, "/api/v3/treatments"))["result"]!.AsArray().Count,
            (await GetAsync(client, "/api/v3/treatments?srvModified$gt=0&sort=srvModified"))["result"]!.AsArray().Count,
            (await GetAsync(client, "/api/v1/iob/treatments"))["iob"]?.GetValue<double>() ?? 0,
            onBoard.Inputs.Boluses.Count,
            onBoard.Inputs.CarbIntakes.Count,
            onBoard.Inputs.TempBasals.Count,
            onBoard.Total.Cob?.Cob ?? 0);
    }

    private async Task<JsonArray> HistoryAsync(HttpClient client)
    {
        await SettleHistoryAsync();
        return (await GetAsync(client, "/api/v3/treatments/history/0"))["result"]!.AsArray();
    }

    private static bool IsDeleted(JsonNode? treatment) => treatment?["isValid"]?.GetValue<bool>() == false;

    private static async Task<string> ServedIdAsync(HttpClient client, bool v3) =>
        v3
            ? (await GetAsync(client, "/api/v3/treatments"))["result"]!.AsArray()
                .Should().ContainSingle().Subject!["identifier"]!.GetValue<string>()
            : (await GetAsync(client, "/api/v1/treatments")).AsArray()
                .Should().ContainSingle().Subject!["_id"]!.GetValue<string>();

    private static void ShouldServeLive(Live live, string eventType)
    {
        (live.V1, live.V3Range, live.V3Modified).Should().Be((1, 1, 1));
        if (eventType is "Correction Bolus" or "Meal Bolus")
        {
            live.Boluses.Should().Be(1);
            live.Iob.Should().BePositive();
        }
        if (eventType == "Meal Bolus")
        {
            live.CarbIntakes.Should().Be(1);
            live.Cob.Should().BePositive();
        }
        if (eventType == "Temp Basal")
            live.TempBasals.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task An_upload_with_isValid_false_is_stored_deleted(string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        await UploadAsync(client, Treatment(eventType, NewId(), isValid: false), v3);

        (await LiveAsync(client)).Should().Be(Live.None);
        var history = await HistoryAsync(client);
        history.Should().NotBeEmpty().And.OnlyContain(t => IsDeleted(t));
        history.Should().Contain(t => t!["eventType"]!.GetValue<string>() == eventType);
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_resend_without_isValid_brings_it_back(string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment(eventType, id, isValid: false), v3);

        await UploadAsync(client, Treatment(eventType, id), v3);

        ShouldServeLive(await LiveAsync(client), eventType);
    }

    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task A_resend_with_isValid_false_stores_nothing_more(string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment(eventType, id, isValid: false), v3);
        var tombstones = (await HistoryAsync(client)).Count;

        await UploadAsync(client, Treatment(eventType, id, isValid: false), v3);

        (await LiveAsync(client)).Should().Be(Live.None);
        (await HistoryAsync(client)).Should().HaveCount(tombstones).And.OnlyContain(t => IsDeleted(t));
    }

    /// <remarks>
    /// A v1 upload names the treatment by the <c>_id</c> it was stored under. A v3 create, stored with
    /// no <c>_id</c> under the key derived from its content, is named by the identifier reads serve,
    /// as Nightscout's create of a stored identifier replaces it.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Uploads))]
    public async Task An_upload_with_isValid_false_deletes_the_live_treatment_its_id_names(string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment(eventType, v3 ? null : id), v3);
        var name = v3 ? await ServedIdAsync(client, v3: true) : id;

        await UploadAsync(client, Treatment(eventType, name, isValid: false), v3);

        (await LiveAsync(client)).Should().Be(Live.None);
        (await HistoryAsync(client)).Should().Contain(t => IsDeleted(t));

        await UploadAsync(client, Treatment(eventType, name), v3);

        ShouldServeLive(await LiveAsync(client), eventType);
    }

    /// <summary>
    /// Two records with the same event type, time, values and uploader share a synthetic id. AAPS sends
    /// <c>isValid: false</c> on a create only for a record it never uploaded, so an invalid upload under
    /// a synthetic id a live treatment holds is that other record, and the live dose stays counted.
    /// </summary>
    [Theory]
    [MemberData(nameof(RecordAndSpanUploads))]
    public async Task An_upload_with_isValid_false_under_another_identity_leaves_the_live_treatment(string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        await UploadAsync(client, Treatment(eventType), v3);
        var before = await LiveAsync(client);
        (before.V1, before.V3Range, before.V3Modified).Should().Be((1, 1, 1));

        var invalid = Treatment(eventType, isValid: false);
        if (v3)
            invalid["identifier"] = NewId();
        await UploadAsync(client, invalid, v3);

        (await LiveAsync(client)).Should().BeEquivalentTo(before, options => options
            .Using<double>(on => on.Subject.Should().BeApproximately(on.Expectation, 0.05))
            .WhenTypeIs<double>());
    }

    /// <summary>
    /// A v1 document without an identity replaces the treatment at its time with its event type, as
    /// Nightscout's upsert does. One sent with <c>isValid: false</c> does not: it may be another dose.
    /// </summary>
    [Fact]
    public async Task A_v1_upload_with_isValid_false_without_an_identity_replaces_no_treatment_at_its_time()
    {
        var client = CreateAuthenticatedClient();
        await UploadAsync(client, Treatment("Correction Bolus"), v3: false);
        var before = await LiveAsync(client);

        var other = Treatment("Correction Bolus", isValid: false);
        other["insulin"] = 1.0;
        await UploadAsync(client, other, v3: false);

        var live = await LiveAsync(client);
        (live.V1, live.V3Range, live.V3Modified, live.Boluses).Should().Be((1, 1, 1, 1));
        live.Iob.Should().BeApproximately(before.Iob, 0.05);
        (await HistoryAsync(client)).Should().Contain(t => IsDeleted(t) && t!["insulin"]!.GetValue<double>() == 1.0);
    }

    /// <summary>
    /// A pump connector reported the same dose an AAPS upload did, and deduplication linked the two.
    /// AAPS invalidating its record takes only its own copy: the dose stays counted from the other source.
    /// </summary>
    [Fact]
    public async Task An_upload_with_isValid_false_leaves_another_sources_copy_of_the_dose()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadWithPumpCopyAsync(client, id);

        await UploadAsync(client, Treatment("Correction Bolus", id, isValid: false), v3: false);

        var live = await LiveAsync(client);
        (live.V1, live.V3Range, live.Boluses).Should().Be((1, 1, 1));
        live.Iob.Should().BePositive();
    }

    /// <summary>
    /// After AAPS invalidated its copy of a dose the pump connector also reported, AAPS sends the dose
    /// again. Its copy joins the group the pump connector's copy kept, so the dose is counted once.
    /// </summary>
    [Fact]
    public async Task A_resend_after_another_sources_copy_was_kept_counts_the_dose_once()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadWithPumpCopyAsync(client, id);
        await UploadAsync(client, Treatment("Correction Bolus", id, isValid: false), v3: false);
        var kept = await LiveAsync(client);

        await UploadAsync(client, Treatment("Correction Bolus", id), v3: false);

        var live = await LiveAsync(client);
        (live.V1, live.V3Range, live.V3Modified, live.Boluses).Should().Be((1, 1, 1, 1));
        live.Iob.Should().BeApproximately(kept.Iob, 0.05);
    }

    /// <summary>Uploads an AAPS bolus under <paramref name="id"/> that a pump connector's copy is linked to.</summary>
    private async Task UploadWithPumpCopyAsync(HttpClient client, string id)
    {
        await UploadAsync(client, Treatment("Correction Bolus", id), v3: false);
        await WithTenantScopeAsync(sp => sp.GetRequiredService<IBolusRepository>().CreateAsync(new V4Models.Bolus
        {
            Timestamp = _createdAt.UtcDateTime.AddSeconds(5),
            LegacyId = "pump_bolus_" + id,
            Device = "pump-connector",
            DataSource = "pump-connector",
            Insulin = 2.0,
            BolusType = V4Models.BolusType.Normal,
        }, WriteOrigin.Live));
        (await LiveAsync(client)).V1.Should().Be(1, "deduplication serves one copy of the dose");
    }

    [Theory]
    [MemberData(nameof(RecordAndSpanUploads))]
    public async Task A_treatment_the_user_deleted_stays_deleted_through_an_invalid_and_a_valid_resend(
        string eventType, bool v3)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment(eventType, id), v3);
        var served = await ServedIdAsync(client, v3: false);
        var delete = await client.DeleteAsync($"/api/v1/treatments/{served}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK, await delete.Content.ReadAsStringAsync());

        await UploadAsync(client, Treatment(eventType, id, isValid: false), v3);
        await UploadAsync(client, Treatment(eventType, id), v3);

        (await LiveAsync(client)).Should().Be(Live.None);
    }

    [Theory]
    [InlineData("Correction Bolus")]
    [InlineData("Temporary Override")]
    public async Task A_v1_put_with_isValid_false_deletes_the_treatment(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await UploadAsync(client, Treatment(eventType, NewId()), v3: false);
        var served = await ServedIdAsync(client, v3: false);

        var put = await client.PutAsJsonAsync("/api/v1/treatments", Treatment(eventType, served, isValid: false));

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        IsDeleted(JsonNode.Parse(await put.Content.ReadAsStringAsync())).Should().BeTrue();
        (await LiveAsync(client)).Should().Be(Live.None);
        (await HistoryAsync(client)).Should().Contain(t => IsDeleted(t));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Two_versions_of_one_id_in_a_batch_leave_the_last(bool firstValid, bool lastValid)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();

        var response = await client.PostAsJsonAsync("/api/v1/treatments", new JsonArray(
            Treatment("Correction Bolus", id, isValid: firstValid ? null : false),
            Treatment("Correction Bolus", id, isValid: lastValid ? null : false)));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var answered = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        answered.Should().HaveCount(2).And.OnlyContain(t => IsDeleted(t) == !lastValid);
        var live = await LiveAsync(client);
        if (lastValid)
            ShouldServeLive(live, "Correction Bolus");
        else
            live.Should().Be(Live.None);
    }

    /// <summary>
    /// Under a synthetic id an invalid record and a valid one with the same content in a batch are two
    /// records, in either order: the valid one is stored, and the batch leaves what sending its records
    /// one at a time leaves. An invalid one sent first, with nothing live under its id, is stored deleted.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_invalid_record_and_a_valid_one_with_its_content_in_a_batch_stay_apart(bool v3, bool invalidFirst)
    {
        var client = CreateAuthenticatedClient();
        JsonObject valid = Treatment("Correction Bolus"), invalid = Treatment("Correction Bolus", isValid: false);
        var batch = invalidFirst ? new JsonArray(invalid, valid) : new JsonArray(valid, invalid);

        var response = await client.PostAsJsonAsync(v3 ? "/api/v3/treatments/bulk" : "/api/v1/treatments", batch);

        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        if (!v3)
        {
            JsonNode.Parse(body)!.AsArray().Select(IsDeleted)
                .Should().Equal(invalidFirst ? [true, false] : [false, true]);
        }
        ShouldServeLive(await LiveAsync(client), "Correction Bolus");
        (await HistoryAsync(client)).Count(IsDeleted).Should().Be(invalidFirst ? 1 : 0);
    }

    /// <summary>
    /// A v1 PUT with <c>isValid: false</c> of a meal split on its <c>preBolus</c> deletes the bolus it
    /// names, as any update that leaves a treatment invalid does, rather than writing the meal again. Its
    /// carbs are a treatment of their own.
    /// </summary>
    [Fact]
    public async Task A_v1_put_with_isValid_false_of_a_prebolused_meal_deletes_its_bolus()
    {
        var client = CreateAuthenticatedClient();
        var meal = Treatment("Meal Bolus");
        meal["preBolus"] = 15;
        await UploadAsync(client, meal, v3: false);
        var served = (await GetAsync(client, "/api/v1/treatments")).AsArray()
            .Should().ContainSingle(t => t!["insulin"] != null).Subject!["_id"]!.GetValue<string>();

        var put = Treatment("Meal Bolus", served, isValid: false);
        put["preBolus"] = 15;
        var response = await client.PutAsJsonAsync("/api/v1/treatments", put);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var live = await LiveAsync(client);
        (live.Boluses, live.Iob).Should().Be((0, 0.0));
        (await GetAsync(client, "/api/v1/treatments")).AsArray().Should().NotContain(t => t!["insulin"] != null);
        (await HistoryAsync(client)).Should().Contain(t => IsDeleted(t) && t!["insulin"] != null);
    }

    /// <summary>
    /// An upload that deletes the live treatment its id names is announced, through the treatment
    /// service's events, as that treatment's delete and as nothing else.
    /// </summary>
    [Fact]
    public async Task An_upload_that_deletes_a_live_treatment_is_announced_as_its_delete()
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment("Correction Bolus", id), v3: false);
        var events = new RecordedEvents();

        await using (var scope = OpenTenantScope())
        {
            var service = ActivatorUtilities.CreateInstance<TreatmentService>(
                scope.ServiceProvider, (IDataEventSink<TreatmentModel>)events);
            await service.CreateV1TreatmentsAsync([InvalidBolus(id)], WriteOrigin.Live);
        }

        var deleted = events.Deleted.Should().ContainSingle().Subject!;
        deleted.EventType.Should().Be("Correction Bolus");
        deleted.Insulin.Should().Be(2.0);
        deleted.IsValid.Should().BeFalse();
        events.Created.Should().BeEmpty();
        events.Updated.Should().BeEmpty();
        (await LiveAsync(client)).Should().Be(Live.None);
    }

    /// <summary>
    /// An upload's delete of a live treatment commits before the batch's other writes, so a batch that
    /// fails after it still announces the delete: a retry finds nothing live and announces nothing.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_delete_committed_before_a_batch_fails_is_announced(bool storeTimesOut)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment("Correction Bolus", id), v3: false);
        var events = new RecordedEvents();

        await using (var scope = OpenTenantScope())
        {
            var notes = new ProxyGenerator().CreateInterfaceProxyWithTarget(
                scope.ServiceProvider.GetRequiredService<INoteRepository>(), new FailsWritingNotes(storeTimesOut));
            var decomposer = TreatmentDecomposerDoubles.Resolved(scope.ServiceProvider, notes);
            var store = ActivatorUtilities.CreateInstance<TreatmentReadService>(scope.ServiceProvider, decomposer);
            var service = ActivatorUtilities.CreateInstance<TreatmentService>(
                scope.ServiceProvider, store, decomposer, (IDataEventSink<TreatmentModel>)events);

            var act = () => service.CreateV1TreatmentsAsync(
            [
                InvalidBolus(id),
                new TreatmentModel { EventType = "Note", Notes = "after", Mills = _createdAt.AddMinutes(1).ToUnixTimeMilliseconds() },
            ], WriteOrigin.Live);
            await act.Should().ThrowAsync<Exception>();
        }

        events.Deleted.Should().ContainSingle();
        (await LiveAsync(client)).Should().Be(Live.None);
    }

    private TreatmentModel InvalidBolus(string id) => new()
    {
        Id = id,
        EventType = "Correction Bolus",
        Insulin = 2.0,
        Mills = _createdAt.ToUnixTimeMilliseconds(),
        EnteredBy = "AndroidAPS",
        IsValid = false,
    };

    private AsyncServiceScope OpenTenantScope() => TenantJobScope.Open(
        Fixture.Services,
        new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false),
        "test:uploaded-invalid");

    private sealed class RecordedEvents : IDataEventSink<TreatmentModel>
    {
        public List<TreatmentModel> Created { get; } = [];
        public List<TreatmentModel> Updated { get; } = [];
        public List<TreatmentModel?> Deleted { get; } = [];

        public Task OnCreatedAsync(IReadOnlyList<TreatmentModel> items, CancellationToken ct = default)
        {
            Created.AddRange(items);
            return Task.CompletedTask;
        }

        public Task OnCreatedAsync(TreatmentModel item, CancellationToken ct = default)
        {
            Created.Add(item);
            return Task.CompletedTask;
        }

        public Task OnUpdatedAsync(TreatmentModel item, CancellationToken ct = default)
        {
            Updated.Add(item);
            return Task.CompletedTask;
        }

        public Task OnDeletedAsync(TreatmentModel? item, CancellationToken ct = default)
        {
            Deleted.Add(item);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fails every note write: with a timeout, which ends the batch as a store failure, or with a
    /// failure the batch does not expect.
    /// </summary>
    private sealed class FailsWritingNotes(bool timesOut) : IInterceptor
    {
        public void Intercept(IInvocation invocation)
        {
            if (invocation.Method.Name == nameof(INoteRepository.BulkUpsertAsync))
                throw timesOut ? new TimeoutException() : new NotSupportedException();

            invocation.Proceed();
        }
    }

    /// <summary>
    /// A connector keys a meal's halves apart; the meal is served under its bolus's id, and the
    /// carbs go and come back with it.
    /// </summary>
    [Fact]
    public async Task An_upload_with_isValid_false_deletes_a_meals_carbs_keyed_apart()
    {
        var client = CreateAuthenticatedClient();
        var correlationId = Guid.CreateVersion7();
        var suffix = NewId();
        await WithTenantScopeAsync(async sp =>
        {
            await sp.GetRequiredService<IBolusRepository>().CreateAsync(new V4Models.Bolus
            {
                Timestamp = _createdAt.UtcDateTime,
                LegacyId = "glooko_bolus_" + suffix,
                SyncIdentifier = "glooko_bolus_" + suffix,
                Device = "glooko-connector",
                DataSource = "glooko-connector",
                Insulin = 3.0,
                BolusType = V4Models.BolusType.Normal,
                CorrelationId = correlationId,
            }, WriteOrigin.Live);
            await sp.GetRequiredService<ICarbIntakeRepository>().CreateAsync(new V4Models.CarbIntake
            {
                Timestamp = _createdAt.UtcDateTime,
                LegacyId = "glooko_carbs_" + suffix,
                SyncIdentifier = "glooko_carbs_" + suffix,
                Device = "glooko-connector",
                DataSource = "glooko-connector",
                Carbs = 30.0,
                CorrelationId = correlationId,
            }, WriteOrigin.Live);
            return true;
        });
        var meal = await ServedIdAsync(client, v3: false);
        (await LiveAsync(client)).CarbIntakes.Should().Be(1);

        await UploadAsync(client, Treatment("Meal Bolus", meal, isValid: false), v3: false);

        (await LiveAsync(client)).Should().Be(Live.None);

        await UploadAsync(client, Treatment("Meal Bolus", meal), v3: false);

        ShouldServeLive(await LiveAsync(client), "Meal Bolus");
    }

    /// <summary>
    /// An open override is ended by the next one that starts. An override stored deleted never starts,
    /// so the open one runs on.
    /// </summary>
    [Fact]
    public async Task An_upload_with_isValid_false_does_not_end_the_open_override_before_it()
    {
        var client = CreateAuthenticatedClient();
        var open = Treatment("Temporary Override", NewId(), at: _createdAt.AddMinutes(-20));
        open.Remove("duration");
        await UploadAsync(client, open, v3: false);
        var before = (await GetAsync(client, "/api/v1/treatments")).AsArray().Should().ContainSingle().Subject!.ToJsonString();

        var invalid = Treatment("Temporary Override", NewId(), isValid: false);
        invalid.Remove("duration");
        await UploadAsync(client, invalid, v3: false);

        (await GetAsync(client, "/api/v1/treatments")).AsArray().Should().ContainSingle()
            .Subject!.ToJsonString().Should().Be(before);
    }

    [Theory]
    [InlineData("Correction Bolus")]
    [InlineData("Temporary Override")]
    public async Task A_v3_update_that_sets_isValid_false_is_refused(string eventType)
    {
        var client = CreateAuthenticatedClient();
        var id = NewId();
        await UploadAsync(client, Treatment(eventType, id), v3: true);
        var identifier = await ServedIdAsync(client, v3: true);

        var put = await client.PutAsJsonAsync($"/api/v3/treatments/{identifier}", Treatment(eventType, id, isValid: false));
        var patch = await client.PatchAsJsonAsync($"/api/v3/treatments/{identifier}", new JsonObject { ["isValid"] = false });

        foreach (var response in new[] { put, patch })
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>()
                .Should().Be("Field isValid cannot be modified by the client");
        }

        (await GetAsync(client, "/api/v1/treatments")).AsArray().Should().ContainSingle();
    }
}
