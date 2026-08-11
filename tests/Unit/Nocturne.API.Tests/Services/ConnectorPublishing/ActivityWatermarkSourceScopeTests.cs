using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.ConnectorPublishing;
using Nocturne.API.Services.Glucose;
using Nocturne.API.Services.Health;
using Nocturne.API.Services.Realtime;
using Nocturne.API.Services.V4;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Connectors;
using Nocturne.Core.Contracts.Events;
using Nocturne.Core.Contracts.Identity;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.Profiles;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.Sleep;
using Nocturne.Core.Contracts.Treatments;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Repositories;
using Xunit;

namespace Nocturne.API.Tests.Services.ConnectorPublishing;

/// <summary>
/// Two connectors publish activity into the same tenant, and each must resolve its catch-up window
/// from its own newest record. Runs the real publish and read paths (decomposer, state spans, heart
/// rates, step counts) over an in-memory database rather than mocks, so a filter that never matches
/// because nothing stamped the source fails here.
/// </summary>
[Trait("Category", "Unit")]
public class ActivityWatermarkSourceScopeTests : IDisposable
{
    private const string SourceA = "nightscout-connector";
    private const string SourceB = "glooko-connector";
    private const string SourceMirror = "nocturne-remote-connector";

    private static readonly DateTime EarlyJune = new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MidJune = new(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LateJune = new(2026, 6, 28, 8, 0, 0, DateTimeKind.Utc);

    private readonly NocturneDbContext _context;
    private readonly StateSpanService _stateSpanService;
    private readonly MetadataPublisher _publisher;

    public ActivityWatermarkSourceScopeTests()
    {
        _context = new NocturneDbContext(
            new DbContextOptionsBuilder<NocturneDbContext>()
                .UseInMemoryDatabase($"activity-watermark-{Guid.NewGuid():N}")
                .Options)
        {
            TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        };

        var processing = new Mock<IDocumentProcessingService>();
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<Activity>>()))
            .Returns((IEnumerable<Activity> docs) => docs);
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<HeartRate>>()))
            .Returns((IEnumerable<HeartRate> docs) => docs);
        processing
            .Setup(p => p.ProcessDocuments(It.IsAny<IEnumerable<StepCount>>()))
            .Returns((IEnumerable<StepCount> docs) => docs);

        var stateSpanRepository = new StateSpanRepository(
            _context,
            Mock.Of<IDeduplicationService>(),
            Mock.Of<IAuditContext>(),
            NullLogger<StateSpanRepository>.Instance);

        _stateSpanService = new StateSpanService(
            stateSpanRepository, NullLogger<StateSpanService>.Instance);

        var heartRateService = new HeartRateService(
            _context, processing.Object, Mock.Of<ISignalRBroadcastService>(),
            NullLogger<HeartRateService>.Instance);

        var stepCountService = new StepCountService(
            _context, processing.Object, Mock.Of<ISignalRBroadcastService>(),
            NullLogger<StepCountService>.Instance);

        var activityService = new ActivityService(
            _stateSpanService,
            Mock.Of<ISleepService>(),
            processing.Object,
            Mock.Of<ISignalRBroadcastService>(),
            Mock.Of<IDataEventSink<Activity>>(),
            new ActivityDecomposer(
                _context, stateSpanRepository, NullLogger<ActivityDecomposer>.Instance),
            heartRateService,
            stepCountService,
            NullLogger<ActivityService>.Instance);

        _publisher = new MetadataPublisher(
            Mock.Of<IProfileWriteService>(),
            Mock.Of<IFoodService>(),
            Mock.Of<IConnectorFoodEntryService>(),
            activityService,
            _stateSpanService,
            Mock.Of<ISystemEventRepository>(),
            Mock.Of<INoteRepository>(),
            Mock.Of<ITenantOwnerResolver>(),
            Mock.Of<ITenantAccessor>(),
            _context,
            NullLogger<MetadataPublisher>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Activity Exercise(DateTime at) => new()
    {
        Id = $"exercise-{at:yyyyMMddHHmm}",
        Type = "exercise",
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        EnteredBy = "some-uploader",
    };

    private static Activity HeartRate(DateTime at) => new()
    {
        Id = $"hr-{at:yyyyMMddHHmm}",
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        AdditionalProperties = new Dictionary<string, object> { ["bpm"] = 64 },
    };

    private static Activity Steps(DateTime at) => new()
    {
        Id = $"steps-{at:yyyyMMddHHmm}",
        Mills = new DateTimeOffset(at).ToUnixTimeMilliseconds(),
        AdditionalProperties = new Dictionary<string, object> { ["metric"] = 900 },
    };

    [Fact]
    public async Task Each_connector_resolves_its_own_latest_state_span()
    {
        await _publisher.PublishActivityAsync([Exercise(EarlyJune)], SourceA, WriteOrigin.Live);
        await _publisher.PublishActivityAsync([Exercise(LateJune)], SourceB, WriteOrigin.Live);

        // A tenant-global latest hands SourceA LateJune, so it concludes it is caught up and skips
        // everything in between.
        (await _publisher.GetLatestActivityTimestampAsync(SourceA)).Should().Be(EarlyJune);
        (await _publisher.GetLatestActivityTimestampAsync(SourceB)).Should().Be(LateJune);
    }

    [Fact]
    public async Task Each_connector_resolves_its_own_latest_heart_rate()
    {
        await _publisher.PublishActivityAsync([HeartRate(EarlyJune)], SourceA, WriteOrigin.Live);
        await _publisher.PublishActivityAsync([HeartRate(LateJune)], SourceB, WriteOrigin.Live);

        (await _publisher.GetLatestActivityTimestampAsync(SourceA)).Should().Be(EarlyJune);
        (await _publisher.GetLatestActivityTimestampAsync(SourceB)).Should().Be(LateJune);
    }

    [Fact]
    public async Task Each_connector_resolves_its_own_latest_step_count()
    {
        await _publisher.PublishActivityAsync([Steps(EarlyJune)], SourceA, WriteOrigin.Live);
        await _publisher.PublishActivityAsync([Steps(LateJune)], SourceB, WriteOrigin.Live);

        (await _publisher.GetLatestActivityTimestampAsync(SourceA)).Should().Be(EarlyJune);
        (await _publisher.GetLatestActivityTimestampAsync(SourceB)).Should().Be(LateJune);
    }

    [Fact]
    public async Task Watermark_is_the_newest_across_a_sources_own_decomposed_destinations()
    {
        await _publisher.PublishActivityAsync(
            [Exercise(EarlyJune), HeartRate(MidJune), Steps(EarlyJune)], SourceA, WriteOrigin.Live);
        await _publisher.PublishActivityAsync([Steps(LateJune)], SourceB, WriteOrigin.Live);

        (await _publisher.GetLatestActivityTimestampAsync(SourceA)).Should().Be(MidJune);
    }

    [Fact]
    public async Task Source_that_has_never_written_activity_has_no_watermark()
    {
        await _publisher.PublishActivityAsync(
            [Exercise(LateJune), HeartRate(LateJune), Steps(LateJune)], SourceA, WriteOrigin.Live);

        // Null is the "no prior data" signal the connector turns into an initial backfill; a
        // borrowed timestamp from SourceA would silently strand SourceB's history.
        (await _publisher.GetLatestActivityTimestampAsync(SourceB)).Should().BeNull();
    }

    [Fact]
    public async Task Published_activity_is_attributed_to_the_publishing_connector()
    {
        var published = await _publisher.PublishActivityAsync(
            [Exercise(MidJune), HeartRate(MidJune), Steps(MidJune)], SourceA, WriteOrigin.Live);
        published.Should().BeTrue();

        // The connector source has to reach every destination column, not the payload's EnteredBy.
        (await _context.StateSpans.AsNoTracking().Select(s => s.Source).ToListAsync())
            .Should().Equal(SourceA);
        (await _context.HeartRates.AsNoTracking().Select(h => h.DataSource).ToListAsync())
            .Should().Equal(SourceA);
        (await _context.StepCounts.AsNoTracking().Select(s => s.DataSource).ToListAsync())
            .Should().Equal(SourceA);
    }

    [Fact]
    public async Task Attributing_an_activity_to_its_connector_does_not_lose_the_uploader_name()
    {
        // The state span's Source column now carries the connector, so EnteredBy — which the v1
        // activity read used to recover from Source — has to round-trip through metadata instead.
        await _publisher.PublishActivityAsync([Exercise(MidJune)], SourceA, WriteOrigin.Live);

        var stored = await _stateSpanService.GetActivitiesAsync(cancellationToken: default);

        stored.Should().ContainSingle().Which.EnteredBy.Should().Be("some-uploader");
    }

    [Fact]
    public async Task State_spans_are_attributed_to_the_publishing_connector_whatever_they_arrived_with()
    {
        var carriesForeignSource = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = MidJune,
            Source = SourceA,
            OriginalId = "carries-foreign-source",
        };
        var carriesNone = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = EarlyJune,
            OriginalId = "carries-none",
        };
        var carriesBlank = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = EarlyJune,
            Source = "",
            OriginalId = "carries-blank",
        };

        await _publisher.PublishStateSpansAsync(
            [carriesForeignSource, carriesNone, carriesBlank], SourceMirror, WriteOrigin.Live);

        carriesForeignSource.Source.Should().Be(SourceMirror);
        carriesNone.Source.Should().Be(SourceMirror);
        carriesBlank.Source.Should().Be(SourceMirror);

        // A displaced source is stashed, not dropped, so the origin stays recoverable. A span that
        // arrived with no source — null or blank — has nothing worth stashing.
        carriesForeignSource.Metadata.Should().Contain("originSource", SourceA);
        carriesNone.Metadata.Should().BeNull();
        carriesBlank.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task A_producer_that_already_names_itself_stashes_no_origin()
    {
        // Glooko, MyLife and Tandem set their own ConnectorSource, so the overwrite is a no-op
        // and must not litter every span with a metadata key repeating the Source column.
        var ownSource = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = MidJune,
            Source = SourceB,
            OriginalId = "own-source",
        };

        await _publisher.PublishStateSpansAsync([ownSource], SourceB, WriteOrigin.Live);

        ownSource.Source.Should().Be(SourceB);
        ownSource.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task A_mirrored_span_does_not_advance_the_watermark_of_the_connector_it_names()
    {
        // NocturneRemote replays a remote instance's StateSpan records verbatim, Source included.
        // Honouring that Source would file the row under the Nightscout connector and hand it a
        // watermark it never earned, so it would skip the window it still has to sync.
        await _publisher.PublishActivityAsync([Exercise(EarlyJune)], SourceA, WriteOrigin.Live);

        var mirrored = new StateSpan
        {
            Category = StateSpanCategory.Exercise,
            State = "exercise",
            StartTimestamp = LateJune,
            Source = SourceA,
            OriginalId = "mirrored-from-remote",
        };
        await _publisher.PublishStateSpansAsync([mirrored], SourceMirror, WriteOrigin.Live);

        (await _publisher.GetLatestActivityTimestampAsync(SourceA)).Should().Be(EarlyJune);
        (await _publisher.GetLatestActivityTimestampAsync(SourceMirror)).Should().Be(LateJune);
    }
}
