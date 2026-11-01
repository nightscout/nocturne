using Nocturne.API.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Nocturne.API.Services.Treatments;
using Nocturne.Core.Contracts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Treatments;

public class TreatmentReadServiceTests
{
    private readonly Mock<IV4ToLegacyProjectionService> _projection = new();
    private readonly Mock<IPerTreatmentDecomposer> _decomposer = new Mock<IPerTreatmentDecomposer>().AnswersBatchPerTreatment();
    private readonly Mock<ITempBasalRepository> _tempBasalRepo = new();
    private readonly Mock<IBolusRepository> _bolusRepo = new();
    private readonly Mock<ICarbIntakeRepository> _carbIntakeRepo = new();
    private readonly Mock<IBGCheckRepository> _bgCheckRepo = new();
    private readonly Mock<INoteRepository> _noteRepo = new();
    private readonly Mock<IDeviceEventRepository> _deviceEventRepo = new();
    private readonly Mock<IBolusCalculationRepository> _bolusCalcRepo = new();
    private readonly TreatmentReadService _service;

    public TreatmentReadServiceTests()
    {
        LegacyTreatmentRepositoryMockExtensions.AnswerServedIdsPerKey(
            _tempBasalRepo, _bolusRepo, _carbIntakeRepo, _bgCheckRepo, _noteRepo, _deviceEventRepo, _bolusCalcRepo);
        _projection.AnswersSpanIdsPerId();
        _service = new TreatmentReadService(
            _projection.Object,
            _decomposer.Object,
            _tempBasalRepo.Object,
            _bolusRepo.Object,
            _carbIntakeRepo.Object,
            _bgCheckRepo.Object,
            _noteRepo.Object,
            _deviceEventRepo.Object,
            _bolusCalcRepo.Object,
            new Mock<IProfileActivationRepository>().AnswersServedIdsPerKey<IProfileActivationRepository, ProfileActivation>().Object,
            Mock.Of<IStateSpanService>(),
            NullLogger<TreatmentReadService>.Instance);
    }

