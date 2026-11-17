using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.Core.Contracts.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.DeviceStatuses;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Models.V4;
using Nocturne.Core.Models.Queries;
using Xunit;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Comprehensive unit tests for TimeQueryService
/// Tests time-based query parsing, MongoDB filter building, and data operations
/// </summary>
public class TimeQueryServiceTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<ITreatmentService> _mockTreatmentService;
    private readonly Mock<IDeviceStatusService> _mockDeviceStatusService;
    private readonly Mock<IBraceExpansionService> _mockBraceExpansionService;
    private readonly Mock<ILogger<TimeQueryService>> _mockLogger;
    private readonly TimeQueryService _timeQueryService;

    public TimeQueryServiceTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockBraceExpansionService = new Mock<IBraceExpansionService>();
        _mockLogger = new Mock<ILogger<TimeQueryService>>();

        _mockDeviceStatusService = new Mock<IDeviceStatusService>();
        _mockDeviceStatusService
            .Setup(x => x.GetDeviceStatusesAsync(
                It.IsAny<FindQuery>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<ReadSortField>()))
            .ReturnsAsync([]);

        _timeQueryService = new TimeQueryService(
            _mockEntryService.Object,
            _mockTreatmentService.Object,
            _mockDeviceStatusService.Object,
            _mockBraceExpansionService.Object,
            _mockLogger.Object
        );
    }

    #region ExecuteTimeQueryAsync Tests - NotImplemented

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithValidParams_ReturnsResults()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T{13..15}:00:00";
        var fieldName = "dateString";

        // Setup mock for brace expansion service
        var timePatterns = new TimePatternQuery
        {
            CanOptimizeWithIndex = false,
            InPatterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            Patterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            FieldName = fieldName,
        };

        _mockBraceExpansionService
            .Setup(x =>
                x.PrepareTimePatterns(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())
            )
            .Returns(timePatterns);

        // Setup mock to return sample entries
        var expectedEntries = new List<Entry>
        {
            new Entry { Id = "1", Type = "sgv" },
            new Entry { Id = "2", Type = "sgv" },
        };
        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(expectedEntries);

        // Act
        var result = await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName
        );

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.Is<EntryQuery>(q => q.Type == null && q.Count == 1000 && q.Skip == 0 && q.DateString == null && !q.ReverseResults), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithDifferentStorageTypes_CallsCorrectMethod(
        string storageType
    )
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        _mockTreatmentService
            .Setup(x =>
                x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Treatment>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, storageType, fieldName);

        // Assert
        switch (storageType.ToLowerInvariant())
        {
            case "entries":
                _mockEntryService.Verify(
                    x =>
                        x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()),
                    Times.Once
                );
                break;
            case "treatments":
                _mockTreatmentService.Verify(
                    x =>
                        x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()),
                    Times.Once
                );
                break;
            case "devicestatus":
                _mockDeviceStatusService.Verify(
                    x =>
                        x.GetDeviceStatusesAsync(
                            It.IsAny<FindQuery>(), 1000, 0, It.IsAny<CancellationToken>(),
                            It.IsAny<bool>(), It.IsAny<ReadSortField>()),
                    Times.Once
                );
                break;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithInvalidStorageType_ThrowsArgumentException()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var invalidStorageType = "invalid";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, invalidStorageType, fieldName)
        );

        exception.Message.Should().Contain("Unsupported storage type: invalid");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithNullPrefix_HandlesGracefully()
    {
        // Arrange
        string? prefix = null;
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "T13:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockBraceExpansionService.Verify(
            x => x.PrepareTimePatterns(prefix, regex, fieldName),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithNullRegex_HandlesGracefully()
    {
        // Arrange
        var prefix = "2024-01";
        string? regex = null;
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockBraceExpansionService.Verify(
            x => x.PrepareTimePatterns(prefix, regex, fieldName),
            Times.Once
        );
    }

    #endregion

    #region ExecuteSliceQueryAsync Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithValidParams_CallsBraceExpansionService()
    {
        // Arrange
        var storage = "entries";
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T{13..15}:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockBraceExpansionService.Verify(
            x => x.PrepareTimePatterns(prefix, regex, field),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithNullType_SkipsTypeFilter()
    {
        // Arrange
        var storage = "entries";
        var field = "dateString";
        string? type = null;
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.Is<EntryQuery>(q => q.Find != null && !q.Find.Contains("find[type]")), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Theory]
    [InlineData("entries")]
    [InlineData("treatments")]
    [InlineData("devicestatus")]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithDifferentStorageTypes_CallsCorrectMethod(
        string storageType
    )
    {
        // Arrange
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        _mockTreatmentService
            .Setup(x =>
                x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Treatment>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storageType, field, type, prefix, regex);

        // Assert
        switch (storageType.ToLowerInvariant())
        {
            case "entries":
                _mockEntryService.Verify(
                    x =>
                        x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()),
                    Times.Once
                );
                break;
            case "treatments":
                _mockTreatmentService.Verify(
                    x =>
                        x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()),
                    Times.Once
                );
                break;
            case "devicestatus":
                _mockDeviceStatusService.Verify(
                    x =>
                        x.GetDeviceStatusesAsync(
                            It.IsAny<FindQuery>(), 1000, 0, It.IsAny<CancellationToken>(),
                            It.IsAny<bool>(), It.IsAny<ReadSortField>()),
                    Times.Once
                );
                break;
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithInvalidStorageType_ThrowsArgumentException()
    {
        // Arrange
        var storage = "invalid";
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex)
        );

        exception.Message.Should().Contain("Unsupported storage type: invalid");
    }

    #endregion

    #region GenerateTimeQueryEcho Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_WithMultiplePatterns_MatchesRegexAlternatives()
    {
        // Arrange
        var patterns = new[] { "^2024-01T13:00:00", "^2024-01T14:00:00" };
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns("2024-01", "T{13..14}:00:00", "dateString"))
            .Returns(
                new TimePatternQuery
                {
                    Patterns = patterns,
                    InPatterns = patterns,
                    CanOptimizeWithIndex = false,
                }
            );

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho("2024-01", "T{13..14}:00:00");
        var echoConditions = FindQuery.Parse(JsonSerializer.Serialize(result.Query));

        // Assert
        echoConditions.Matches(new Entry { DateString = "2024-01T13:00:00.000Z" }).Should().BeTrue();
        echoConditions.Matches(new Entry { DateString = "2024-01T14:00:00.000Z" }).Should().BeTrue();
        echoConditions.Matches(new Entry { DateString = "2024-01T15:00:00.000Z" }).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_KeepsRawParametersSeparateFromAppliedFind()
    {
        // Arrange
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(null, null, "dateString"))
            .Returns(new TimePatternQuery { Patterns = [], InPatterns = [] });
        const string query = "?storage=entries&field=dateString&type=mbg&device=ignored"
            + "&token=x&secret=y&_=1&count=10&sort=dateString"
            + "&find[type]=sgv&find[device]=001";

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(null, null, queryString: query);

        // Assert
        result.Req.Query["token"].Should().Be("x");
        result.Req.Query["secret"].Should().Be("y");
        var echoConditions = FindQuery.Parse(JsonSerializer.Serialize(result.Query));
        echoConditions.Matches(new Entry { Type = "sgv", Device = "001" }).Should().BeTrue();
        echoConditions.Matches(new Entry { Type = "sgv", Device = "1" }).Should().BeFalse();
        echoConditions.Matches(new Entry { Type = "mbg", Device = "001" }).Should().BeFalse();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Parity]
    [InlineData(null, new[] { "" }, true, "{\"find\":{\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}")]
    [InlineData("entries", new[] { "" }, true, "{\"find\":{\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}")]
    [InlineData("2024-{01,02}", new[] { "^2024-01", "^2024-02" }, false, "{\"find\":{\"dateString\":{\"$in\":[{},{}]}}}")]
    public void GenerateTimeQueryEcho_RequestQueryCarriesMergedFindWithRegExpsAsEmptyObjects(
        string? prefix, string[] patterns, bool singleRegex, string expected)
    {
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, null, "dateString"))
            .Returns(new TimePatternQuery
            {
                Patterns = patterns,
                InPatterns = patterns,
                CanOptimizeWithIndex = singleRegex,
            });

        var result = _timeQueryService.GenerateTimeQueryEcho(prefix, null);

        JsonSerializer.Serialize(result.Req.Query).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_RequestQueryNestsCallerFindBesideTimePatternAndDropsRawKeys()
    {
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns("entries", null, "dateString"))
            .Returns(new TimePatternQuery
            {
                Patterns = [""],
                InPatterns = [""],
                CanOptimizeWithIndex = true,
            });

        var result = _timeQueryService.GenerateTimeQueryEcho(
            "entries",
            null,
            queryString: "?count=5&find[type]=sgv&find[sgv][$gte]=100"
        );

        JsonSerializer.Serialize(result.Req.Query).Should().Be(
            "{\"count\":\"5\",\"find\":{\"type\":\"sgv\",\"sgv\":{\"$gte\":\"100\"},"
                + "\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}");
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Parity]
    [InlineData("?find[dateString][$gte]=2024", "{\"find\":{\"dateString\":{\"$gte\":\"2024\",\"$in\":[{}]}}}")]
    [InlineData("?find[dateString][$regex]=T10", "{\"find\":{\"dateString\":{\"$regex\":\"T10\",\"$in\":[{}]}}}")]
    [InlineData("?find[$or][0][type]=sgv", "{\"find\":{\"$or\":[{\"type\":\"sgv\"}],\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}")]
    [InlineData("?find[]=sgv", "{\"find\":{\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}")]
    [InlineData("?find[type][$in][]=sgv", "{\"find\":{\"type\":{\"$in\":[\"sgv\"]},\"dateString\":{\"$in\":[{}],\"$regex\":{}}}}")]
    public void GenerateTimeQueryEcho_RequestQueryMergesTimePatternAsUpstreamDoes(string queryString, string expected)
    {
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns("entries", null, "dateString"))
            .Returns(new TimePatternQuery
            {
                Patterns = [""],
                InPatterns = [""],
                CanOptimizeWithIndex = true,
            });

        var result = _timeQueryService.GenerateTimeQueryEcho("entries", null, queryString: queryString);

        JsonSerializer.Serialize(result.Req.Query).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GenerateTimeQueryEcho_UnsupportedOrBranchFailsClosedWithoutRequiringAnId()
    {
        // Arrange
        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(null, null, "dateString"))
            .Returns(new TimePatternQuery { Patterns = [], InPatterns = [] });

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(
            null,
            null,
            queryString: "?find[$or][0][sgv][$unknown]=100&find[$or][1][type]=mbg"
        );
        var echoConditions = FindQuery.Parse(JsonSerializer.Serialize(result.Query));

        // Assert
        echoConditions.Matches(new { type = "sgv" }).Should().BeFalse();
        echoConditions.Matches(new { _id = "a", type = "sgv" }).Should().BeFalse();
        echoConditions.Matches(new { _id = (string?)null, type = "sgv" }).Should().BeFalse();
        echoConditions.Matches(new { type = "mbg" }).Should().BeTrue();
        echoConditions.Matches(new { _id = "f", type = "mbg" }).Should().BeTrue();
    }

    #endregion

    #region Query Parameter Processing Tests

    #endregion

    #region Edge Cases and Error Handling Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithEmptyPatterns_HandlesGracefully()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = Array.Empty<string>(),
            FieldName = fieldName,
            InPatterns = Array.Empty<string>(),
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithCancellationToken_PassesToMongoDb()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var cancellationToken = new CancellationToken();
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), cancellationToken)
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            null,
            cancellationToken
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), cancellationToken),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithCancellationToken_PassesToMongoDb()
    {
        // Arrange
        var storage = "entries";
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var cancellationToken = new CancellationToken();
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), cancellationToken)
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(
            storage,
            field,
            type,
            prefix,
            regex,
            null,
            cancellationToken
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), cancellationToken),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_StorageTypeCaseInsensitive_WorksCorrectly()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var storageType = "ENTRIES"; // Uppercase
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, storageType, fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.QueryEntriesAsync(It.IsAny<EntryQuery>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_StorageTypeCaseInsensitive_WorksCorrectly()
    {
        // Arrange
        var storage = "TREATMENTS"; // Uppercase
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = field,
            InPatterns = new[] { "2024-01T13:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, field))
            .Returns(expectedPatterns);

        _mockTreatmentService
            .Setup(x =>
                x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new List<Treatment>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockTreatmentService.Verify(
            x =>
                x.QueryTreatmentsAsync(It.IsAny<TreatmentQuery>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    #endregion
}
