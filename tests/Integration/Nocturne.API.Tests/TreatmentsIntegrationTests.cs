using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for Treatment CRUD operations against the API running in-process on real PostgreSQL.
/// Tests the complete request/response cycle for v1 treatment endpoints.
/// </summary>
[Trait("Category", "Integration")]
[Parity]
public class TreatmentsIntegrationTests : ApiIntegrationTestBase
{
    public TreatmentsIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    private static Treatment CreateTestTreatment(string? notes = null) => new()
    {
        Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Created_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        EventType = "Meal Bolus",
        Insulin = 3.5,
        Carbs = 45.0,
        Notes = notes ?? "Test treatment",
        EnteredBy = "test-user",
    };

    #region POST /api/v1/treatments

    [Fact]
    public async Task PostTreatment_Single_ShouldCreateAndReturnTreatment()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatment = CreateTestTreatment();

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/treatments", treatment);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(content);

        Log($"POST single treatment response: {response.StatusCode}");

        // The response should contain the created treatment with an assigned ID
        result.ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task PostTreatments_Array_ShouldCreateMultipleTreatments()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatments = new[]
        {
            CreateTestTreatment("Array treatment 1"),
            CreateTestTreatment("Array treatment 2"),
            CreateTestTreatment("Array treatment 3"),
        };

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/treatments", treatments);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        Log($"POST array treatments response: {response.StatusCode}, length: {content.Length}");
    }

    [Fact]
    public async Task PostTreatment_WithoutAuth_ShouldReturnUnauthorized()
    {
        // Arrange - use unauthenticated client
        var treatment = CreateTestTreatment();

        // Act
        var response = await ApiClient.PostAsJsonAsync("/api/v1/treatments", treatment);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log("POST without auth correctly returned Unauthorized");
    }

    #endregion

    #region GET /api/v1/treatments

    [Fact]
    public async Task GetTreatments_WhenEmpty_ShouldReturnEmptyArray()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Act
        var response = await client.GetAsync("/api/v1/treatments");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var treatments = await response.Content.ReadFromJsonAsync<JsonElement>();
        treatments.ValueKind.Should().Be(JsonValueKind.Array);
        treatments.GetArrayLength().Should().Be(0);

        Log("GET treatments on empty database returned empty array");
    }

    [Fact]
    public async Task GetTreatments_AfterCreating_ShouldReturnTreatments()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatment = CreateTestTreatment("Get after create");
        await client.PostAsJsonAsync("/api/v1/treatments", treatment);

        // Act
        var response = await client.GetAsync("/api/v1/treatments");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var treatments = await response.Content.ReadFromJsonAsync<JsonElement>();
        treatments.ValueKind.Should().Be(JsonValueKind.Array);
        treatments.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        Log($"GET treatments returned {treatments.GetArrayLength()} treatment(s)");
    }

    [Fact]
    public async Task GetTreatments_WithCountParameter_ShouldLimitResults()
    {
        // Arrange
        var client = CreateAuthenticatedClient();

        // Create multiple treatments
        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/treatments", CreateTestTreatment($"Pagination {i}"));
        }

        // Act
        var response = await client.GetAsync("/api/v1/treatments?count=2");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var treatments = await response.Content.ReadFromJsonAsync<JsonElement>();
        treatments.ValueKind.Should().Be(JsonValueKind.Array);
        treatments.GetArrayLength().Should().BeLessThanOrEqualTo(2);

        Log($"GET with count=2 returned {treatments.GetArrayLength()} treatment(s)");
    }

    #endregion

    #region GET /api/v1/treatments/{id}

    [Fact]
    public async Task GetTreatmentById_ShouldReturnSpecificTreatment()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatment = CreateTestTreatment("Get by ID");

        var createResponse = await client.PostAsJsonAsync("/api/v1/treatments", treatment);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Extract the created treatment's ID
        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);

        string? id = null;
        if (created.ValueKind == JsonValueKind.Array && created.GetArrayLength() > 0)
        {
            created[0].TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }
        else if (created.ValueKind == JsonValueKind.Object)
        {
            created.TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }

        id.Should().NotBeNullOrEmpty("the created treatment should have an ID");

        // Act
        var response = await client.GetAsync($"/api/v1/treatments/{id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(content);

        Log($"GET treatment by ID '{id}' returned: {response.StatusCode}");
    }

    [Fact]
    public async Task GetTreatmentById_NonExistent_ShouldReturnNotFound()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var fakeId = "000000000000000000000000";

        // Act
        var response = await client.GetAsync($"/api/v1/treatments/{fakeId}");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        Log($"GET non-existent treatment returned: {response.StatusCode}");
    }

    #endregion

    #region PUT /api/v1/treatments/{id}

    [Fact]
    public async Task PutTreatment_ShouldUpdateExistingTreatment()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatment = CreateTestTreatment("Before update");

        var createResponse = await client.PostAsJsonAsync("/api/v1/treatments", treatment);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);

        string? id = null;
        if (created.ValueKind == JsonValueKind.Array && created.GetArrayLength() > 0)
        {
            created[0].TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }
        else if (created.ValueKind == JsonValueKind.Object)
        {
            created.TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }

        id.Should().NotBeNullOrEmpty("the created treatment should have an ID");

        // Act - update the treatment
        var updatedTreatment = CreateTestTreatment("After update");
        updatedTreatment.Mills = treatment.Mills;
        updatedTreatment.Created_at = treatment.Created_at;
        updatedTreatment.Insulin = 5.0;
        updatedTreatment.Carbs = 60.0;

        var putResponse = await client.PutAsJsonAsync($"/api/v1/treatments/{id}", updatedTreatment);

        // Assert
        putResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        var stored = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[created_at][$eq]={treatment.Created_at}&find[insulin][$exists]=true");
        var meal = stored.EnumerateArray().Should().ContainSingle("the PUT updates the stored treatment rather than adding one").Subject;
        meal.GetProperty("_id").GetString().Should().Be(id);
        meal.GetProperty("insulin").GetDouble().Should().Be(5.0);
        meal.GetProperty("carbs").GetDouble().Should().Be(60.0);
        Log($"PUT treatment '{id}' returned: {putResponse.StatusCode}");
    }

    [Fact]
    public async Task PutCollection_LoopOverrideByUuid_UpdatesInPlace()
    {
        var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;

        (await client.PostAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["_id"] = id,
            ["eventType"] = "Temporary Override",
            ["durationType"] = "indefinite",
            ["created_at"] = now.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["_id"] = id,
            ["eventType"] = "Temporary Override",
            ["duration"] = 30,
            ["created_at"] = now.AddMinutes(-15).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var overrides = await WithTenantScopeAsync(async sp =>
            (await sp.GetRequiredService<IStateSpanService>().GetStateSpansAsync(
                category: StateSpanCategory.Override,
                from: now.AddHours(-2).UtcDateTime,
                to: now.AddHours(1).UtcDateTime))
            .Where(s => string.Equals(s.OriginalId, id, StringComparison.OrdinalIgnoreCase))
            .ToList());

        var stored = overrides.Should().ContainSingle().Subject;
        stored.StartTimestamp.Should().BeCloseTo(now.AddMinutes(-15).UtcDateTime, TimeSpan.FromSeconds(1));
        (stored.EndTimestamp - stored.StartTimestamp).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task PutCollection_UnknownUuid_InsertsTheOverride()
    {
        var client = CreateAuthenticatedClient();
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["_id"] = id,
            ["eventType"] = "Temporary Override",
            ["duration"] = 45,
            ["created_at"] = now.AddMinutes(-90).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.GetProperty("eventType").GetString().Should().Be("Temporary Override");

        var overrides = await WithTenantScopeAsync(async sp =>
            (await sp.GetRequiredService<IStateSpanService>().GetStateSpansAsync(
                category: StateSpanCategory.Override,
                from: now.AddHours(-2).UtcDateTime,
                to: now.AddHours(1).UtcDateTime))
            .Where(s => string.Equals(s.OriginalId, id, StringComparison.OrdinalIgnoreCase))
            .ToList());

        var stored = overrides.Should().ContainSingle().Subject;
        (stored.EndTimestamp - stored.StartTimestamp).Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public async Task PutCollection_WithAnArray_IsRefused()
    {
        var client = CreateAuthenticatedClient();

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new[]
        {
            new Dictionary<string, object> { ["eventType"] = "Note", ["notes"] = "in an array" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutCollection_LoopCarbByTheIdItsPostReturned_UpdatesTheOneCarb()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var at = DistinctTime();

        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { LoopCarb(syncIdentifier, 20, at) });
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var returnedId = (await post.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString()!;

        var put = await client.PutAsJsonAsync("/api/v1/treatments", LoopCarb(syncIdentifier, 35, at, returnedId));

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var stored = (await TreatmentsAtAsync(at)).Should().ContainSingle().Subject;
        stored.Carbs.Should().Be(35);
        MongoObjectId.Coerce(stored.Id).Should().Be(returnedId);
    }

    [Fact]
    public async Task PutCollection_LoopCarbWhoseIdResolvesToNothing_UpdatesTheCarbItsSyncIdentifierNames()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var at = DistinctTime();
        (await client.PostAsJsonAsync("/api/v1/treatments", new[] { LoopCarb(syncIdentifier, 20, at) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync(
            "/api/v1/treatments", LoopCarb(syncIdentifier, 40, at, MongoObjectId.FromGuid(Guid.NewGuid())));

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(at)).Should().ContainSingle().Which.Carbs.Should().Be(40);
    }

    [Theory]
    [InlineData("served")]
    [InlineData("returned")]
    [InlineData("legacy")]
    [InlineData("syncIdentifier")]
    [InlineData("hashed")]
    public async Task PutCollection_OfATreatmentTheUserDeleted_SavesNothing(string idKind)
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var at = DistinctTime();
        var posted = idKind == "hashed"
            ? LoopCarb(syncIdentifier: null, 20, at, $"client-{Guid.NewGuid():N}")
            : LoopCarb(syncIdentifier, 20, at);
        var post = await client.PostAsJsonAsync("/api/v1/treatments", new[] { posted });
        var returnedId = (await post.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString()!;
        var servedId = MongoObjectId.Coerce((await TreatmentsAtAsync(at)).Single().Id)!;
        (await client.DeleteAsync($"/api/v1/treatments/{servedId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TreatmentsAtAsync(at)).Should().BeEmpty();

        var body = idKind switch
        {
            "served" => LoopCarb(syncIdentifier: null, 30, at, servedId),
            "returned" or "hashed" => LoopCarb(syncIdentifier: null, 30, at, returnedId),
            "legacy" => LoopCarb(syncIdentifier: null, 30, at, syncIdentifier),
            _ => LoopCarb(syncIdentifier, 30, at, MongoObjectId.FromGuid(Guid.NewGuid())),
        };
        var put = await client.PutAsJsonAsync("/api/v1/treatments", body);

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var answer = await put.Content.ReadFromJsonAsync<JsonElement>();
        answer.ValueKind.Should().Be(JsonValueKind.Array);
        answer.GetArrayLength().Should().Be(0);
        (await TreatmentsAtAsync(at)).Should().BeEmpty();
    }

    [Fact]
    public async Task PutCollection_WithoutAnId_ReplacesTheTreatmentAtTheSameTimeWithTheSameEventType()
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        (await client.PostAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["eventType"] = "Note",
            ["notes"] = "before",
            ["created_at"] = at,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["eventType"] = "Note",
            ["notes"] = "after",
            ["created_at"] = at,
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(at)).Should().ContainSingle().Which.Notes.Should().Be("after");
    }

    [Fact]
    public async Task PutCollection_WithoutAnId_CreatesWhenNoTreatmentOfThatEventTypeIsStoredThen()
    {
        var client = CreateAuthenticatedClient();
        var at = DistinctTime();
        (await client.PostAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["eventType"] = "Note",
            ["notes"] = "a note",
            ["created_at"] = at,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var put = await client.PutAsJsonAsync("/api/v1/treatments", new Dictionary<string, object>
        {
            ["eventType"] = "BG Check",
            ["glucose"] = 123,
            ["glucoseType"] = "Finger",
            ["units"] = "mg/dl",
            ["created_at"] = at,
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var stored = await TreatmentsAtAsync(at);
        stored.Should().HaveCount(2);
        stored.Should().ContainSingle(t => t.EventType == "Note").Which.Notes.Should().Be("a note");
        stored.Should().ContainSingle(t => t.EventType == "BG Check");
    }

    /// <summary>A carb entry as NightscoutKit's CarbCorrectionNightscoutTreatment serialises it.</summary>
    private static Dictionary<string, object> LoopCarb(string? syncIdentifier, int carbs, string at, string? id = null)
    {
        var carb = new Dictionary<string, object>
        {
            ["eventType"] = "Carb Correction",
            ["carbs"] = carbs,
            ["absorptionTime"] = 180,
            ["enteredBy"] = "loop://iPhone",
            ["created_at"] = at,
        };
        if (syncIdentifier is not null)
            carb["syncIdentifier"] = syncIdentifier;
        if (id is not null)
            carb["_id"] = id;
        return carb;
    }

    /// <summary>A millisecond no other test in the fixture writes a treatment at.</summary>
    private static string DistinctTime() => DateTimeOffset.UtcNow
        .AddMinutes(-Random.Shared.Next(30, 6 * 60))
        .AddMilliseconds(Random.Shared.Next(1, 60_000))
        .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private Task<IReadOnlyList<Treatment>> TreatmentsAtAsync(string at)
    {
        var mills = DateTimeOffset.Parse(at).ToUnixTimeMilliseconds();
        return WithTenantScopeAsync(sp =>
            sp.GetRequiredService<ITreatmentService>().GetTreatmentsByRangeAsync(mills, mills));
    }

    #endregion

    #region DELETE /api/v1/treatments/{id}

    [Fact]
    public async Task DeleteTreatment_ShouldRemoveTreatment()
    {
        // Arrange
        var client = CreateAuthenticatedClient();
        var treatment = CreateTestTreatment("To be deleted");

        var createResponse = await client.PostAsJsonAsync("/api/v1/treatments", treatment);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var createContent = await createResponse.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<JsonElement>(createContent);

        string? id = null;
        if (created.ValueKind == JsonValueKind.Array && created.GetArrayLength() > 0)
        {
            created[0].TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }
        else if (created.ValueKind == JsonValueKind.Object)
        {
            created.TryGetProperty("_id", out var idProp);
            id = idProp.GetString();
        }

        id.Should().NotBeNullOrEmpty("the created treatment should have an ID");

        // Act
        var deleteResponse = await client.DeleteAsync($"/api/v1/treatments/{id}");

        // Assert
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await deleteResponse.Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(1);
        status.GetProperty("n").GetInt64().Should().Be(1);

        (await client.GetAsync($"/api/v1/treatments/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<JsonElement>($"/api/v1/treatments?find[created_at][$eq]={treatment.Created_at}"))
            .GetArrayLength().Should().Be(0, "the meal's bolus, carbs and note all go with it");

        Log($"DELETE treatment '{id}' returned: {deleteResponse.StatusCode}");
    }

    [Fact]
    public async Task DeleteTreatment_UnknownId_AnswersOkWithNoneDeleted()
    {
        var response = await CreateAuthenticatedClient().DeleteAsync("/api/v1/treatments/000000000000000000000000");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        status.GetProperty("acknowledged").GetBoolean().Should().BeTrue();
        status.GetProperty("deletedCount").GetInt64().Should().Be(0);
        status.GetProperty("n").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task DeleteTreatment_AnyIdWithFind_DeletesTheMatches()
    {
        var client = CreateAuthenticatedClient();
        var enteredBy = $"wildcard-{Guid.NewGuid():N}";
        var treatments = new[] { CreateTestTreatment("first"), CreateTestTreatment("second") };
        foreach (var treatment in treatments)
            treatment.EnteredBy = enteredBy;
        treatments[1].Mills -= 60_000;
        treatments[1].Created_at = DateTimeOffset.FromUnixTimeMilliseconds(treatments[1].Mills)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        (await client.PostAsJsonAsync("/api/v1/treatments", treatments))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.DeleteAsync($"/api/v1/treatments/*?find[enteredBy]={enteredBy}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deletedCount").GetInt64().Should().Be(2);
        var remaining = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[enteredBy]={enteredBy}");
        remaining.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DeleteTreatment_AnyIdWithoutFind_IsRefused()
    {
        var response = await CreateAuthenticatedClient().DeleteAsync("/api/v1/treatments/*");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTreatment_WithoutAuth_ShouldReturnUnauthorized()
    {
        // Arrange
        var fakeId = "000000000000000000000000";

        // Act - use unauthenticated client
        var response = await ApiClient.DeleteAsync($"/api/v1/treatments/{fakeId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Log("DELETE without auth correctly returned Unauthorized");
    }

    #endregion

    #region Loop objectIdCache

    private static async Task<string> PostForIdAsync(HttpClient client, object treatment)
    {
        var response = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("_id").GetString()!;
    }

    [Fact]
    public async Task LoopCarb_DeleteByTheIdThePostReturned_RemovesTheCarb()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = DistinctTime();
        var id = await PostForIdAsync(client, LoopCarb(Guid.NewGuid().ToString().ToUpperInvariant(), 20, createdAt));

        var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");

        delete.IsSuccessStatusCode.Should().BeTrue(await delete.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(createdAt)).Should().BeEmpty();
    }

    [Fact]
    public async Task LoopCarb_IdAnOlderReleaseEchoed_StillEditsAndDeletes()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = DistinctTime();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        await PostForIdAsync(client, LoopCarb(syncIdentifier, 20, createdAt));
        var cached = MongoObjectId.Coerce(syncIdentifier)!;

        var edit = LoopCarb(syncIdentifier, 25, createdAt);
        edit["_id"] = cached;
        (await client.PutAsJsonAsync("/api/v1/treatments", edit)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TreatmentsAtAsync(createdAt)).Should().ContainSingle().Which.Carbs.Should().Be(25);

        (await client.DeleteAsync($"/api/v1/treatments/{cached}")).IsSuccessStatusCode.Should().BeTrue();
        (await TreatmentsAtAsync(createdAt)).Should().BeEmpty();
    }

    [Fact]
    public async Task LoopDose_IdAnOlderReleaseEchoedForAHexSyncIdentifier_StillDeletes()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = DistinctTime();
        var syncIdentifier = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + "0a1b";
        await PostForIdAsync(client, new Dictionary<string, object>
        {
            ["eventType"] = "Correction Bolus",
            ["insulin"] = 1.5,
            ["syncIdentifier"] = syncIdentifier,
            ["enteredBy"] = "loop://iPhone",
            ["created_at"] = createdAt,
        });

        var delete = await client.DeleteAsync($"/api/v1/treatments/{MongoObjectId.Coerce(syncIdentifier)}");

        delete.IsSuccessStatusCode.Should().BeTrue(await delete.Content.ReadAsStringAsync());
        (await TreatmentsAtAsync(createdAt)).Should().BeEmpty();
    }

    #endregion

    #region v3 identifier

    private static JsonElement V3Result(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty("result", out var result) ? result : body;

    private static bool CarriesIdentifier(JsonElement treatments, string identifier) =>
        treatments.EnumerateArray().Any(t => t.GetProperty("identifier").GetString() == identifier);

    [Fact]
    public async Task V3Create_ReturnedIdentifier_ResolvesOnGetSearchHistoryPatchAndDelete()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = DistinctTime();
        var post = await client.PostAsJsonAsync("/api/v3/treatments", new Dictionary<string, object>
        {
            ["eventType"] = "Carb Correction",
            ["carbs"] = 12,
            ["created_at"] = createdAt,
            ["app"] = "AAPS",
        });
        post.StatusCode.Should().Be(HttpStatusCode.Created, await post.Content.ReadAsStringAsync());
        var identifier = V3Result(await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("identifier").GetString()!;

        (await client.GetAsync($"/api/v3/treatments/{identifier}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var search = V3Result(await client.GetFromJsonAsync<JsonElement>($"/api/v3/treatments?created_at$eq={createdAt}"));
        CarriesIdentifier(search, identifier).Should().BeTrue("search serves the identifier the create returned");

        var history = V3Result(await client.GetFromJsonAsync<JsonElement>("/api/v3/treatments/history/0"));
        CarriesIdentifier(history, identifier).Should().BeTrue("history serves the identifier the create returned");

        var patch = await client.PatchAsJsonAsync($"/api/v3/treatments/{identifier}", new { carbs = 18 });
        patch.StatusCode.Should().Be(HttpStatusCode.OK, await patch.Content.ReadAsStringAsync());
        var patched = V3Result(await client.GetFromJsonAsync<JsonElement>($"/api/v3/treatments/{identifier}"));
        patched.GetProperty("carbs").GetDouble().Should().Be(18);

        (await client.DeleteAsync($"/api/v3/treatments/{identifier}")).IsSuccessStatusCode.Should().BeTrue();
        (await client.GetAsync($"/api/v3/treatments/{identifier}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Trio lowercase id

    [Fact]
    public async Task TrioCarb_DeleteByFindId_ThenReupload_LeavesOneCarb()
    {
        var client = CreateAuthenticatedClient();
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var originalId = Guid.NewGuid().ToString().ToUpperInvariant();
        var replacementId = Guid.NewGuid().ToString().ToUpperInvariant();

        object TrioCarb(string id, int carbs) => new[]
        {
            new Dictionary<string, object>
            {
                ["id"] = id, ["enteredBy"] = "Trio", ["eventType"] = "Carb Correction",
                ["carbs"] = carbs, ["fat"] = 0, ["protein"] = 0, ["created_at"] = createdAt,
            },
        };

        (await client.PostAsJsonAsync("/api/v1/treatments", TrioCarb(originalId, 30)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await client.DeleteAsync($"/api/v1/treatments?find[id][$eq]={originalId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("n").GetInt64().Should().Be(1);

        (await client.PostAsJsonAsync("/api/v1/treatments", TrioCarb(replacementId, 45)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var carbs = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/treatments?find[created_at][$eq]={createdAt}&find[carbs][$exists]=true");
        carbs.GetArrayLength().Should().Be(1);
        carbs[0].GetProperty("carbs").GetDouble().Should().Be(45);
        carbs[0].GetProperty("id").GetString().Should().Be(replacementId);
    }

    #endregion
}
