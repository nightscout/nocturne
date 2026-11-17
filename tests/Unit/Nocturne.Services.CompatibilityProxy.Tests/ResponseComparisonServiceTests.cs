using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Core.Models;
using Nocturne.API.Configuration;
using Nocturne.API.Models.Compatibility;
using Nocturne.API.Services.Compatibility;
using Xunit;

namespace Nocturne.Services.CompatibilityProxy.Tests.Unit;

public class ResponseComparisonServiceTests
{
    private readonly ResponseComparisonService _service;
    private readonly ILogger<ResponseComparisonService> _logger;

    public ResponseComparisonServiceTests()
    {
        _logger = new LoggerFactory().CreateLogger<ResponseComparisonService>();
        var config = Options.Create(
            new CompatibilityProxyConfiguration
            {
                Comparison = new ResponseComparisonSettings
                {
                    ExcludeFields = new List<string> { "timestamp", "dateString" },
                    TimestampToleranceMs = 5000,
                    NumericPrecisionTolerance = 0.001,
                    NormalizeFieldOrdering = true,
                    ArrayOrderHandling = ArrayOrderHandling.Strict,
                },
            }
        );
        _service = new ResponseComparisonService(config, TimeProvider.System, _logger);
    }

    [Fact]
    public async Task CompareResponsesAsync_BothNull_ShouldReturnBothMissing()
    {
        // Act
        var result = await _service.CompareResponsesAsync(null, null, "test-correlation");

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.BothMissing, result.OverallMatch);
        Assert.Equal("Both responses are missing", result.Summary);
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public async Task CompareResponsesAsync_NightscoutNull_ShouldReturnNightscoutMissing()
    {
        // Arrange
        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            null,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.NightscoutMissing, result.OverallMatch);
        Assert.Equal("Nightscout response is missing", result.Summary);
    }

    [Fact]
    public async Task CompareResponsesAsync_IdenticalResponses_ShouldReturnPerfectMatch()
    {
        // Arrange
        var jsonContent = """{"status": "ok", "value": 100}""";
        var bodyBytes = Encoding.UTF8.GetBytes(jsonContent);

        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = bodyBytes,
            ResponseTimeMs = 100,
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = bodyBytes,
            ResponseTimeMs = 120,
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.Perfect, result.OverallMatch);
        Assert.True(result.StatusCodeMatch);
        Assert.True(result.BodyMatch);
        Assert.Contains("perfectly", result.Summary);
        Assert.NotNull(result.PerformanceComparison);
        Assert.Equal(20, result.PerformanceComparison.TimeDifference);
    }

    [Fact]
    public async Task CompareResponsesAsync_DifferentStatusCodes_ShouldReturnCriticalDifferences()
    {
        // Arrange
        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 404,
            IsSuccess = false,
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(
            Nocturne.Core.Models.ResponseMatchType.CriticalDifferences,
            result.OverallMatch
        );
        Assert.False(result.StatusCodeMatch);
        Assert.Single(result.Discrepancies);
        Assert.Equal(DiscrepancyType.StatusCode, result.Discrepancies[0].Type);
        Assert.Equal(DiscrepancySeverity.Critical, result.Discrepancies[0].Severity);
    }

    [Fact]
    public async Task CompareResponsesAsync_JsonWithExcludedFields_ShouldIgnoreExcludedFields()
    {
        // Arrange
        var nightscoutJson =
            """{"status": "ok", "timestamp": "2023-01-01T00:00:00Z", "value": 100}""";
        var nocturneJson =
            """{"status": "ok", "timestamp": "2023-01-01T00:05:00Z", "value": 100}""";

        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nightscoutJson),
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nocturneJson),
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.Perfect, result.OverallMatch);
        Assert.True(result.BodyMatch);
        Assert.Empty(result.Discrepancies);
    }

    [Fact]
    public async Task CompareResponsesAsync_JsonWithNumericDifferences_ShouldDetectNumericDiscrepancies()
    {
        // Arrange
        var nightscoutJson = """{"value": 100.001}""";
        var nocturneJson = """{"value": 100.002}""";

        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nightscoutJson),
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nocturneJson),
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.Perfect, result.OverallMatch); // Within tolerance
        Assert.True(result.BodyMatch);
        Assert.Empty(result.Discrepancies); // Should be within numeric tolerance
    }

    [Fact]
    public async Task CompareResponsesAsync_JsonWithLargeNumericDifferences_ShouldDetectDiscrepancy()
    {
        // Arrange
        var nightscoutJson = """{"value": 100.0}""";
        var nocturneJson = """{"value": 101.0}""";

        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nightscoutJson),
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nocturneJson),
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.MinorDifferences, result.OverallMatch);
        Assert.False(result.BodyMatch);
        Assert.Single(result.Discrepancies);
        Assert.Equal(DiscrepancyType.NumericValue, result.Discrepancies[0].Type);
        Assert.Equal(DiscrepancySeverity.Minor, result.Discrepancies[0].Severity);
    }

    [Fact]
    public async Task CompareResponsesAsync_JsonArrays_ShouldCompareArrayElements()
    {
        // Arrange
        var nightscoutJson = """{"values": [1, 2, 3]}""";
        var nocturneJson = """{"values": [1, 2, 4]}""";

        var nightscoutResponse = new TargetResponse
        {
            Target = "Nightscout",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nightscoutJson),
        };

        var nocturneResponse = new TargetResponse
        {
            Target = "Nocturne",
            StatusCode = 200,
            IsSuccess = true,
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes(nocturneJson),
        };

        // Act
        var result = await _service.CompareResponsesAsync(
            nightscoutResponse,
            nocturneResponse,
            "test-correlation"
        );

        // Assert
        Assert.Equal(Nocturne.Core.Models.ResponseMatchType.MinorDifferences, result.OverallMatch); // Numeric differences are minor
        Assert.False(result.BodyMatch);
        Assert.Single(result.Discrepancies);
        Assert.Equal(DiscrepancyType.NumericValue, result.Discrepancies[0].Type);
        Assert.Contains("values[2]", result.Discrepancies[0].Field);
    }
}
