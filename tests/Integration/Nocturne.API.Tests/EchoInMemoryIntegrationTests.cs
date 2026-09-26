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
/// Integration tests for Echo endpoints using in-memory MongoDB
/// Tests the complete request/response cycle for debugging and preview functionality
/// </summary>
[Trait("Category", "Integration")]
public class EchoInMemoryIntegrationTests : ApiIntegrationTestBase
{
    public EchoInMemoryIntegrationTests(
        ApiIntegrationTestFixture fixture,
        Xunit.Abstractions.ITestOutputHelper output
    )
        : base(fixture, output) { }

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [InlineData("activity")]
    [InlineData("profile")]
    [InlineData("food")]
    public async Task EchoQuery_WithValidStorageTypes_ShouldReturnQueryInformation(
        string storageType
    )
    {
        // Arrange & Act
        var response = await ApiClient
            .GetAsync($"/api/v1/echo/{storageType}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("storage", out var storageProperty).Should().BeTrue();
        storageProperty.GetString().Should().Be(storageType);

        result.TryGetProperty("query", out var queryProperty).Should().BeTrue();
        queryProperty.ValueKind.Should().Be(JsonValueKind.Object);

        result.TryGetProperty("input", out var inputProperty).Should().BeTrue();
        inputProperty.ValueKind.Should().Be(JsonValueKind.Object);

        result.TryGetProperty("params", out var paramsProperty).Should().BeTrue();
        paramsProperty.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task EchoQuery_WithInvalidStorageType_ShouldReturnBadRequest()
    {
        // Arrange & Act
        var response = await ApiClient
            .GetAsync("/api/v1/echo/invalidtype", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );
        result.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetString().Should().Contain("Invalid storage type");
    }

    [Fact]
    public async Task EchoQuery_WithQueryParameters_ShouldIncludeParametersInResponse()
    {
        // Arrange
        var queryString = "?count=50&find={\"type\":\"sgv\"}&dateString=2024";

        // Act
        var response = await ApiClient
            .GetAsync($"/api/v1/echo/entries{queryString}", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("input", out var inputProperty).Should().BeTrue();

        inputProperty.TryGetProperty("count", out var countProperty).Should().BeTrue();
        countProperty.GetString().Should().Be("50");

        inputProperty.TryGetProperty("find", out var findProperty).Should().BeTrue();
        findProperty.GetString().Should().Be("{\"type\":\"sgv\"}");

        inputProperty.TryGetProperty("dateString", out var dateStringProperty).Should().BeTrue();
        dateStringProperty.GetString().Should().Be("2024");

        result.TryGetProperty("queryString", out var queryStringProperty).Should().BeTrue();
        queryStringProperty.GetString().Should().Be(queryString);
    }

    [Fact]
    public async Task EchoQuery_WithModelAndSpec_ShouldIncludeInParameters()
    {
        // Arrange & Act
        var response = await ApiClient
            .GetAsync("/api/v1/echo/entries/sgv/current", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("params", out var paramsProperty).Should().BeTrue();

        paramsProperty.TryGetProperty("echo", out var echoProperty).Should().BeTrue();
        echoProperty.GetString().Should().Be("entries");

        paramsProperty.TryGetProperty("model", out var modelProperty).Should().BeTrue();
        modelProperty.GetString().Should().Be("sgv");

        paramsProperty.TryGetProperty("spec", out var specProperty).Should().BeTrue();
        specProperty.GetString().Should().Be("current");
    }

    [Fact]
    public async Task EchoQuery_ShouldIncludeTimestampInResponse()
    {
        // Arrange
        var beforeRequest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var response = await ApiClient
            .GetAsync("/api/v1/echo/entries", CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("timestamp", out var timestampProperty).Should().BeTrue();
        var timestamp = timestampProperty.GetInt64();

        var afterRequest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        timestamp.Should().BeInRange(beforeRequest, afterRequest);
    }

    [Fact]
    public async Task PreviewEntries_WithValidSingleEntry_ShouldReturnPreviewWithValidation()
    {
        // Arrange
        var entry = new Entry
        {
            Sgv = 120,
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateString = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                entry,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("entries", out var entriesProperty).Should().BeTrue();
        entriesProperty.GetArrayLength().Should().Be(1);

        result.TryGetProperty("validationResults", out var validationProperty).Should().BeTrue();
        validationProperty.GetArrayLength().Should().Be(1);

        result.TryGetProperty("summary", out var summaryProperty).Should().BeTrue();
        summaryProperty.TryGetProperty("totalEntries", out var totalProperty).Should().BeTrue();
        totalProperty.GetInt32().Should().Be(1);

        summaryProperty.TryGetProperty("validEntries", out var validProperty).Should().BeTrue();
        validProperty.GetInt32().Should().Be(1);

        summaryProperty.TryGetProperty("invalidEntries", out var invalidProperty).Should().BeTrue();
        invalidProperty.GetInt32().Should().Be(0);

        summaryProperty.TryGetProperty("preview", out var previewProperty).Should().BeTrue();
        previewProperty.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PreviewEntries_WithValidEntryArray_ShouldReturnCorrectSummary()
    {
        // Arrange
        var entries = new[]
        {
            new Entry
            {
                Sgv = 120,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            new Entry
            {
                Sgv = 130,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            },
            new Entry
            {
                Sgv = 140,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            },
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                entries,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("entries", out var entriesProperty).Should().BeTrue();
        entriesProperty.GetArrayLength().Should().Be(3);

        result.TryGetProperty("validationResults", out var validationProperty).Should().BeTrue();
        validationProperty.GetArrayLength().Should().Be(3);

        result.TryGetProperty("summary", out var summaryProperty).Should().BeTrue();
        summaryProperty.TryGetProperty("totalEntries", out var totalProperty).Should().BeTrue();
        totalProperty.GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task PreviewEntries_WithInvalidEntry_ShouldReturnValidationErrors()
    {
        // Arrange
        var invalidEntry = new Entry
        {
            // Missing required SGV/MBG and timestamp
            Type = "sgv",
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                invalidEntry,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("summary", out var summaryProperty).Should().BeTrue();
        summaryProperty.TryGetProperty("validEntries", out var validProperty).Should().BeTrue();
        validProperty.GetInt32().Should().Be(0);

        summaryProperty.TryGetProperty("invalidEntries", out var invalidProperty).Should().BeTrue();
        invalidProperty.GetInt32().Should().Be(1);

        result.TryGetProperty("validationResults", out var validationProperty).Should().BeTrue();
        var firstValidation = validationProperty[0];

        firstValidation.TryGetProperty("validation", out var validationDetails).Should().BeTrue();
        validationDetails.TryGetProperty("isValid", out var isValidProperty).Should().BeTrue();
        isValidProperty.GetBoolean().Should().BeFalse();

        validationDetails.TryGetProperty("errors", out var errorsProperty).Should().BeTrue();
        errorsProperty.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PreviewEntries_WithEntryHavingWarnings_ShouldIncludeWarnings()
    {
        // Arrange
        var entryWithWarnings = new Entry
        {
            Sgv = 2000, // Out of normal range - should trigger warning
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateString = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                entryWithWarnings,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("validationResults", out var validationProperty).Should().BeTrue();
        var firstValidation = validationProperty[0];

        firstValidation.TryGetProperty("validation", out var validationDetails).Should().BeTrue();
        validationDetails.TryGetProperty("warnings", out var warningsProperty).Should().BeTrue();
        warningsProperty.GetArrayLength().Should().BeGreaterThan(0);

        var warningText = warningsProperty[0].GetString();
        warningText.Should().Contain("out of normal range");
    }

    [Fact]
    public async Task PreviewEntries_WithNullData_ShouldReturnBadRequest()
    {
        // Arrange
        var content = new StringContent("null", System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await ApiClient
            .PostAsync("/api/v1/entries/preview", content, CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );
        result.TryGetProperty("error", out var errorProperty).Should().BeTrue();
        errorProperty.GetString().Should().Contain("Entry data is required");
    }

    [Fact]
    public async Task PreviewEntries_WithInvalidJsonFormat_ShouldReturnBadRequest()
    {
        // Arrange
        var invalidJson = "{ invalid json format }";
        var content = new StringContent(invalidJson, System.Text.Encoding.UTF8, "application/json");

        // Act
        var response = await ApiClient
            .PostAsync("/api/v1/entries/preview", content, CancellationToken.None);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PreviewEntries_ShouldIncludeTimestampInResults()
    {
        // Arrange
        var entry = new Entry
        {
            Sgv = 120,
            Type = "sgv",
            Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var beforeRequest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                entry,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        var afterRequest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        result.TryGetProperty("summary", out var summaryProperty).Should().BeTrue();
        summaryProperty.TryGetProperty("timestamp", out var timestampProperty).Should().BeTrue();
        var timestamp = timestampProperty.GetInt64();
        timestamp.Should().BeInRange(beforeRequest, afterRequest);

        result.TryGetProperty("validationResults", out var validationProperty).Should().BeTrue();
        var firstValidation = validationProperty[0];
        firstValidation
            .TryGetProperty("timestamp", out var validationTimestampProperty)
            .Should()
            .BeTrue();
        var validationTimestamp = validationTimestampProperty.GetInt64();
        validationTimestamp.Should().BeInRange(beforeRequest, afterRequest);
    }

    [Fact]
    public async Task PreviewEntries_WithMixedValidAndInvalidEntries_ShouldProvideCorrectSummary()
    {
        // Arrange
        var mixedEntries = new[]
        {
            new Entry
            {
                Sgv = 120,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, // Valid
            new Entry { Type = "sgv" }, // Invalid - missing SGV and timestamp
            new Entry
            {
                Sgv = 130,
                Type = "sgv",
                Mills = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            }, // Valid
        };

        // Act
        var response = await ApiClient
            .PostAsJsonAsync(
                "/api/v1/entries/preview",
                mixedEntries,
                cancellationToken: CancellationToken.None
            );

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: CancellationToken.None
        );

        result.TryGetProperty("summary", out var summaryProperty).Should().BeTrue();
        summaryProperty.TryGetProperty("totalEntries", out var totalProperty).Should().BeTrue();
        totalProperty.GetInt32().Should().Be(3);

        summaryProperty.TryGetProperty("validEntries", out var validProperty).Should().BeTrue();
        validProperty.GetInt32().Should().Be(2);

        summaryProperty.TryGetProperty("invalidEntries", out var invalidProperty).Should().BeTrue();
        invalidProperty.GetInt32().Should().Be(1);
    }
}
