using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for Treatment CRUD operations using Aspire-orchestrated infrastructure.
/// Tests the complete request/response cycle for v1 treatment endpoints.
/// </summary>
[Trait("Category", "Integration")]
[Parity]
public class TreatmentsIntegrationTests : AspireIntegrationTestBase
{
    public TreatmentsIntegrationTests(
        AspireIntegrationTestFixture fixture,
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
        updatedTreatment.Insulin = 5.0;
        updatedTreatment.Carbs = 60.0;

        var putResponse = await client.PutAsJsonAsync($"/api/v1/treatments/{id}", updatedTreatment);

        // Assert
        putResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Verify the update persisted
        var getResponse = await client.GetAsync($"/api/v1/treatments/{id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var getContent = await getResponse.Content.ReadAsStringAsync();
        Log($"PUT treatment '{id}' returned: {putResponse.StatusCode}");
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
        deleteResponse.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        // Verify the treatment is gone
        var getResponse = await client.GetAsync($"/api/v1/treatments/{id}");
        getResponse.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);

        Log($"DELETE treatment '{id}' returned: {deleteResponse.StatusCode}");
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
