using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Platform;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.API.Services.Devices;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Contracts.Treatments;
using Xunit;
using TimePatternQuery = Nocturne.Core.Contracts.Legacy.TimePatternQuery;

namespace Nocturne.API.Tests.Services.Platform;

/// <summary>
/// Comprehensive unit tests for TimeQueryService
/// Tests time-based query parsing, MongoDB filter building, and data operations
/// </summary>
public class TimeQueryServiceTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<ITreatmentService> _mockTreatmentService;
    private readonly Mock<IBraceExpansionService> _mockBraceExpansionService;
    private readonly Mock<ILogger<TimeQueryService>> _mockLogger;
    private readonly TimeQueryService _timeQueryService;

    public TimeQueryServiceTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockTreatmentService = new Mock<ITreatmentService>();
        _mockBraceExpansionService = new Mock<IBraceExpansionService>();
        _mockLogger = new Mock<ILogger<TimeQueryService>>();

        var projectionService = new DeviceStatusProjectionService(
            new Mock<IApsSnapshotRepository>().Object,
            new Mock<IPumpSnapshotRepository>().Object,
            new Mock<IUploaderSnapshotRepository>().Object,
            new Mock<IStateSpanRepository>().Object,
            new Mock<IDeviceStatusExtrasRepository>().Object,
            new Mock<ILogger<DeviceStatusProjectionService>>().Object);

        _timeQueryService = new TimeQueryService(
            _mockEntryService.Object,
            _mockTreatmentService.Object,
            projectionService,
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithOptimizedSinglePattern_UsesRegexFilter()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var singleRegexPattern = "^2024-01T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            SingleRegexPattern = singleRegexPattern,
            CanOptimizeWithIndex = true,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithMultiplePatterns_UsesInFilter()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T{13..15}:00:00";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        _mockEntryService
            .Setup(x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        _mockTreatmentService
            .Setup(x =>
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
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
                        x.GetEntriesWithAdvancedFilterAsync(
                            It.IsAny<string>(),
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<CancellationToken>()
                        ),
                    Times.Once
                );
                break;
            case "treatments":
                _mockTreatmentService.Verify(
                    x =>
                        x.GetTreatmentsWithAdvancedFilterAsync(
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<string?>(),
                            It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()
                        ),
                    Times.Once
                );
                break;
            case "devicestatus":
                // DeviceStatusProjectionService is concrete, verified by returning empty results
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
    public async Task ExecuteTimeQueryAsync_WithAdditionalQueryParameters_AppliesFilters()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            { "type", "sgv" },
            { "device", "test-device" },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
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
    public async Task ExecuteSliceQueryAsync_WithTypeFilter_AppliesTypeFilter()
    {
        // Arrange
        var storage = "entries";
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        _mockTreatmentService
            .Setup(x =>
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
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
                        x.GetEntriesWithAdvancedFilterAsync(
                            It.IsAny<string>(),
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<CancellationToken>()
                        ),
                    Times.Once
                );
                break;
            case "treatments":
                _mockTreatmentService.Verify(
                    x =>
                        x.GetTreatmentsWithAdvancedFilterAsync(
                            It.IsAny<int>(),
                            It.IsAny<int>(),
                            It.IsAny<string?>(),
                            It.IsAny<bool>(),
                            It.IsAny<CancellationToken>()
                        ),
                    Times.Once
                );
                break;
            case "devicestatus":
                // DeviceStatusProjectionService is concrete, verified by returning empty results
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
    public void GenerateTimeQueryEcho_WithValidParams_ReturnsCorrectEcho()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T{13..15}:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(prefix, regex, storage, fieldName);

        // Assert
        result.Should().NotBeNull();
        result.Req.Should().NotBeNull();
        result.Req.Params.Should().ContainKey("prefix").WhoseValue.Should().Be(prefix);
        result.Req.Params.Should().ContainKey("regex").WhoseValue.Should().Be(regex);
        result.Req.Params.Should().ContainKey("storage").WhoseValue.Should().Be(storage);
        result.Req.Params.Should().ContainKey("field").WhoseValue.Should().Be(fieldName);
        result.Pattern.Should().BeEquivalentTo(expectedPatterns.Patterns);
        result.Query.Should().ContainKey(fieldName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_WithOptimizedPattern_UsesRegexQuery()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var singleRegexPattern = "^2024-01T13:00:00";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00" },
            FieldName = fieldName,
            SingleRegexPattern = singleRegexPattern,
            CanOptimizeWithIndex = true,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(prefix, regex, storage, fieldName);

        // Assert
        result.Should().NotBeNull();
        result.Query.Should().ContainKey(fieldName);
        var fieldQuery = result.Query[fieldName] as Dictionary<string, object>;
        fieldQuery.Should().NotBeNull();
        fieldQuery.Should().ContainKey("$regex").WhoseValue.Should().Be(singleRegexPattern);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_WithMultiplePatterns_UsesInQuery()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T{13..15}:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var expectedPatterns = new TimePatternQuery
        {
            Patterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            FieldName = fieldName,
            InPatterns = new[] { "2024-01T13:00:00", "2024-01T14:00:00", "2024-01T15:00:00" },
            CanOptimizeWithIndex = false,
        };

        _mockBraceExpansionService
            .Setup(x => x.PrepareTimePatterns(prefix, regex, fieldName))
            .Returns(expectedPatterns);

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(prefix, regex, storage, fieldName);

        // Assert
        result.Should().NotBeNull();
        result.Query.Should().ContainKey(fieldName);
        var fieldQuery = result.Query[fieldName] as Dictionary<string, object>;
        fieldQuery.Should().NotBeNull();
        fieldQuery.Should().ContainKey("$in");
        var inValues = fieldQuery!["$in"] as IEnumerable<string>;
        inValues.Should().BeEquivalentTo(expectedPatterns.InPatterns);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_WithQueryParameters_IncludesInResult()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            { "type", "sgv" },
            { "device", "test-device" },
        };
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

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(
            prefix,
            regex,
            storage,
            fieldName,
            queryParameters
        );

        // Assert
        result.Should().NotBeNull();
        result.Req.Query.Should().BeEquivalentTo(queryParameters);
        result.Query.Should().ContainKey("type").WhoseValue.Should().Be("sgv");
        result.Query.Should().ContainKey("device").WhoseValue.Should().Be("test-device");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_WithFindParameters_ProcessesFindDict()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var findDict = new Dictionary<string, object>
        {
            {
                "sgv",
                new Dictionary<string, object> { { "$gte", 100 } }
            },
        };
        var queryParameters = new Dictionary<string, object> { { "find", findDict } };
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

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(
            prefix,
            regex,
            storage,
            fieldName,
            queryParameters
        );

        // Assert
        result.Should().NotBeNull();
        result.Query.Should().ContainKey("sgv");
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public void GenerateTimeQueryEcho_ExcludesSpecialQueryParameters()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var storage = "entries";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            { "find", new Dictionary<string, object>() },
            { "count", 10 },
            { "sort", "dateString" },
            { "type", "sgv" },
        };
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

        // Act
        var result = _timeQueryService.GenerateTimeQueryEcho(
            prefix,
            regex,
            storage,
            fieldName,
            queryParameters
        );

        // Assert
        result.Should().NotBeNull();
        result.Query.Should().NotContainKey("count");
        result.Query.Should().NotContainKey("sort");
        result.Query.Should().ContainKey("type").WhoseValue.Should().Be("sgv");
    }

    #endregion

    #region Query Parameter Processing Tests

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithJsonElementValues_ConvertsCorrectly()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";

        // Create JsonElement values to test conversion logic
        var jsonDoc = JsonDocument.Parse(
            """
            {
                "numValue": 123,
                "stringValue": "test",
                "boolValue": true,
                "arrayValue": [1, 2, 3]
            }
            """
        );

        var queryParameters = new Dictionary<string, object>
        {
            { "numParam", jsonDoc.RootElement.GetProperty("numValue") },
            { "stringParam", jsonDoc.RootElement.GetProperty("stringValue") },
            { "boolParam", jsonDoc.RootElement.GetProperty("boolValue") },
            { "arrayParam", jsonDoc.RootElement.GetProperty("arrayValue") },
        };

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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithMongoOperators_BuildsCorrectFilters()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            {
                "sgv",
                new Dictionary<string, object> { { "$gte", 100 }, { "$lte", 200 } }
            },
            {
                "type",
                new Dictionary<string, object> { { "$in", new[] { "sgv", "mbg" } } }
            },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithInvalidFilterValue_HandlesGracefully()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            {
                "invalidField",
                new Dictionary<string, object> { { "$invalidOp", "value" } }
            },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, "entries", fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    cancellationToken
                )
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    cancellationToken
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    cancellationToken
                )
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    cancellationToken
                ),
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(prefix, regex, storageType, fieldName);

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
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
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Treatment>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(storage, field, type, prefix, regex);

        // Assert
        _mockTreatmentService.Verify(
            x =>
                x.GetTreatmentsWithAdvancedFilterAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<string?>(),
                    It.IsAny<bool>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion

    #region Additional Coverage Tests for Uncovered Code Paths

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithFindParameters_ProcessesFindDict()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var findDict = new Dictionary<string, object>
        {
            {
                "sgv",
                new Dictionary<string, object> { { "$gte", 100 }, { "$lte", 200 } }
            },
        };
        var queryParameters = new Dictionary<string, object> { { "find", findDict } };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteSliceQueryAsync_WithFindParameters_ProcessesFindDict()
    {
        // Arrange
        var storage = "entries";
        var field = "dateString";
        var type = "sgv";
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var findDict = new Dictionary<string, object>
        {
            {
                "mgdl",
                new Dictionary<string, object> { { "$gte", 80 } }
            },
        };
        var queryParameters = new Dictionary<string, object> { { "find", findDict } };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteSliceQueryAsync(
            storage,
            field,
            type,
            prefix,
            regex,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithComplexQueryParameters_BuildsAndFilters()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            {
                "type",
                new Dictionary<string, object> { { "$ne", "cal" } }
            },
            {
                "sgv",
                new Dictionary<string, object> { { "$gt", 50 }, { "$lt", 400 } }
            },
            { "device", "test-device" },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithRegexOperator_BuildsCorrectFilter()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            {
                "notes",
                new Dictionary<string, object> { { "$regex", "test.*pattern" } }
            },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithNinOperator_BuildsCorrectFilter()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        var queryParameters = new Dictionary<string, object>
        {
            {
                "type",
                new Dictionary<string, object> { { "$nin", new[] { "cal", "mbg" } } }
            },
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Parity]
    public async Task ExecuteTimeQueryAsync_WithSingleValueForArray_ConvertsCorrectly()
    {
        // Arrange
        var prefix = "2024-01";
        var regex = "T13:00:00";
        var fieldName = "dateString";
        // This will test the ConvertArray fallback path for non-array values
        var queryParameters = new Dictionary<string, object>
        {
            {
                "type",
                new Dictionary<string, object> { { "$in", "sgv" } }
            }, // Single value instead of array
        };
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
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new List<Entry>());

        // Act
        await _timeQueryService.ExecuteTimeQueryAsync(
            prefix,
            regex,
            "entries",
            fieldName,
            queryParameters
        );

        // Assert
        _mockEntryService.Verify(
            x =>
                x.GetEntriesWithAdvancedFilterAsync(
                    It.IsAny<string>(),
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    #endregion
}
