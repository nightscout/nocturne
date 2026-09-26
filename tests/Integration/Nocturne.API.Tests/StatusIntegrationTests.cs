using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Integration tests for Status endpoints against the API running in-process on real PostgreSQL.
/// Tests the complete request/response cycle for various status formats against
/// the full distributed application stack.
/// </summary>
[Trait("Category", "Integration")]
public class StatusIntegrationTests : ApiIntegrationTestBase
{
    public StatusIntegrationTests(
        ApiIntegrationTestFixture fixture,
        ITestOutputHelper output
    )
        : base(fixture, output) { }

    /// <summary>
    /// GET /api/v1/status as Nightscout clients send it: the endpoint answers JSON only to an
    /// Accept that asks for it, and HTML otherwise.
    /// </summary>
    private static Task<HttpResponseMessage> GetJsonStatusAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client.SendAsync(request);
    }

    [Fact]
    public async Task GetStatus_Json_ShouldReturnValidStatusResponse()
    {
        // Arrange & Act
        var response = await GetJsonStatusAsync(ApiClient);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var status = JsonSerializer.Deserialize<JsonElement>(content);

        // Verify required fields exist (legacy compatibility)
        status.TryGetProperty("apiEnabled", out var apiEnabled).Should().BeTrue();
        apiEnabled.GetBoolean().Should().BeTrue();

        status.TryGetProperty("careportalEnabled", out var careportalEnabled).Should().BeTrue();
        careportalEnabled.GetBoolean().Should().Be(careportalEnabled.GetBoolean()); // Verify it's a valid boolean

        status.TryGetProperty("settings", out var settings).Should().BeTrue();
        settings.TryGetProperty("enable", out var enable).Should().BeTrue();
        enable.ValueKind.Should().Be(JsonValueKind.Array);

        status.TryGetProperty("name", out var name).Should().BeTrue();
        name.GetString().Should().Be("nightscout", "Nightscout clients read the name Nightscout reports");

        status.TryGetProperty("version", out var version).Should().BeTrue();
        version.GetString().Should().NotBeNullOrEmpty();

        status.TryGetProperty("serverTime", out var serverTime).Should().BeTrue();
        serverTime.ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task GetStatus_ShouldIncludeEnabledFeatures()
    {
        // Arrange & Act
        var response = await GetJsonStatusAsync(ApiClient);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var status = JsonSerializer.Deserialize<JsonElement>(content);

        status.TryGetProperty("settings", out var settings).Should().BeTrue();
        settings.TryGetProperty("enable", out var enable).Should().BeTrue();

        var enabledFeatures = new List<string>();
        foreach (var feature in enable.EnumerateArray())
        {
            enabledFeatures.Add(feature.GetString()!);
        }

        // Should have at least some basic features enabled
        enabledFeatures.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStatus_ServerTime_ShouldBeRecentUtc()
    {
        // Arrange
        var beforeRequest = DateTime.UtcNow;

        // Act
        var response = await GetJsonStatusAsync(ApiClient);

        // Assert
        var afterRequest = DateTime.UtcNow;
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var status = JsonSerializer.Deserialize<JsonElement>(content);

        status.TryGetProperty("serverTime", out var serverTimeElement).Should().BeTrue();
        var serverTimeString = serverTimeElement.GetString();

        DateTime.TryParse(
                serverTimeString,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var serverTime)
            .Should().BeTrue();
        serverTime.Should().BeAfter(beforeRequest.AddSeconds(-5));
        serverTime.Should().BeBefore(afterRequest.AddSeconds(5));
    }

    [Fact]
    public async Task GetStatus_ShouldHaveConsistentApiEnabledField()
    {
        // Arrange & Act
        var response = await GetJsonStatusAsync(ApiClient);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var status = JsonSerializer.Deserialize<JsonElement>(content);

        // The API should report that it's enabled since we're successfully calling it
        status.TryGetProperty("apiEnabled", out var apiEnabled).Should().BeTrue();
        apiEnabled.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetStatus_Settings_ShouldContainRequiredLegacyFields()
    {
        // Arrange & Act
        var response = await GetJsonStatusAsync(ApiClient);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        var status = JsonSerializer.Deserialize<JsonElement>(content);

        status.TryGetProperty("settings", out var settings).Should().BeTrue();

        // Verify that settings contain the expected legacy fields
        settings.TryGetProperty("enable", out _).Should().BeTrue();

        // These may be optional but if present should be valid
        if (settings.TryGetProperty("units", out var units))
        {
            var unitsValue = units.GetString();
            new[] { "mg/dl", "mmol" }.Should().Contain(unitsValue);
        }

        if (settings.TryGetProperty("timeFormat", out var timeFormat))
        {
            var timeFormatValue = timeFormat.GetInt32();
            new[] { 12, 24 }.Should().Contain(timeFormatValue);
        }
    }

    [Fact]
    public async Task GetStatus_Multiple_ShouldReturnConsistentResults()
    {
        // Arrange & Act
        var response1 = await GetJsonStatusAsync(ApiClient);
        var response2 = await GetJsonStatusAsync(ApiClient);

        // Assert
        response1.StatusCode.Should().Be(HttpStatusCode.OK);
        response2.StatusCode.Should().Be(HttpStatusCode.OK);

        var content1 = await response1.Content.ReadAsStringAsync(CancellationToken.None);
        var content2 = await response2.Content.ReadAsStringAsync(CancellationToken.None);

        var status1 = JsonSerializer.Deserialize<JsonElement>(content1);
        var status2 = JsonSerializer.Deserialize<JsonElement>(content2);

        // Core fields should be consistent between calls
        status1
            .GetProperty("name")
            .GetString()
            .Should()
            .Be(status2.GetProperty("name").GetString());
        status1
            .GetProperty("version")
            .GetString()
            .Should()
            .Be(status2.GetProperty("version").GetString());
        status1
            .GetProperty("apiEnabled")
            .GetBoolean()
            .Should()
            .Be(status2.GetProperty("apiEnabled").GetBoolean());

        // Settings should be consistent
        var settings1 = status1.GetProperty("settings");
        var settings2 = status2.GetProperty("settings");

        var enable1 = settings1.GetProperty("enable");
        var enable2 = settings2.GetProperty("enable");

        enable1.GetArrayLength().Should().Be(enable2.GetArrayLength());
    }

    [Fact]
    public async Task GetStatus_ShouldHandleConcurrentRequests()
    {
        // Arrange
        const int concurrentRequests = 10;
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act - Create separate HttpClient instances for concurrent requests
        for (int i = 0; i < concurrentRequests; i++)
        {
            tasks.Add(GetJsonStatusAsync(CreateHttpClient("nocturne-api")));
        }

        var responses = await Task.WhenAll(tasks);

        // Assert
        foreach (var response in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var content = await response.Content.ReadAsStringAsync(CancellationToken.None);
            var status = JsonSerializer.Deserialize<JsonElement>(content);

            status.TryGetProperty("apiEnabled", out var apiEnabled).Should().BeTrue();
            apiEnabled.GetBoolean().Should().BeTrue();

            status.TryGetProperty("name", out var name).Should().BeTrue();
            name.GetString().Should().Be("nightscout", "Nightscout clients read the name Nightscout reports");
        }
    }

    [Fact]
    public async Task Database_ShouldBeAccessible()
    {
        var connectionString = await GetPostgresConnectionStringAsync();

        connectionString.Should().NotBeNullOrEmpty();
        connectionString.Should().Contain("Host=");
        connectionString.Should().Contain("Database=");

        Log(
            $"PostgreSQL connection string available: {connectionString?[..Math.Min(50, connectionString?.Length ?? 0)]}..."
        );
    }
}
