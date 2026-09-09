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
using Nocturne.Core.Contracts.Entries;

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

        StubNothingStored();

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

        StubNothingStored();

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

        StubNothingStored();

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

        StubNothingStored();

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

        StubNothingStored();

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

        StubNothingStored();

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

        StubStoredAt((1000L, stored1), (2000L, stored2));

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

        StubStoredAt((2000L, storedDuplicate));

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

    /// <summary>
    /// The duplicate check finds nothing stored, so every submitted entry is written. Aligned with
    /// the batch probe the controller uses: one result per submitted entry, in the same order.
    /// </summary>
    [Fact]
    public async Task CreateEntries_LargeAllStoredBatch_ReportsTheUploaderOnce()
    {
        // The re-upload loop this exists to surface: a client re-sending a stored backlog every
        // cycle. Without a log line the next tenant in this state is only findable in
        // pg_stat_statements.
        var submitted = StoredBacklog(120);
        _controller.ControllerContext.HttpContext.Request.Headers.UserAgent = "Loop/57 CFNetwork Darwin";

        await _controller.CreateEntries(submitted);

        VerifyInformationLogged("re-sending stored readings", Times.Once());
        VerifyInformationLogged("Loop/57 CFNetwork Darwin", Times.Once());
    }

    [Fact]
    public async Task CreateEntries_ReuploadLine_StripsControlCharactersFromTheUserAgent()
    {
        // The only log sink is a line-oriented console exporter, so a control character in a
        // caller-supplied header forges log lines.
        var submitted = StoredBacklog(120);
        _controller.ControllerContext.HttpContext.Request.Headers.UserAgent =
            "Loop/57\r\nLogRecord.Body: forged";

        await _controller.CreateEntries(submitted);

        VerifyInformationLogged("Loop/57", Times.Once());
        VerifyInformationLogged("\n", Times.Never());
        VerifyInformationLogged("\r", Times.Never());
    }

    [Fact]
    public async Task CreateEntries_ReuploadLine_CapsAndFoldsAwkwardUserAgents()
    {
        // Length is capped so one uploader cannot write unbounded log lines, and format and
        // separator characters are folded as well as controls: a right-to-left override or a
        // U+2028 spoofs how a line reads without being a control character.
        var submitted = StoredBacklog(120);
        _controller.ControllerContext.HttpContext.Request.Headers.UserAgent =
            "Loop/57\u202e\u2028" + new string('x', 400);

        await _controller.CreateEntries(submitted);

        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("Loop/57")
                    && !v.ToString()!.Contains('\u202e')
                    && !v.ToString()!.Contains('\u2028')
                    && !v.ToString()!.Contains(new string('x', 250))),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once());
    }

    [Fact]
    public async Task CreateEntries_SmallAllStoredBatch_IsNotReported()
    {
        // A handful of duplicates is the normal overlap between an uploader's cycles.
        var submitted = StoredBacklog(20);

        await _controller.CreateEntries(submitted);

        VerifyInformationLogged("re-sending stored readings", Times.Never());
    }

    [Fact]
    public async Task CreateEntries_LargeBatchMostlyNew_IsNotReported()
    {
        var submitted = Enumerable.Range(0, 120)
            .Select(i => new Entry { Sgv = 100 + i, Mills = 1000 + i, Device = "Dexcom G7", Type = "sgv" })
            .ToArray();
        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns<IEnumerable<Entry>>(entries => entries);
        // Half stored: below the share that marks a re-upload loop.
        StubStoredAt(submitted.Take(60)
            .Select(e => (e.Mills, new Entry { Id = $"stored-{e.Mills}", Mills = e.Mills, Type = "sgv" }))
            .ToArray());
        _mockEntryService
            .Setup(x => x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<Entry> entries, WriteOrigin _, CancellationToken _) => entries.ToList());

        await _controller.CreateEntries(submitted);

        VerifyInformationLogged("re-sending stored readings", Times.Never());
    }

    /// <summary>
    /// A batch of <paramref name="count"/> entries the server already holds, wired through the
    /// processing and duplicate stubs.
    /// </summary>
    private Entry[] StoredBacklog(int count)
    {
        var submitted = Enumerable.Range(0, count)
            .Select(i => new Entry { Sgv = 100 + i, Mills = 1000 + i, Device = "Dexcom G7", Type = "sgv" })
            .ToArray();

        _mockDocumentProcessingService
            .Setup(x => x.ProcessDocuments(It.IsAny<IEnumerable<Entry>>()))
            .Returns<IEnumerable<Entry>>(entries => entries);
        StubStoredAt(submitted
            .Select(e => (e.Mills, new Entry { Id = $"stored-{e.Mills}", Mills = e.Mills, Type = "sgv" }))
            .ToArray());
        _mockEntryService
            .Setup(x => x.CreateEntriesAsync(It.IsAny<IEnumerable<Entry>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Entry>());

        return submitted;
    }

    private void VerifyInformationLogged(string fragment, Times times) =>
        _mockLogger.Verify(
            l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains(fragment)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    private void StubNothingStored() => StubStoredAt();

    /// <summary>
    /// The duplicate check reports the given entries as already stored, keyed by the submitted
    /// entry's <see cref="Entry.Mills"/>, and finds nothing for every other entry. Results are
    /// aligned with the submitted batch, as the batch probe the controller uses returns them.
    /// </summary>
    private void StubStoredAt(params (long Mills, Entry Stored)[] stored)
    {
        var byMills = stored.ToDictionary(x => x.Mills, x => x.Stored);
        _mockEntryService
            .Setup(x =>
                x.CheckForDuplicateEntriesAsync(
                    It.IsAny<IReadOnlyList<EntryDuplicateProbe>>(),
                    It.IsAny<int>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (IReadOnlyList<EntryDuplicateProbe> probes, int _, CancellationToken _) =>
                    probes
                        .Select(probe => byMills.GetValueOrDefault(probe.Mills))
                        .ToArray()
            );
    }

}
