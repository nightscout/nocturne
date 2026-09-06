using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Models;
using Nocturne.Core.Models.Extensions;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// Unit tests for EntriesController
/// </summary>
public class EntriesControllerTests
{
    private readonly Mock<IEntryService> _mockEntryService;
    private readonly Mock<IDocumentProcessingService> _mockDocumentProcessingService;
    private readonly Mock<IProcessingStatusService> _mockProcessingStatusService;
    private readonly Mock<ICanonicalAlertEvaluator> _mockAlertEvaluator;
    private readonly Mock<ILogger<EntriesController>> _mockLogger;
    private readonly EntriesController _controller;

    public EntriesControllerTests()
    {
        _mockEntryService = new Mock<IEntryService>();
        _mockDocumentProcessingService = new Mock<IDocumentProcessingService>();
        _mockProcessingStatusService = new Mock<IProcessingStatusService>();
        _mockAlertEvaluator = new Mock<ICanonicalAlertEvaluator>();
        _mockLogger = new Mock<ILogger<EntriesController>>();

        _controller = new EntriesController(
            _mockEntryService.Object,
            _mockDocumentProcessingService.Object,
            _mockProcessingStatusService.Object,
            _mockAlertEvaluator.Object,
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
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

    [Fact]
    public async Task CreateEntriesAsync_DerivesSameUtcMillsAsSyncEndpoint()
    {
        // A date-bearing entry must resolve to the same UTC mills on both the sync and async
        // endpoints — the conversion lives in Entry.Mills (UTC), not in the controller, so the two
        // endpoints can never diverge by timezone again.
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
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(new[] { new Entry { Id = "created-id", Sgv = 120 } });

        // Act
        await _controller.CreateEntriesAsync(entryWithDate);

        // Assert: identical UTC mills to the sync endpoint, regardless of server time zone.
        processedInput.Should().NotBeNull();
        processedInput.Should().HaveCount(1);
        processedInput![0].Mills.Should().Be(1686565800000);
    }

    [Fact]
    public async Task CreateEntries_AllDuplicates_EchoesStoredEntriesWithSameCount()
    {
        // v1 uploaders (Loop's NightscoutKit) require one response object per submitted
        // entry; an all-duplicate batch must echo the stored entries, not return [].
        var submitted = new[]
        {
            new Entry { Sgv = 164, Mills = 1000, Device = "Dexcom G7" },
            new Entry { Sgv = 158, Mills = 2000, Device = "Dexcom G7" },
        };
        var stored1 = new Entry { Id = "stored-1", Sgv = 164, Mills = 1000, Device = "Dexcom G7", Type = "sgv" };
        var stored2 = new Entry { Id = "stored-2", Sgv = 158, Mills = 2000, Device = "Dexcom G7", Type = "sgv" };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<double?>(),
                    1000L,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(stored1);
        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<double?>(),
                    2000L,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(stored2);

        List<Entry>? createInput = null;
        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
            )
            .Callback<IEnumerable<Entry>, WriteOrigin, CancellationToken>((entries, _, _) => createInput = entries.ToList())
            .ReturnsAsync(Array.Empty<Entry>());

        // Act
        var result = await _controller.CreateEntries(submitted);

        // Assert
        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);

        var body = objectResult
            .Value.Should()
            .BeAssignableTo<IEnumerable<object>>()
            .Subject.Cast<EntryV1Response>()
            .ToList();
        body.Should().HaveCount(2);
        body[0].Id.Should().Be("stored-1");
        body[1].Id.Should().Be("stored-2");

        // Nothing new is written for an all-duplicate batch
        createInput.Should().NotBeNull();
        createInput.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateEntry_AcceptsIdGeneratedByCreateEndpoint()
    {
        var generatedId = Guid.CreateVersion7().ToString("N");
        var update = new Entry { Sgv = 123, Mills = 1686565800000 };

        _mockEntryService
            .Setup(x =>
                x.UpdateEntryAsync(
                    generatedId,
                    It.Is<Entry>(entry => entry.Id == generatedId),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((string _, Entry entry, CancellationToken _) => entry);

        var result = await _controller.UpdateEntry(generatedId, update);

        result.Result.Should().BeOfType<OkObjectResult>();
        _mockEntryService.Verify(
            x =>
                x.UpdateEntryAsync(
                    generatedId,
                    It.Is<Entry>(entry => entry.Id == generatedId),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task DeleteEntry_AcceptsIdGeneratedByCreateEndpoint()
    {
        var generatedId = Guid.CreateVersion7().ToString("N");

        _mockEntryService
            .Setup(x => x.DeleteEntryAsync(generatedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await _controller.DeleteEntry(generatedId);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateEntries_MixedDuplicateAndNew_EchoesOneResponsePerSubmittedEntry()
    {
        var submitted = new[]
        {
            new Entry { Sgv = 120, Mills = 1000, Device = "Dexcom G7" },
            new Entry { Sgv = 130, Mills = 2000, Device = "Dexcom G7" }, // duplicate of a stored entry
            new Entry { Sgv = 140, Mills = 3000, Device = "Dexcom G7" },
        };
        var storedDuplicate = new Entry { Id = "stored-dup", Sgv = 130, Mills = 2000, Device = "Dexcom G7", Type = "sgv" };

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns<IEnumerable<Entry>>(entries => entries);

        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<double?>(),
                    It.IsAny<long>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync((Entry?)null);
        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntryAsync(
                    It.IsAny<string?>(),
                    It.IsAny<string>(),
                    It.IsAny<double?>(),
                    2000L,
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(storedDuplicate);

        List<Entry>? createInput = null;
        _mockEntryService
            .Setup(x =>
                x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>())
            )
            .Callback<IEnumerable<Entry>, WriteOrigin, CancellationToken>((entries, _, _) => createInput = entries.ToList())
            .ReturnsAsync((IEnumerable<Entry> entries, WriteOrigin _, CancellationToken _) => entries.ToList());

        // Act
        var result = await _controller.CreateEntries(submitted);

        // Assert
        var objectResult = result.Result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);

        var body = objectResult
            .Value.Should()
            .BeAssignableTo<IEnumerable<object>>()
            .Subject.Cast<EntryV1Response>()
            .ToList();

        // One response object per submitted entry, in submission order; the duplicate
        // slot carries the stored entry's _id, not the resubmitted copy's.
        body.Should().HaveCount(3);
        body[0].Mills.Should().Be(1000);
        body[1].Id.Should().Be("stored-dup");
        body[2].Mills.Should().Be(3000);
        body[0].Id.Should().NotBeNullOrEmpty();
        body[2].Id.Should().NotBeNullOrEmpty();

        // Only the two non-duplicates are written
        createInput.Should().NotBeNull();
        createInput!.Select(e => e.Mills).Should().Equal(1000, 3000);
    }
}