    [Fact]
    public async Task QueryAsync_DelegatesToProjection()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 1000 },
            new() { Id = "2", Mills = 2000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, 10, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 10 });

        result.Should().HaveCount(2);
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(null, null, 10, It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task QueryAsync_AppliesSkipAndTake()
    {
        var treatments = new List<Treatment>
        {
            new() { Id = "1", Mills = 3000 },
            new() { Id = "2", Mills = 2000 },
            new() { Id = "3", Mills = 1000 },
        };

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(treatments);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 1, Skip = 1 });

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("2");
    }

    private void StoreHoldingTreatmentsAt(params long[] mills)
    {
        var all = mills.Select(m => new Treatment { Id = m.ToString(), Mills = m }).ToList();

        _projection
            .Setup(p => p.GetProjectedTreatmentsAsync(null, null, It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long? _, long? _, int limit, bool descending, CancellationToken _) =>
                (descending ? all.OrderByDescending(t => t.Mills) : all.OrderBy(t => t.Mills)).Take(limit).ToList());
    }

    private List<long> ServeModifiedSince(IReadOnlyList<Treatment> all)
    {
        var cursors = new List<long>();
        _projection
            .Setup(p => p.GetProjectedTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((long cursor, int limit, CancellationToken _) =>
            {
                cursors.Add(cursor);
                return all.Where(t => t.SrvModified > cursor).OrderBy(t => t.SrvModified).Take(limit).ToList();
            });
        return cursors;
    }

    [Fact]
    public async Task QueryAsync_SrvModifiedAfterACursor_ReadsOnlyTheModifiedSincePages_AndNeverTheTimeWindow()
    {
        var all = Enumerable.Range(1, 1200)
            .Select(i => new Treatment { Id = i.ToString(), Mills = 5000 - i, SrvModified = 10_000 + i })
            .ToList();
        var cursors = ServeModifiedSince(all);

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[srvModified][$gt]=10100",
            Count = 3,
            Skip = 1,
            ReverseResults = true,
            SortField = ReadSortField.SrvModified,
        });

        result.Select(t => t.SrvModified).Should().Equal(10_102L, 10_103L, 10_104L);
        cursors.Should().Equal(10_100L);
        _projection.Verify(
            p => p.GetProjectedTreatmentsAsync(It.IsAny<long?>(), It.IsAny<long?>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task QueryAsync_SrvModifiedOrderedPage_ResumesAcrossPagesWithoutLosingARecord()
    {
        var all = Enumerable.Range(1, 1200)
            .Select(i => new Treatment { Id = i.ToString(), Mills = 5000 - i, SrvModified = 10_000 + i })
            .ToList();
        ServeModifiedSince(all);

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Count = 1000,
            Skip = 100,
            ReverseResults = true,
            SortField = ReadSortField.SrvModified,
        });

        result.Select(t => t.SrvModified).Should().Equal(Enumerable.Range(101, 1000).Select(i => (long?)(10_000 + i)));
    }

    [Fact]
    public async Task QueryAsync_SrvModifiedAfterACursor_LeavesOutDeletedAndOrdersByTimeWhenAsked()
    {
        var all = new List<Treatment>
        {
            new() { Id = "a", Mills = 3000, SrvModified = 10_001 },
            new() { Id = "b", Mills = 1000, SrvModified = 10_002 },
            new() { Id = "gone", Mills = 2000, SrvModified = 10_003, IsValid = false },
        };
        ServeModifiedSince(all);

        var result = await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[srvModified][$gte]=10001",
            Count = 10,
            ReverseResults = true,
        });

        result.Select(t => t.Id).Should().Equal("b", "a");
    }

    [Fact]
    public async Task QueryAsync_SrvModifiedBesideAnotherFilter_KeepsTheTimeOrderedRead()
    {
        StoreHoldingTreatmentsAt(1000, 2000);

        await _service.QueryAsync(new TreatmentQuery
        {
            Find = "find[srvModified][$gt]=10100&find[eventType]=Note",
            Count = 2,
            ReverseResults = true,
            SortField = ReadSortField.SrvModified,
        });

        _projection.Verify(
            p => p.GetProjectedTreatmentsModifiedSinceAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task QueryAsync_ReverseResultsWithASmallLimit_ReturnsTheOldestTreatments()
    {
        StoreHoldingTreatmentsAt(1000, 2000, 3000, 4000, 5000);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 2, ReverseResults = true });

        result.Select(t => t.Mills).Should().Equal(1000, 2000);
    }

    [Fact]
    public async Task QueryAsync_ReverseResultsWithSkip_PagesFromTheOldestEnd()
    {
        StoreHoldingTreatmentsAt(1000, 2000, 3000, 4000, 5000);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 2, Skip = 2, ReverseResults = true });

        result.Select(t => t.Mills).Should().Equal(3000, 4000);
    }

    [Fact]
    public async Task QueryAsync_DescendingWithASmallLimit_ReturnsTheNewestTreatmentsNewestFirst()
    {
        StoreHoldingTreatmentsAt(1000, 2000, 3000, 4000, 5000);

        var result = await _service.QueryAsync(new TreatmentQuery { Count = 2 });

        result.Select(t => t.Mills).Should().Equal(5000, 4000);
    }

    [Fact]
    public async Task GetByIdAsync_WithGuid_SearchesV4Repos()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = id, Timestamp = timestamp };
        var projected = new List<Treatment> { new() { Id = id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.Is<IReadOnlyCollection<long>>(at => at.SequenceEqual(new[] { 1000L })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(id.ToString());

        result.Should().NotBeNull();
        result!.Id.Should().Be(id.ToString());
    }

    [Fact]
    public async Task GetByIdAsync_WithLegacyId_SearchesByLegacyId()
    {
        var legacyId = "abc123";
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var bolus = new Bolus { Id = Guid.NewGuid(), Timestamp = timestamp, LegacyId = legacyId };
        var projected = new List<Treatment> { new() { Id = bolus.Id.ToString(), Mills = 1000 } };

        _bolusRepo.Setup(r => r.GetByLegacyIdAsync(legacyId, It.IsAny<CancellationToken>())).ReturnsAsync(bolus);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.Is<IReadOnlyCollection<long>>(at => at.SequenceEqual(new[] { 1000L })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(projected);

        var result = await _service.GetByIdAsync(legacyId);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_ReturnsNull()
    {
        _bolusRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Bolus?)null);
        _carbIntakeRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((CarbIntake?)null);
        _bgCheckRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BGCheck?)null);
        _noteRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Note?)null);
        _deviceEventRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((DeviceEvent?)null);
        _bolusCalcRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((BolusCalculation?)null);
        _tempBasalRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((TempBasal?)null);

        var result = await _service.GetByIdAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_DecomposesEachTreatment()
    {
        var treatment = new Treatment { Id = "t1", Mills = 1000, EventType = "Note", Notes = "test" };
        var decompositionResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        decompositionResult.CreatedRecords.Add(new Note
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime
        });

        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decompositionResult);

        var result = await _service.CreateAsync([treatment], WriteOrigin.Live);

        result.Should().HaveCount(1);
        _decomposer.Verify(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenDecompositionCanceled_StopsTheBatchCarryingWhatWasStored()
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var canceled = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };
        var next = new Treatment { Id = "t3", Mills = 3000, EventType = "Note" };
        DecomposeAll(writes: true);
        FailOn(canceled, new OperationCanceledException());

        var act = () => _service.CreateAsync([first, canceled, next], WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.InnerException.Should().BeOfType<OperationCanceledException>();
        failed.Written.Select(t => t.Id).Should().Equal("t1");
        NeverDecomposed(next);
    }

    [Theory]
    [MemberData(nameof(TransientFailures))]
    public async Task CreateAsync_WhenATreatmentFailsTransiently_StopsTheBatchAndCarriesWhatWasStored(Exception failure)
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var bad = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };
        var last = new Treatment { Id = "t3", Mills = 3000, EventType = "Note" };
        DecomposeAll(skippedDeleted: 1, writes: true);
        FailOn(bad, failure);

        var act = () => _service.CreateAsync([first, bad, last], WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.FailedIndex.Should().Be(1);
        failed.InnerException.Should().BeSameAs(failure);
        failed.Written.Select(t => t.Id).Should().Equal("t1");
        failed.Written.SkippedDeleted.Should().Be(1);
        failed.Message.Should().Contain("1 treatment(s) were stored");
        NeverDecomposed(last);
    }

    [Fact]
    public async Task CreateAsync_WhenTheBatchStops_CarriesItsRefusalsAndNamesNoRefusedTreatment()
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var refused = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };
        var failing = new Treatment { Id = "t3", Mills = 3000, EventType = "Note" };
        DecomposeAll(writes: true);
        FailOn(refused, new TreatmentMappingException(new FormatException("bad date")));
        FailOn(failing, new TimeoutException());

        var act = () => _service.CreateAsync([first, refused, failing], WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.Written.Rejected.Should().ContainSingle().Which.Index.Should().Be(1);
        failed.FailedIndex.Should().Be(2, "a treatment refused for its data stores nothing by design");
        failed.Written.Select(t => t.Id).Should().Equal("t1");
    }

    [Fact]
    public async Task CreateAsync_WhenTheBatchStopsAfterEveryTreatmentStored_NamesNone()
    {
        _decomposer
            .Setup(d => d.DecomposeBatchAsync(
                It.IsAny<IReadOnlyList<Treatment>>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>(),
                It.IsAny<TreatmentDecomposition?>()))
            .Returns((IReadOnlyList<Treatment> treatments, WriteOrigin _, CancellationToken _, TreatmentDecomposition? into) =>
            {
                var groups = treatments.Select(_ => Guid.CreateVersion7()).ToList();
                into!.Begin(groups);
                into.CreatedRecords.AddRange(groups.Select(g => new Note { Id = Guid.CreateVersion7(), CorrelationId = g }));
                throw new TimeoutException("the span write timed out");
            });

        var act = () => _service.CreateAsync(
            [new Treatment { Id = "t1", Mills = 1000, EventType = "Note" }, new Treatment { Id = "t2", Mills = 2000, EventType = "Note" }],
            WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.FailedIndex.Should().BeNull();
        failed.Written.Select(t => t.Id).Should().Equal("t1", "t2");
        failed.Message.Should().Contain("no treatment left to store").And.Contain("2 treatment(s) were stored").And.Contain("0 refused");
    }

    public static TheoryData<Exception> TransientFailures() => new()
    {
        new InvalidOperationException("connection broken"),
        new TimeoutException(),
        new DbUpdateException("unique", new PostgresException("duplicate key", "ERROR", "ERROR", "23505")),
        new DbUpdateException("network", new InvalidOperationException("connection reset")),
        new DbUpdateException("our bug", new PostgresException("null value", "ERROR", "ERROR", "23502")),
        new DbUpdateException("our bug", new PostgresException("invalid input syntax", "ERROR", "ERROR", "22P02")),
    };

    [Theory]
    [MemberData(nameof(DeterministicFailures))]
    public async Task CreateAsync_WhenATreatmentCanNeverBeStored_RefusesItAndStoresTheRest(Exception failure, string reason)
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var bad = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };
        var last = new Treatment { Id = "t3", Mills = 3000, EventType = "Note" };
        DecomposeAll();
        FailOn(bad, failure);

        var written = await _service.CreateAsync([first, bad, last], WriteOrigin.Live);

        written.Select(t => t.Id).Should().Equal("t1", "t3");
        written.Rejected.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new BulkWriteRejection<Treatment>(1, bad, reason));
    }

    public static TheoryData<Exception, string> DeterministicFailures() => new()
    {
        {
            new DbUpdateException("too long", new PostgresException("value too long for type character varying(255)", "ERROR", "ERROR", "22001")),
            "A text value is longer than Nocturne can store; parts of the treatment may be stored"
        },
        {
            new DbUpdateException("out of range", new PostgresException("date/time field value out of range", "ERROR", "ERROR", "22008")),
            "A date or time is invalid or outside the range Nocturne can store; parts of the treatment may be stored"
        },
        {
            new TreatmentMappingException(new FormatException("bad date")),
            "A field of the treatment has a value Nocturne cannot read; parts of the treatment may be stored"
        },
    };

    [Fact]
    public async Task CreateAsync_WhenAnArgumentErrorComesFromOutsideTheMappingStep_DoesNotRefuseTheTreatment()
    {
        var bad = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        DecomposeAll();
        FailOn(bad, new ArgumentException("resolver bug"));

        var act = () => _service.CreateAsync([bad], WriteOrigin.Live);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CreateAsync_ReportsEachStoredTreatmentAsItLands()
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var bad = new Treatment { Id = "t2", Mills = 2000, EventType = "Note" };
        var note = new Note { Id = Guid.CreateVersion7(), LegacyId = "t1" };
        var written = new DecompositionResult();
        written.CreatedRecords.Add(note);
        _decomposer
            .Setup(d => d.DecomposeAsync(first, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(written);
        FailOn(bad, new NotSupportedException());
        var stored = new List<StoredTreatment>();

        var act = () => _service.CreateAsync([first, bad], WriteOrigin.Live, CancellationToken.None, stored);

        await act.Should().ThrowAsync<NotSupportedException>();
        stored.Select(t => t.Created.Id).Should().Equal(note.Id.ToString());
    }

    [Fact]
    public async Task CreateAsync_DoesNotReportATreatmentTheUserDeletedAsStored()
    {
        DecomposeAll(skippedDeleted: 1);
        var stored = new List<StoredTreatment>();

        var created = await _service.CreateAsync(
            [new Treatment { Id = "t1", Mills = 1000, EventType = "Note" }], WriteOrigin.Live, CancellationToken.None, stored);

        created.Should().ContainSingle();
        created.SkippedDeleted.Should().Be(1);
        stored.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_DecomposesUnderTheGivenOrigin()
    {
        DecomposeAll();

        await _service.CreateAsync([new Treatment { Id = "t1", Mills = 1000, EventType = "Note" }], WriteOrigin.Backfill);

        _decomposer.Verify(d => d.DecomposeAsync(It.IsAny<Treatment>(), WriteOrigin.Backfill, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateAsync_WhenAStoredTreatmentCannotBeReadBack_CarriesItAsStored()
    {
        var first = new Treatment { Id = "t1", Mills = 1000, EventType = "Note" };
        var span = new Treatment { Id = "t2", Mills = 2000, EventType = "Temporary Target" };
        var last = new Treatment { Id = "t3", Mills = 3000, EventType = "Note" };
        DecomposeAll();
        var spanResult = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        spanResult.CreatedRecords.Add(new StateSpan { OriginalId = "t2", Category = StateSpanCategory.TemporaryTarget });
        _decomposer
            .Setup(d => d.DecomposeAsync(span, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(spanResult);
        _projection
            .Setup(p => p.GetProjectedStateSpanTreatmentAsync("t2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());

        var act = () => _service.CreateAsync([first, span, last], WriteOrigin.Live);

        var failed = (await act.Should().ThrowAsync<TreatmentBatchFailedException>()).Which;
        failed.Written.Select(t => t.Id).Should().Equal("t1", "t2");
        failed.Message.Should().Contain("was stored but could not be read back");
        failed.FailedIndex.Should().Be(1);
    }

    /// <param name="writes">Whether each treatment writes a record, so a batch that fails after it carries it as stored.</param>
    private void DecomposeAll(int skippedDeleted = 0, bool writes = false) =>
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var stored = new DecompositionResult { CorrelationId = Guid.NewGuid(), SkippedDeleted = skippedDeleted };
                if (writes)
                    stored.CreatedRecords.Add(new Note { Id = Guid.CreateVersion7(), CorrelationId = stored.CorrelationId });
                return stored;
            });

    private void FailOn(Treatment treatment, Exception failure) =>
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

    private void NeverDecomposed(Treatment treatment) =>
        _decomposer.Verify(
            d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);

    [Fact]
    public async Task DeleteAsync_ByStoredLegacyId_DeletesEverySiblingThroughThePipeline()
    {
        _bolusRepo
            .Setup(r => r.GetByLegacyIdAsync("t1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Bolus { Id = Guid.CreateVersion7(), LegacyId = "t1" });
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var result = await _service.DeleteAsync("t1");

        result.Should().NotBeNull();
        _decomposer.Verify(p => p.DeleteByLegacyIdAsync("t1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_NothingStoredUnderTheId_DeletesNothing()
    {
        var result = await _service.DeleteAsync("t1");

        result.Should().BeNull();
        _decomposer.Verify(
            p => p.DeleteByLegacyIdAsync(It.IsAny<string>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ReturnsTheIdReadsServeForTheStoredRecord()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var treatment = new Treatment { Id = syncIdentifier, Mills = 1000, EventType = "Carb Correction", Carbs = 20 };
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(carb);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment], WriteOrigin.Live);

        created.Should().ContainSingle().Which.Id.Should().Be(carb.Id.ToString());
        System.Text.Json.JsonSerializer.SerializeToElement(created[0]).GetProperty("_id").GetString()
            .Should().Be(MongoObjectId.FromGuid(carb.Id));
    }

    [Fact]
    public async Task CreateAsync_MealBolus_ReturnsTheBolusIdTheMealIsReadBackUnder()
    {
        var treatment = new Treatment { Id = "meal-1", Mills = 1000, EventType = "Meal Bolus", Insulin = 2, Carbs = 30, Notes = "lunch" };
        var bolus = new Bolus { Id = Guid.CreateVersion7(), LegacyId = "meal-1" };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(new Note { Id = Guid.CreateVersion7(), LegacyId = "meal-1" });
        result.CreatedRecords.Add(new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = "meal-1" });
        result.UpdatedRecords.Add(bolus);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment], WriteOrigin.Live);

        created.Should().ContainSingle().Which.Id.Should().Be(bolus.Id.ToString());
    }

    [Fact]
    public async Task CreateAsync_NothingProjectedWritten_KeepsTheClientId()
    {
        var treatment = new Treatment { Id = "override-1", Mills = 1000, EventType = "Temporary Override" };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(new StateSpan { Id = Guid.CreateVersion7().ToString(), OriginalId = "override-1" });
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);

        var created = await _service.CreateAsync([treatment], WriteOrigin.Live);

        created.Should().ContainSingle().Which.Id.Should().Be("override-1");
    }

    private readonly List<string?> _decomposedIds = [];

    private Treatment CarbsDecomposedUnder(string id)
    {
        var treatment = new Treatment { Id = id, Mills = 1000, EventType = "Carb Correction", Carbs = 20 };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = id });
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .Callback((Treatment decomposed, WriteOrigin _, CancellationToken _) => _decomposedIds.Add(decomposed.Id))
            .ReturnsAsync(result);
        return treatment;
    }

    [Fact]
    public async Task CreateAsync_FreshIdShapedLikeAServedOne_KeepsItWithoutScanningLegacyIdHashes()
    {
        var id = MongoObjectId.FromGuid(Guid.CreateVersion7());
        var treatment = CarbsDecomposedUnder(id);

        await _service.CreateAsync([treatment], WriteOrigin.Live);

        _decomposedIds.Should().Equal(id);
        _carbIntakeRepo.Verify(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        _bolusRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _bgCheckRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _noteRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _deviceEventRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _bolusCalcRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _tempBasalRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_ObjectIdNoServedIdCouldBe_LooksNothingUp()
    {
        var treatment = CarbsDecomposedUnder(MongoObjectId.NewObjectId());

        await _service.CreateAsync([treatment], WriteOrigin.Live);

        _carbIntakeRepo.Verify(r => r.GetByLegacyIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _carbIntakeRepo.Verify(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateAsync_IdServedForAStoredRecord_DecomposesUnderItsLegacyId()
    {
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = "loop-sync-id" };
        var served = MongoObjectId.FromGuid(carb.Id);
        _carbIntakeRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(carb);
        var treatment = CarbsDecomposedUnder(served);

        await _service.CreateAsync([treatment], WriteOrigin.Live);

        _decomposedIds.Should().Equal("loop-sync-id");
    }

    [Fact]
    public async Task GetByIdAsync_GuidStoredOnlyAsALegacyId_ResolvesIt()
    {
        var clientId = Guid.NewGuid().ToString();
        var carb = new CarbIntake
        {
            Id = Guid.CreateVersion7(),
            LegacyId = clientId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime,
        };
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(clientId, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.Is<IReadOnlyCollection<long>>(at => at.SequenceEqual(new[] { 1000L })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = carb.Id.ToString(), Mills = 1000 }]);

        var result = await _service.GetByIdAsync(clientId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(carb.Id.ToString());
    }

    [Fact]
    public async Task GetByIdAsync_LegacyIdOfATreatmentWithNotes_ResolvesTheRecordTheCreateReturned()
    {
        var treatment = new Treatment { Id = "site-1", Mills = 1000, EventType = "Site Change", Notes = "left arm" };
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1000).UtcDateTime;
        var deviceEvent = new DeviceEvent { Id = Guid.CreateVersion7(), LegacyId = "site-1", Timestamp = timestamp };
        var note = new Note { Id = Guid.CreateVersion7(), LegacyId = "site-1", Timestamp = timestamp };
        var result = new DecompositionResult { CorrelationId = Guid.NewGuid() };
        result.CreatedRecords.Add(note);
        result.CreatedRecords.Add(deviceEvent);
        _decomposer
            .Setup(d => d.DecomposeAsync(treatment, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        _deviceEventRepo.Setup(r => r.GetByLegacyIdAsync("site-1", It.IsAny<CancellationToken>())).ReturnsAsync(deviceEvent);
        _noteRepo.Setup(r => r.GetByLegacyIdAsync("site-1", It.IsAny<CancellationToken>())).ReturnsAsync(note);
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.Is<IReadOnlyCollection<long>>(at => at.SequenceEqual(new[] { 1000L })), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new Treatment { Id = note.Id.ToString(), Mills = 1000 },
                new Treatment { Id = deviceEvent.Id.ToString(), Mills = 1000 },
            ]);

        var created = await _service.CreateAsync([treatment], WriteOrigin.Live);
        var read = await _service.GetByIdAsync("site-1");

        created.Should().ContainSingle().Which.Id.Should().Be(deviceEvent.Id.ToString());
        read!.Id.Should().Be(deviceEvent.Id.ToString());
    }

    [Fact]
    public async Task DeleteAsync_IdAnOlderCreateEchoed_DeletesTheRecordStoredUnderTheClientId()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        _carbIntakeRepo
            .Setup(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier });
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync(syncIdentifier, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var result = await _service.DeleteAsync(echoed);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetForUpdateAsync_IdAnOlderCreateEchoed_KeysTheTreatmentToTheStoredClientId()
    {
        const string syncIdentifier = "a1b2c3d4e5f60718293a4b5c6d7e8f90aa";
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        var bolus = new Bolus { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        _bolusRepo
            .Setup(r => r.GetByLegacyIdHashAsync(echoed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolus);
        ProjectAs(bolus.Id);

        var existing = await _service.GetForUpdateAsync(echoed);

        existing!.Id.Should().Be(syncIdentifier);
    }

    [Fact]
    public async Task DeleteAsync_ExactLegacyIdInOneTable_WinsOverAPrefixMatchInAnother()
    {
        var prefixed = new Bolus { Id = Guid.CreateVersion7(), LegacyId = "bolus-legacy" };
        var wireId = MongoObjectId.FromGuid(prefixed.Id);
        _bolusRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prefixed);
        _noteRepo
            .Setup(r => r.GetByLegacyIdAsync(wireId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Note { Id = Guid.CreateVersion7(), LegacyId = wireId });
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync(wireId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        await _service.DeleteAsync(wireId);

        _decomposer.Verify(
            p => p.DeleteByLegacyIdAsync("bolus-legacy", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteAsync_ByDerivedObjectId_DeletesViaLegacyIdToRemoveSiblings()
    {
        // A meal bolus + its carb share one LegacyId; deleting by the derived wire ObjectId must
        // route through DeleteByLegacyId (removes both) rather than the single-row range delete
        // (which would orphan the carb into a phantom correction).
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var bolus = new Bolus { Id = uuid, LegacyId = "syn-meal-1" };

        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync(wireId, It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // the ObjectId is not a stored LegacyId
        _bolusRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(bolus);
        _decomposer
            .Setup(p => p.DeleteByLegacyIdAsync("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(2); // both siblings

        var result = await _service.DeleteAsync(wireId);

        result.Should().NotBeNull();
        _decomposer.Verify(
            p => p.DeleteByLegacyIdAsync("syn-meal-1", It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetForUpdateAsync_BackfillsNullLegacyId_SoUpdateUpsertsInPlace()
    {
        // A native V4 row (LegacyId == null) resolved by a derived ObjectId must be backfilled with
        // that ObjectId so the decomposer upserts it in place instead of inserting a duplicate.
        var uuid = Guid.CreateVersion7();
        var wireId = MongoObjectId.FromGuid(uuid);
        var note = new Note { Id = uuid, LegacyId = null };

        _noteRepo
            .Setup(r => r.GetByGuidRangeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(note);
        ProjectAs(uuid);

        var existing = await _service.GetForUpdateAsync(wireId);

        existing!.Id.Should().Be(wireId);
        // The backfill goes through IV4Repository<Note>.UpdateAsync (the base slot the generic
        // helper is typed against), which INoteRepository new-shadows: verify the base slot.
        _noteRepo.As<IV4Repository<Note>>().Verify(
            r => r.UpdateAsync(uuid, It.Is<Note>(n => n.LegacyId == wireId), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void ProjectAs(Guid recordId) =>
        _projection
            .Setup(p => p.GetProjectedTreatmentsAtAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Treatment { Id = recordId.ToString() }]);

    private void VerifyOneHashScanPerTable()
    {
        _bolusRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _bgCheckRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _noteRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _deviceEventRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _bolusCalcRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _tempBasalRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.DeleteAsync("0123456789abcdef01234567")).Should().BeNull();

        VerifyOneHashScanPerTable();
    }

    [Fact]
    public async Task UpdateAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.UpdateAsync("0123456789abcdef01234567", new Treatment { EventType = "Note" })).Should().BeNull();

        VerifyOneHashScanPerTable();
        _decomposer.Verify(d => d.DecomposeAsync(
            It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetForUpdateAsync_UnknownObjectId_ScansEachTableOnce()
    {
        (await _service.GetForUpdateAsync("0123456789abcdef01234567")).Should().BeNull();

        VerifyOneHashScanPerTable();
    }

    [Fact]
    public async Task UpdateAsync_ByAnEchoedId_ReadsTheResultBackByTheUpsertKey()
    {
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        var echoed = MongoObjectId.Coerce(syncIdentifier)!;
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = syncIdentifier };
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(syncIdentifier, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        ProjectAs(carb.Id);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecompositionResult());

        var updated = await _service.UpdateAsync(echoed, new Treatment { EventType = "Carb Correction", Carbs = 25 });

        updated!.Id.Should().Be(carb.Id.ToString());
        _decomposer.Verify(d => d.DecomposeAsync(
            It.Is<Treatment>(t => t.Id == syncIdentifier && t.Carbs == 25),
            It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdUuidPrefixAsync(echoed, It.IsAny<CancellationToken>()), Times.Once);
        _carbIntakeRepo.Verify(r => r.GetByLegacyIdHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateAsync_WhenTheStoredTreatmentFailsToDecompose_Propagates(bool stateSpan)
    {
        const string id = "69F15FD2-8075-4DEB-AEA3-4352F455840D";
        if (stateSpan)
        {
            _projection
                .Setup(p => p.GetProjectedStateSpanTreatmentAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Treatment { Id = "span-1" });
            _projection
                .Setup(p => p.GetStateSpanTreatmentIdAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(id);
        }
        else
        {
            var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = id };
            _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
            ProjectAs(carb.Id);
        }

        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DbUpdateException("store down"));

        var update = () => _service.UpdateAsync(id, new Treatment { EventType = "Carb Correction", Carbs = 25 });

        await update.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task UpdateAsync_WhenTheCallerCancels_Propagates()
    {
        const string id = "69F15FD2-8075-4DEB-AEA3-4352F455840D";
        var carb = new CarbIntake { Id = Guid.CreateVersion7(), LegacyId = id };
        _carbIntakeRepo.Setup(r => r.GetByLegacyIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(carb);
        ProjectAs(carb.Id);
        _decomposer
            .Setup(d => d.DecomposeAsync(It.IsAny<Treatment>(), It.IsAny<WriteOrigin>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var update = () => _service.UpdateAsync(id, new Treatment { EventType = "Carb Correction" });

        await update.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IsDeletedByUserAsync_AnyTreatmentTableHoldingATombstone_IsTrue()
    {
        _noteRepo.Setup(r => r.IsDeletedByUserAsync("t1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        (await _service.IsDeletedByUserAsync("t1")).Should().BeTrue();
        (await _service.IsDeletedByUserAsync("t2")).Should().BeFalse();
    }
}
