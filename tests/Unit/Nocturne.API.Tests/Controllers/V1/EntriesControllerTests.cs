using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Unit tests for EntriesController
/// </summary>
public class EntriesControllerTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<IDataFormatService> _mockDataFormatService;
    private readonly Mock<IDocumentProcessingService> _mockDocumentProcessingService;
    private readonly Mock<IProcessingStatusService> _mockProcessingStatusService;
    private readonly Mock<IAlertOrchestrator> _mockAlertOrchestrator;
    private readonly Mock<ILogger<EntriesController>> _mockLogger;
    private readonly EntriesController _controller;

    public EntriesControllerTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockDataFormatService = new Mock<IDataFormatService>();
        _mockDocumentProcessingService = new Mock<IDocumentProcessingService>();
        _mockProcessingStatusService = new Mock<IProcessingStatusService>();
        _mockAlertOrchestrator = new Mock<IAlertOrchestrator>();
        _mockLogger = new Mock<ILogger<EntriesController>>();

        _controller = new EntriesController(
            _mockEntryService.Object,
            _mockDataFormatService.Object,
            _mockDocumentProcessingService.Object,
            _mockProcessingStatusService.Object,
            _mockAlertOrchestrator.Object,
            _mockLogger.Object
        );

        // Setup controller context
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
    }

    [Fact]
    public async Task CreateEntries_ProcessesValidEntriesNotRawEntries()
    {
        // Arrange
        var rawEntry = new Entry
        {
            // No ID, no mills, no dateString - these should be set by validation
            Sgv = 120,
            // Type intentionally omitted so controller can default to "sgv"
        };

        var expectedProcessedEntry = new Entry
        {
            Id = "generated-id",
            Mills = 1234567890000,
            DateString = "2023-06-12T10:30:00.000Z",
            Sgv = 120,
            Type = "sgv",
        };

        // Track what gets passed to ProcessDocuments
        List<Entry>? processedInput = null;
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Callback<IEnumerable<Entry>>(entries => processedInput = entries.ToList())
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new[] { expectedProcessedEntry });

        // Act
        var result = await _controller.CreateEntries(rawEntry);

        // Assert
        result.Should().NotBeNull();
        var statusCodeResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        statusCodeResult.StatusCode.Should().Be(201);

        // Verify ProcessDocuments was called with validEntries (which have IDs set)
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(1);

        // The entry passed to ProcessDocuments should have:
        // - A generated ID (not null/empty)
        // - Type defaulted to "sgv"
        var entryPassedToProcess = processedInput![0];
        entryPassedToProcess.Id.Should().NotBeNullOrEmpty();
        entryPassedToProcess.Type.Should().Be("sgv");

        // Verify ProcessDocuments was called exactly once
        _mockDocumentProcessingService.Verify(
            x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateEntries_WithMultipleEntries_ProcessesValidatedEntriesWithModifications()
    {
        // Arrange
        var rawEntries = new[]
        {
            new Entry { Sgv = 120 }, // No ID, should get one
            new Entry { Sgv = 0 }, // Invalid - no meaningful data, should be filtered out
            new Entry { Sgv = 150, Mills = 1234567890000 }, // Has mills, should get ID and dateString
        };

        // Track what gets passed to ProcessDocuments
        List<Entry>? processedInput = null;
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Callback<IEnumerable<Entry>>(entries => processedInput = entries.ToList())
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new[]
                {
                    new Entry { Id = "1", Sgv = 120 },
                    new Entry { Id = "2", Sgv = 150 },
                }
            );

        // Act
        var result = await _controller.CreateEntries(rawEntries);

        // Assert
        result.Should().NotBeNull();

        // Verify ProcessDocuments was called with only valid entries (2 out of 3)
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(2); // Invalid entry should be filtered out

        // All entries passed to ProcessDocuments should have IDs and proper defaults
        processedInput!.All(e => !string.IsNullOrEmpty(e.Id)).Should().BeTrue();
        processedInput.All(e => e.Type == "sgv").Should().BeTrue();

        // The entry with Mills should have DateString set
        var entryWithMills = processedInput.First(e => e.Mills == 1234567890000);
        entryWithMills.DateString.Should().NotBeNullOrEmpty();

        // Verify ProcessDocuments was called exactly once
        _mockDocumentProcessingService.Verify(
            x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateEntries_WithMixedValidAndInvalidEntries_ProcessesOnlyValidOnes()
    {
        // Arrange - Mix of valid and invalid entries
        var mixedEntries = new[]
        {
            new Entry { Sgv = 120 }, // Valid
            new Entry { Type = "cal" }, // Valid - non-sgv type
        };

        // Track what gets passed to ProcessDocuments
        List<Entry>? processedInput = null;
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Callback<IEnumerable<Entry>>(entries => processedInput = entries.ToList())
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new[]
                {
                    new Entry { Id = "1", Sgv = 120 },
                    new Entry { Id = "2", Type = "cal" },
                }
            );

        // Act
        var result = await _controller.CreateEntries(mixedEntries);

        // Assert
        result.Should().NotBeNull();

        // Verify ProcessDocuments was called with validated entries (2 valid entries)
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(2);

        // All entries passed to ProcessDocuments should have IDs and proper types
        processedInput!.All(e => !string.IsNullOrEmpty(e.Id)).Should().BeTrue();
        processedInput.First(e => e.Sgv == 120).Type.Should().Be("sgv"); // Default type
        processedInput.First(e => e.Type == "cal").Type.Should().Be("cal"); // Preserved type

        // Verify ProcessDocuments was called exactly once
        _mockDocumentProcessingService.Verify(
            x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()),
            Times.Once
        );
    }

    [Fact]
    public async Task CreateEntries_EnsuresIDsGeneratedBeforeProcessing()
    {
        // Arrange
        var entryWithoutId = new Entry { Sgv = 120 };

        List<Entry>? processedInput = null;
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Callback<IEnumerable<Entry>>(entries => processedInput = entries.ToList())
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new[]
                {
                    new Entry { Id = "created-id", Sgv = 120 },
                }
            );

        // Act
        var result = await _controller.CreateEntries(entryWithoutId);

        // Assert
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(1);

        // The entry passed to ProcessDocuments should have an ID
        var entry = processedInput![0];
        entry.Id.Should().NotBeNullOrEmpty();

        // The ID should be a valid GUID-like string (hex characters, 32 chars without dashes)
        entry.Id.Should().MatchRegex("^[a-f0-9]{32}$");
    }

    [Fact]
    public async Task CreateEntries_EnsuresTimestampsSetBeforeProcessing()
    {
        // Arrange
        var entryWithDate = new Entry
        {
            Sgv = 120,
            Date = DateTimeOffset.Parse("2023-06-12T10:30:00.000Z").DateTime,
        };

        List<Entry>? processedInput = null;
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Callback<IEnumerable<Entry>>(entries => processedInput = entries.ToList())
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);

        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new[]
                {
                    new Entry { Id = "created-id", Sgv = 120 },
                }
            );

        // Act
        var result = await _controller.CreateEntries(entryWithDate);

        // Assert
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(1);

        var entry = processedInput![0];

        // Mills should be set from Date
        entry.Mills.Should().NotBe(0);
        entry.Mills.Should().Be(1686565800000);

        // DateString should be set from Mills
        entry.DateString.Should().NotBeNullOrEmpty();
        entry.DateString.Should().Contain("2023-06-12");
    }
}
